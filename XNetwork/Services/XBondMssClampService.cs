using System.Diagnostics;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class XBondMssClampService
{
    /// <summary>
    /// Checking the clamp rule costs a privileged `sudo iptables -C` fork. The Settings page polls
    /// status on a timer, so the result is cached to keep that from forking once per poll.
    /// </summary>
    private static readonly TimeSpan DefaultStatusCacheDuration = TimeSpan.FromSeconds(15);

    private readonly ILogger<XBondMssClampService> logger;
    private readonly XBondSettings settings;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _statusCacheDuration;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> _runner;
    private readonly Func<bool> _isSupported;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _statusLock = new(1, 1);
    private XBondMssClampStatus? _cachedStatus;
    private DateTimeOffset _statusExpiresAtUtc = DateTimeOffset.MinValue;

    public XBondMssClampService(ILogger<XBondMssClampService> logger, XBondSettings settings)
        : this(logger, settings, null, null, null, null)
    {
    }

    public XBondMssClampService(
        ILogger<XBondMssClampService> logger,
        XBondSettings settings,
        TimeProvider? timeProvider,
        TimeSpan? statusCacheDuration,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>>? runner,
        Func<bool>? isSupported = null)
    {
        this.logger = logger;
        this.settings = settings;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _statusCacheDuration = statusCacheDuration ?? DefaultStatusCacheDuration;
        _runner = runner ?? (static (command, args, ct) => RunCommandAsync(command, args, ct));
        _isSupported = isSupported ?? OperatingSystem.IsLinux;
    }

    /// <summary>Drops the cached status so the next read reflects a change we just made.</summary>
    public void InvalidateStatus() => _statusExpiresAtUtc = DateTimeOffset.MinValue;

    public async Task<XBondMssClampStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (_cachedStatus is not null && now < _statusExpiresAtUtc)
        {
            return _cachedStatus;
        }

        await _statusLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cachedStatus is not null && now < _statusExpiresAtUtc)
            {
                return _cachedStatus;
            }

            var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            _cachedStatus = status;
            _statusExpiresAtUtc = now + _statusCacheDuration;
            return status;
        }
        finally
        {
            _statusLock.Release();
        }
    }

    private async Task<XBondMssClampStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        if (!_isSupported())
        {
            return new XBondMssClampStatus
            {
                IsSupported = false,
                MssValue = settings.MssClampValue,
                TunnelDevice = settings.TunnelDevice,
                Message = "MSS clamp is only available on Linux."
            };
        }

        var check = await RunIptablesAsync("-C", cancellationToken).ConfigureAwait(false);
        return new XBondMssClampStatus
        {
            IsSupported = true,
            IsEnabled = check.ExitCode == 0,
            MssValue = settings.MssClampValue,
            TunnelDevice = settings.TunnelDevice,
            Message = check.ExitCode == 0
                ? "MSS clamp is enabled for uLink."
                : "MSS clamp is disabled for uLink."
        };
    }

    public async Task<XBondMssClampStatus> EnableAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.AllowServiceControl)
        {
            return Error("uLink MSS clamp changes are locked by configuration.");
        }

        if (!await _lock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Error("Another uLink MSS clamp change is already running.");
        }

        try
        {
            var current = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (current.IsEnabled)
            {
                return current;
            }

            var append = await RunIptablesAsync("-A", cancellationToken).ConfigureAwait(false);
            if (append.ExitCode != 0)
            {
                return Error(string.IsNullOrWhiteSpace(append.Output) ? "Unable to enable MSS clamp." : append.Output);
            }

            return await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enable uLink MSS clamp");
            return Error(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<XBondMssClampStatus> DisableAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.AllowServiceControl)
        {
            return Error("uLink MSS clamp changes are locked by configuration.");
        }

        if (!await _lock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Error("Another uLink MSS clamp change is already running.");
        }

        try
        {
            var delete = await RunIptablesAsync("-D", cancellationToken).ConfigureAwait(false);
            if (delete.ExitCode != 0)
            {
                var current = await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
                return current.IsEnabled
                    ? Error(string.IsNullOrWhiteSpace(delete.Output) ? "Unable to disable MSS clamp." : delete.Output)
                    : current;
            }

            return await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to disable uLink MSS clamp");
            return Error(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<XBondMssClampStatus> RefreshStatusAsync(CancellationToken cancellationToken)
    {
        var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        _cachedStatus = status;
        _statusExpiresAtUtc = _timeProvider.GetUtcNow() + _statusCacheDuration;
        return status;
    }

    private async Task<CommandResult> RunIptablesAsync(string action, CancellationToken cancellationToken)
    {
        var args = new List<string>();
        if (settings.UseSudoForServiceManager)
        {
            args.Add("-n");
            args.Add(settings.IptablesCommandPath);
        }

        args.AddRange([
            "-t", "mangle",
            action, "FORWARD",
            "-o", settings.TunnelDevice,
            "-p", "tcp",
            "--tcp-flags", "SYN,RST", "SYN",
            "-j", "TCPMSS",
            "--set-mss", Math.Clamp(settings.MssClampValue, 536, 1460).ToString()
        ]);

        var result = await _runner(
            settings.UseSudoForServiceManager ? settings.SudoPath : settings.IptablesCommandPath,
            args,
            cancellationToken).ConfigureAwait(false);
        return new CommandResult(result.ExitCode, result.Output);
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = string.Join('\n', new[] { await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false) }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()));
        return (process.ExitCode, output);
    }

    private XBondMssClampStatus Error(string error) => new()
    {
        IsSupported = OperatingSystem.IsLinux(),
        IsEnabled = false,
        MssValue = settings.MssClampValue,
        TunnelDevice = settings.TunnelDevice,
        Message = "uLink MSS clamp command failed.",
        Error = error
    };

    private sealed record CommandResult(int ExitCode, string Output);
}
