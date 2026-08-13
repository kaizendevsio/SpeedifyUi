using System.Collections.Concurrent;
using System.Diagnostics;
using XNetwork.Models;

namespace XNetwork.Services;

public enum WifiEnforcementAction
{
    None,
    Disconnect
}

/// <summary>
/// Keeps a Wi-Fi adapter from joining any network while it is disabled in Settings. NetworkManager
/// stores per-device autoconnect as runtime state only, so the setting is re-asserted on a periodic
/// enforcement pass as well as at startup and on save.
/// </summary>
public sealed class WifiControlService : BackgroundService
{
    private static readonly string[] SudoRetryMarkers =
    [
        "not authorized", "insufficient privileges", "permission denied", "access denied"
    ];

    private readonly ILogger<WifiControlService> _logger;
    private readonly WifiControlSettings _settings;
    private readonly WifiControlSettingsStore? _store;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output, string Error)>> _runner;
    private readonly ConcurrentDictionary<string, InterfaceState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    public WifiControlService(
        ILogger<WifiControlService> logger,
        WifiControlSettings settings,
        WifiControlSettingsStore store)
        : this(logger, settings, store, null, null)
    {
    }

    public WifiControlService(
        ILogger<WifiControlService> logger,
        WifiControlSettings settings,
        WifiControlSettingsStore? store,
        TimeProvider? timeProvider,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output, string Error)>>? runner)
    {
        _logger = logger;
        _settings = settings;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _runner = runner ?? RunProcessAsync;
    }

    public bool IsDisabled(string interfaceName) => _settings.IsDisabled(interfaceName);

    public WifiControlStatus GetStatus()
    {
        var names = _settings.DisabledInterfaces.Keys
            .Concat(_state.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WifiControlStatus
        {
            IsSupported = OperatingSystem.IsLinux(),
            Message = OperatingSystem.IsLinux() ? null : "Wi-Fi control is only supported on Linux.",
            Interfaces = names
                .Select(name =>
                {
                    _state.TryGetValue(name, out var state);
                    return new WifiControlInterfaceStatus
                    {
                        InterfaceName = name,
                        IsDisabled = _settings.IsDisabled(name),
                        DeviceState = state?.DeviceState ?? "unknown",
                        LastAppliedUtc = state?.LastAppliedUtc,
                        LastError = state?.LastError,
                        ReassertCount = state?.ReassertCount ?? 0
                    };
                })
                .ToArray()
        };
    }

    public async Task SetDisabledAsync(string interfaceName, bool disabled, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);

        interfaceName = interfaceName.Trim();
        _settings.DisabledInterfaces[interfaceName] = disabled;

        if (_store is not null)
        {
            await _store.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Wi-Fi for {Interface} set to {State} from Settings",
            interfaceName,
            disabled ? "disabled" : "enabled");

        await ApplyAsync(interfaceName, disabled, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-asserts the disabled state for any adapter that drifted back into a connection.</summary>
    public async Task EnforceAsync(CancellationToken cancellationToken)
    {
        var disabledInterfaces = _settings.DisabledInterfaces
            .Where(pair => pair.Value)
            .Select(pair => pair.Key)
            .ToArray();

        if (disabledInterfaces.Length == 0)
        {
            return;
        }

        var status = await _runner(
            "nmcli",
            ["-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device", "status"],
            cancellationToken).ConfigureAwait(false);

        if (status.ExitCode != 0)
        {
            _logger.LogDebug("Could not read device status for Wi-Fi enforcement: {Error}", status.Error.Trim());
            return;
        }

        var devices = WifiService.ParseWifiInterfaces(status.Output);
        foreach (var interfaceName in disabledInterfaces)
        {
            var device = devices.FirstOrDefault(item =>
                string.Equals(item.InterfaceName, interfaceName, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                continue;
            }

            var state = _state.GetOrAdd(interfaceName, _ => new InterfaceState());
            state.DeviceState = device.State;

            if (Evaluate(disabled: true, device.State) != WifiEnforcementAction.Disconnect)
            {
                continue;
            }

            _logger.LogWarning(
                "Wi-Fi adapter {Interface} is {State} while disabled in Settings; disconnecting it again",
                interfaceName,
                device.State);
            state.ReassertCount++;
            await ApplyAsync(interfaceName, disabled: true, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "WifiControlService starting; disabled adapters: {Disabled}",
            _settings.DisabledInterfaces.Count(pair => pair.Value));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnforceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Wi-Fi enforcement pass failed");
            }

            try
            {
                await Task.Delay(_settings.EnforcementInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ApplyAsync(string interfaceName, bool disabled, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() && _runner == RunProcessAsync)
        {
            return;
        }

        var state = _state.GetOrAdd(interfaceName, _ => new InterfaceState());
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var autoconnect = await RunWithSudoFallbackAsync(
                BuildAutoconnectArgs(interfaceName, allowAutoconnect: !disabled),
                cancellationToken).ConfigureAwait(false);

            if (autoconnect.ExitCode != 0)
            {
                state.LastError = DescribeFailure(autoconnect);
                _logger.LogWarning(
                    "Could not set autoconnect for {Interface}: {Error}",
                    interfaceName,
                    state.LastError);
                return;
            }

            if (disabled)
            {
                var disconnect = await RunWithSudoFallbackAsync(
                    BuildDisconnectArgs(interfaceName),
                    cancellationToken).ConfigureAwait(false);

                // A device that is already disconnected reports a non-zero exit; that is not a failure.
                if (disconnect.ExitCode != 0 && !IsAlreadyDisconnected(disconnect))
                {
                    state.LastError = DescribeFailure(disconnect);
                    _logger.LogWarning(
                        "Could not disconnect {Interface}: {Error}",
                        interfaceName,
                        state.LastError);
                    return;
                }
            }

            state.LastError = null;
            state.LastAppliedUtc = _timeProvider.GetUtcNow();
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunWithSudoFallbackAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var result = await _runner("nmcli", args, cancellationToken).ConfigureAwait(false);
        if (!ShouldRetryWithSudo(result.ExitCode, result.Error))
        {
            return result;
        }

        _logger.LogDebug("Retrying nmcli with sudo after authorization failure: {Error}", result.Error.Trim());
        return await _runner("sudo", ["-n", "nmcli", .. args], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decides whether a disabled adapter needs the disable steps re-applied.</summary>
    public static WifiEnforcementAction Evaluate(bool disabled, string? deviceState)
    {
        if (!disabled || string.IsNullOrWhiteSpace(deviceState))
        {
            return WifiEnforcementAction.None;
        }

        var state = deviceState.Trim();
        return state.StartsWith("connected", StringComparison.OrdinalIgnoreCase) ||
               state.StartsWith("connecting", StringComparison.OrdinalIgnoreCase)
            ? WifiEnforcementAction.Disconnect
            : WifiEnforcementAction.None;
    }

    public static IReadOnlyList<string> BuildAutoconnectArgs(string interfaceName, bool allowAutoconnect) =>
        ["device", "set", interfaceName, "autoconnect", allowAutoconnect ? "yes" : "no"];

    public static IReadOnlyList<string> BuildDisconnectArgs(string interfaceName) =>
        ["device", "disconnect", interfaceName];

    public static bool ShouldRetryWithSudo(int exitCode, string? error)
    {
        if (exitCode == 0 || string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return SudoRetryMarkers.Any(marker => error.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAlreadyDisconnected((int ExitCode, string Output, string Error) result)
    {
        var text = $"{result.Output} {result.Error}";
        return text.Contains("not active", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("already disconnected", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeFailure((int ExitCode, string Output, string Error) result)
    {
        var error = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        return string.IsNullOrWhiteSpace(error) ? $"nmcli exited with {result.ExitCode}" : error.Trim();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private sealed class InterfaceState
    {
        public string DeviceState { get; set; } = "unknown";

        public DateTimeOffset? LastAppliedUtc { get; set; }

        public string? LastError { get; set; }

        public int ReassertCount { get; set; }
    }
}
