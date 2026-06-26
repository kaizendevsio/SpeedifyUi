using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed partial class F50ModemRecoveryService(
    F50ModemRecoverySettings settings,
    F50ModemRecoverySettingsStore settingsStore,
    LocalDeviceProxyService proxyService,
    InterfaceMetadataService interfaceMetadataService,
    XBondStatusService xbondStatusService,
    XBondSettings xbondSettings,
    ILogger<F50ModemRecoveryService> logger) : BackgroundService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient = new() { Timeout = RequestTimeout };
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly object _statusLock = new();
    private readonly Dictionary<string, int> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private F50ModemRecoveryStatus _status = BuildInitialStatus(settings);

    public F50ModemRecoverySettings Settings => settings;

    public F50ModemRecoveryStatus GetStatus()
    {
        lock (_statusLock)
        {
            return CloneStatus(_status);
        }
    }

    public async Task SaveSettingsAsync(F50ModemRecoverySettings updated, CancellationToken cancellationToken = default)
    {
        F50ModemRecoverySettingsStore.Apply(updated, settings);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        UpdateStatus(status =>
        {
            status.Enabled = settings.Enabled;
            status.CheckIntervalMinutes = settings.CheckIntervalMinutes;
            status.Message = settings.Enabled
                ? "F50 modem recovery settings saved."
                : "F50 modem recovery is disabled.";
        });
    }

    public async Task<F50ModemRecoveryStatus> RunCheckNowAsync(CancellationToken cancellationToken = default)
    {
        await RunCheckAsync(manual: true, cancellationToken).ConfigureAwait(false);
        return GetStatus();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMinutes(Math.Clamp(settings.CheckIntervalMinutes, 1, 60));
            UpdateStatus(status => status.NextRunAtUtc = DateTimeOffset.UtcNow + delay);

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                if (settings.Enabled)
                {
                    await RunCheckAsync(manual: false, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    UpdateStatus(status =>
                    {
                        status.Enabled = false;
                        status.Message = "F50 modem recovery is disabled.";
                    });
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "F50 modem recovery loop failed");
                UpdateStatus(status =>
                {
                    status.Message = $"F50 modem recovery failed: {ex.Message}";
                    status.IsRunning = false;
                    status.LastCompletedAtUtc = DateTimeOffset.UtcNow;
                });
            }
        }
    }

    private async Task RunCheckAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            UpdateStatus(status =>
            {
                status.Enabled = settings.Enabled;
                status.Message = "F50 modem recovery runs only on Linux.";
                status.IsRunning = false;
            });
            return;
        }

        if (!settings.Enabled && !manual)
        {
            return;
        }

        if (!await _checkLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            UpdateStatus(status => status.Message = "F50 modem recovery check is already running.");
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        UpdateStatus(status =>
        {
            status.Enabled = settings.Enabled;
            status.CheckIntervalMinutes = settings.CheckIntervalMinutes;
            status.IsRunning = true;
            status.LastStartedAtUtc = startedAt;
            status.Message = manual ? "Running manual F50 modem recovery check." : "Running F50 modem recovery check.";
        });

        try
        {
            var entries = proxyService.GetEntries()
                .Where(entry => entry.Enabled && entry.TelemetryEnabled)
                .Where(entry => !string.IsNullOrWhiteSpace(LocalDeviceProxyService.GetTargetHost(entry)))
                .ToArray();
            if (entries.Length == 0)
            {
                UpdateStatus(status =>
                {
                    status.IsRunning = false;
                    status.LastCompletedAtUtc = DateTimeOffset.UtcNow;
                    status.Message = "No enabled F50 telemetry proxy entries are configured.";
                    status.Modems = new List<F50ModemRecoveryEntryStatus>();
                });
                return;
            }

            var xbondStatus = await xbondStatusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var routes = await interfaceMetadataService.GetDefaultGatewayRoutesAsync(cancellationToken).ConfigureAwait(false);
            var routeByGateway = routes
                .GroupBy(route => route.Gateway, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Device, StringComparer.OrdinalIgnoreCase);
            var results = new List<F50ModemRecoveryEntryStatus>();

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await CheckEntryAsync(
                    entry,
                    xbondStatus,
                    routeByGateway,
                    manual,
                    cancellationToken).ConfigureAwait(false));
            }

            var actions = results.Count(result => !string.Equals(result.LastAction, "none", StringComparison.OrdinalIgnoreCase));
            var failures = results.Count(result => !string.IsNullOrWhiteSpace(result.LastError));
            UpdateStatus(status =>
            {
                status.Enabled = settings.Enabled;
                status.CheckIntervalMinutes = settings.CheckIntervalMinutes;
                status.IsRunning = false;
                status.LastCompletedAtUtc = DateTimeOffset.UtcNow;
                status.NextRunAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(settings.CheckIntervalMinutes);
                status.Modems = results;
                status.Message = actions == 0
                    ? "F50 modem recovery check completed; no recovery action was needed."
                    : failures == 0
                        ? $"F50 modem recovery completed with {actions} action(s)."
                        : $"F50 modem recovery completed with {actions} action(s) and {failures} failure(s).";
            });
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private async Task<F50ModemRecoveryEntryStatus> CheckEntryAsync(
        LocalDeviceProxyEntry entry,
        XBondStatus xbondStatus,
        IReadOnlyDictionary<string, string> routeByGateway,
        bool manual,
        CancellationToken cancellationToken)
    {
        var targetHost = LocalDeviceProxyService.GetTargetHost(entry) ?? "";
        var path = FindMatchingPath(xbondStatus, entry, routeByGateway);
        var interfaceName = ResolveInterfaceName(entry, targetHost, routeByGateway, path);
        var modemStatus = await ReadModemStatusAsync(entry, cancellationToken).ConfigureAwait(false);
        var result = BuildEntryStatus(entry, targetHost, interfaceName, path, modemStatus);

        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            result.State = "unknown";
            result.LastResult = "No interface could be matched to this modem.";
            return result;
        }

        if (!manual && _cooldowns.TryGetValue(entry.Id, out var cooldown) && cooldown > 0)
        {
            _cooldowns[entry.Id] = cooldown - 1;
            result.State = "cooldown";
            result.CooldownRoundsRemaining = cooldown;
            result.LastResult = "Skipping this round after a failed recovery attempt.";
            return result;
        }

        var pingOk = await PingThroughInterfaceAsync(interfaceName, cancellationToken).ConfigureAwait(false);
        var issue = DetermineRecoveryIssue(path, modemStatus, pingOk, settings.SevereLossPercent);
        if (issue == RecoveryIssue.None)
        {
            _cooldowns.Remove(entry.Id);
            result.State = "healthy";
            result.LastResult = "Modem and XBond path look usable.";
            return result;
        }

        if (issue == RecoveryIssue.SocketOnly || !settings.UsbResetEnabled)
        {
            result.LastAction = "rebind";
            var rebind = await RebindPathAsync(path?.PathId, interfaceName, cancellationToken).ConfigureAwait(false);
            result.LastResult = rebind.ExitCode == 0
                ? "XBond path socket rebind requested."
                : "XBond path socket rebind failed.";
            result.LastError = rebind.ExitCode == 0 ? null : FirstNonEmpty(rebind.Error, rebind.Output);
            result.State = rebind.ExitCode == 0 ? "rebound" : "failed";
            if (rebind.ExitCode != 0)
            {
                ApplyCooldown(entry.Id, result);
            }

            return result;
        }

        result.LastAction = "usb reset + rebind";
        var resetTarget = ResolveUsbResetTarget(interfaceName);
        if (string.IsNullOrWhiteSpace(resetTarget))
        {
            result.State = "failed";
            result.LastResult = "USB reset target could not be resolved.";
            result.LastError = $"No bus/dev target found for {interfaceName}.";
            ApplyCooldown(entry.Id, result);
            return result;
        }

        var reset = await UsbResetAsync(resetTarget, cancellationToken).ConfigureAwait(false);
        if (reset.ExitCode != 0)
        {
            result.State = "failed";
            result.LastResult = $"USB reset failed for {resetTarget}.";
            result.LastError = FirstNonEmpty(reset.Error, reset.Output);
            ApplyCooldown(entry.Id, result);
            return result;
        }

        var recovered = await WaitForRecoveryAsync(entry, interfaceName, cancellationToken).ConfigureAwait(false);
        var postResetRebind = await RebindPathAsync(path?.PathId, interfaceName, cancellationToken).ConfigureAwait(false);
        if (recovered && postResetRebind.ExitCode == 0)
        {
            _cooldowns.Remove(entry.Id);
            result.State = "recovered";
            result.LastResult = $"USB reset {resetTarget} completed and XBond path was rebound.";
            return result;
        }

        result.State = "failed";
        result.LastResult = recovered
            ? "Modem recovered, but XBond socket rebind failed."
            : "Modem did not recover before the settle timeout.";
        result.LastError = recovered
            ? FirstNonEmpty(postResetRebind.Error, postResetRebind.Output)
            : null;
        ApplyCooldown(entry.Id, result);
        return result;
    }

    private async Task<F50RecoveryProbeResponse?> ReadModemStatusAsync(
        LocalDeviceProxyEntry entry,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(LocalDeviceProxyService.NormalizeTargetUrl(entry.TargetUrl), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);
            var uri = new Uri(baseUri, "/goform/goform_get_cmd_process?isTest=false&cmd=network_type,current_network_type,signalbar,network_provider,operator,ppp_status,modem_main_state,wan_ipaddr&multi_data=1");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Referrer = new Uri(baseUri, "/index.html");
            request.Headers.TryAddWithoutValidation("Origin", $"{baseUri.Scheme}://{baseUri.Host}");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<F50RecoveryProbeResponse>(
                JsonOptions,
                timeoutCts.Token).ConfigureAwait(false);
            return payload is null || !string.IsNullOrWhiteSpace(payload.Error) ? null : payload;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to read F50 recovery status from {Target}", entry.TargetUrl);
            return null;
        }
    }

    private async Task<bool> WaitForRecoveryAsync(
        LocalDeviceProxyEntry entry,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(settings.SettleTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            var modem = await ReadModemStatusAsync(entry, cancellationToken).ConfigureAwait(false);
            var pingOk = await PingThroughInterfaceAsync(interfaceName, cancellationToken).ConfigureAwait(false);
            if (pingOk && HasUsableWan(modem))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> PingThroughInterfaceAsync(string interfaceName, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(
            xbondSettings.PingCommandPath,
            [
                "-I",
                interfaceName,
                "-c",
                settings.PingCount.ToString(CultureInfo.InvariantCulture),
                "-W",
                settings.PingTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                settings.PingTarget
            ],
            Math.Clamp(settings.PingCount * (settings.PingTimeoutSeconds + 1) + 3, 6, 30),
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private async Task<CommandResult> RebindPathAsync(int? pathId, string interfaceName, CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "path-rebind",
            "--socket",
            string.IsNullOrWhiteSpace(xbondSettings.ClientControlSocketPath)
                ? "/run/xbond/client-control.sock"
                : xbondSettings.ClientControlSocketPath,
            "--json"
        };

        if (pathId.HasValue && pathId.Value > 0)
        {
            args.Add("--path-id");
            args.Add(pathId.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            args.Add("--interface");
            args.Add(interfaceName);
        }

        return await RunXBondClientCommandAsync(
            args,
            Math.Clamp(xbondSettings.ServiceCommandTimeoutSeconds, 3, 60),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandResult> UsbResetAsync(string resetTarget, CancellationToken cancellationToken)
    {
        return await RunCommandAsync(
            string.IsNullOrWhiteSpace(xbondSettings.SudoPath) ? "sudo" : xbondSettings.SudoPath,
            ["-n", settings.UsbResetCommandPath, resetTarget],
            Math.Clamp(xbondSettings.ServiceCommandTimeoutSeconds, 3, 60),
            cancellationToken).ConfigureAwait(false);
    }

    private Task<CommandResult> RunXBondClientCommandAsync(
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (xbondSettings.UseSudoForServiceManager && !string.IsNullOrWhiteSpace(xbondSettings.SudoPath))
        {
            return RunCommandAsync(
                xbondSettings.SudoPath,
                ["-n", ResolveSudoClientBinaryPath(), .. arguments],
                timeoutSeconds,
                cancellationToken);
        }

        return RunCommandAsync(xbondSettings.ClientBinaryPath, arguments, timeoutSeconds, cancellationToken);
    }

    private string ResolveSudoClientBinaryPath()
    {
        if (Path.IsPathRooted(xbondSettings.ClientBinaryPath))
        {
            return xbondSettings.ClientBinaryPath;
        }

        return OperatingSystem.IsLinux()
            ? $"/usr/local/bin/{xbondSettings.ClientBinaryPath}"
            : xbondSettings.ClientBinaryPath;
    }

    private static async Task<CommandResult> RunCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)));
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

        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new CommandResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new CommandResult(124, "", $"{command} timed out.");
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new CommandResult(127, "", ex.Message);
        }
    }

    private string? ResolveUsbResetTarget(string interfaceName)
    {
        try
        {
            var deviceLink = new FileInfo($"/sys/class/net/{interfaceName}/device");
            var target = deviceLink.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            return BuildUsbResetTargetFromUsbDeviceDirectory(target, path =>
            {
                try
                {
                    return File.Exists(path) ? File.ReadAllText(path) : null;
                }
                catch
                {
                    return null;
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to resolve USB reset target for interface {Interface}", interfaceName);
            return null;
        }
    }

    public static string? BuildUsbResetTargetFromUsbDeviceDirectory(string? directory, Func<string, string?> readFile)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var current = new DirectoryInfo(directory);
        for (var depth = 0; current is not null && depth < 12; depth++, current = current.Parent)
        {
            if (!UsbDeviceNameRegex().IsMatch(current.Name))
            {
                continue;
            }

            var busText = readFile(Path.Combine(current.FullName, "busnum"));
            var deviceText = readFile(Path.Combine(current.FullName, "devnum"));
            if (!int.TryParse(busText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bus) ||
                !int.TryParse(deviceText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var device))
            {
                continue;
            }

            return $"{bus:000}/{device:000}";
        }

        return null;
    }

    public static RecoveryIssue DetermineRecoveryIssue(
        XBondPathStatus? path,
        F50RecoveryProbeResponse? modem,
        bool boundPingOk,
        int severeLossPercent)
    {
        var hasSocketFault = IsRebindSocketFault(path);
        var severeLoss = path is not null && path.LossRate >= Math.Clamp(severeLossPercent, 50, 100) / 100.0;
        var modemKnownBad = modem is not null && !HasUsableWan(modem);
        var modemUsableOrUnknown = modem is null || HasUsableWan(modem);

        if (hasSocketFault && boundPingOk && modemUsableOrUnknown)
        {
            return RecoveryIssue.SocketOnly;
        }

        if (boundPingOk && !modemKnownBad && !severeLoss)
        {
            return RecoveryIssue.None;
        }

        return RecoveryIssue.ModemReset;
    }

    private static bool IsRebindSocketFault(XBondPathStatus? path)
    {
        if (path is null)
        {
            return false;
        }

        if (path.SendFailureStreak >= 2)
        {
            return true;
        }

        var error = path.LastSocketError ?? path.LastRebindError;
        return !string.IsNullOrWhiteSpace(error) &&
               (error.Contains("No such device", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("os error 19", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasUsableWan(F50RecoveryProbeResponse? modem)
    {
        if (modem is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(modem.Error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(modem.WanIpAddress) ||
            modem.WanIpAddress.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var state = FirstNonEmpty(modem.PppStatus, modem.ModemMainState);
        return state is null ||
               !state.Contains("disconnect", StringComparison.OrdinalIgnoreCase) &&
               !state.Contains("no service", StringComparison.OrdinalIgnoreCase) &&
               !state.Contains("search", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveInterfaceName(
        LocalDeviceProxyEntry entry,
        string targetHost,
        IReadOnlyDictionary<string, string> routeByGateway,
        XBondPathStatus? path)
    {
        if (!string.IsNullOrWhiteSpace(targetHost) &&
            routeByGateway.TryGetValue(targetHost, out var routeInterface))
        {
            return routeInterface;
        }

        return path?.InterfaceName;
    }

    private static XBondPathStatus? FindMatchingPath(
        XBondStatus status,
        LocalDeviceProxyEntry entry,
        IReadOnlyDictionary<string, string> routeByGateway)
    {
        var targetHost = LocalDeviceProxyService.GetTargetHost(entry);
        if (!string.IsNullOrWhiteSpace(targetHost) &&
            routeByGateway.TryGetValue(targetHost, out var routeInterface))
        {
            var byInterface = status.Paths.FirstOrDefault(path =>
                string.Equals(path.InterfaceName, routeInterface, StringComparison.OrdinalIgnoreCase));
            if (byInterface is not null)
            {
                return byInterface;
            }
        }

        var entryName = NormalizeName(entry.DisplayName);
        return status.Paths.FirstOrDefault(path =>
        {
            var pathName = NormalizeName(path.Name);
            return !string.IsNullOrWhiteSpace(entryName) &&
                   !string.IsNullOrWhiteSpace(pathName) &&
                   (pathName.Contains(entryName, StringComparison.OrdinalIgnoreCase) ||
                    entryName.Contains(pathName, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static F50ModemRecoveryEntryStatus BuildEntryStatus(
        LocalDeviceProxyEntry entry,
        string targetHost,
        string? interfaceName,
        XBondPathStatus? path,
        F50RecoveryProbeResponse? modem)
    {
        return new F50ModemRecoveryEntryStatus
        {
            ProxyId = entry.Id,
            DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? targetHost : entry.DisplayName,
            TargetHost = targetHost,
            InterfaceName = interfaceName,
            PathId = path?.PathId,
            Role = path?.Role ?? "",
            RttMs = path?.RttMs,
            LossPercent = path is null ? null : (int)Math.Round(Math.Clamp(path.LossRate, 0.0, 1.0) * 100),
            WanIpAddress = modem?.WanIpAddress,
            ModemState = modem?.ModemMainState,
            PppStatus = modem?.PppStatus,
            State = "checking",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private void ApplyCooldown(string proxyId, F50ModemRecoveryEntryStatus result)
    {
        var rounds = Math.Clamp(settings.FailedRecoveryCooldownRounds, 0, 10);
        if (rounds > 0)
        {
            _cooldowns[proxyId] = rounds;
        }

        result.CooldownRoundsRemaining = rounds;
    }

    private void UpdateStatus(Action<F50ModemRecoveryStatus> update)
    {
        lock (_statusLock)
        {
            var clone = CloneStatus(_status);
            update(clone);
            _status = clone;
        }
    }

    private static F50ModemRecoveryStatus BuildInitialStatus(F50ModemRecoverySettings settings)
    {
        F50ModemRecoverySettingsStore.Normalize(settings);
        return new F50ModemRecoveryStatus
        {
            Enabled = settings.Enabled,
            CheckIntervalMinutes = settings.CheckIntervalMinutes,
            Message = settings.Enabled
                ? "F50 modem recovery is waiting for its first check."
                : "F50 modem recovery is disabled."
        };
    }

    private static F50ModemRecoveryStatus CloneStatus(F50ModemRecoveryStatus status)
    {
        return new F50ModemRecoveryStatus
        {
            Enabled = status.Enabled,
            IsRunning = status.IsRunning,
            CheckIntervalMinutes = status.CheckIntervalMinutes,
            LastStartedAtUtc = status.LastStartedAtUtc,
            LastCompletedAtUtc = status.LastCompletedAtUtc,
            NextRunAtUtc = status.NextRunAtUtc,
            Message = status.Message,
            Modems = status.Modems.Select(CloneEntryStatus).ToList()
        };
    }

    private static F50ModemRecoveryEntryStatus CloneEntryStatus(F50ModemRecoveryEntryStatus status)
    {
        return new F50ModemRecoveryEntryStatus
        {
            ProxyId = status.ProxyId,
            DisplayName = status.DisplayName,
            TargetHost = status.TargetHost,
            InterfaceName = status.InterfaceName,
            PathId = status.PathId,
            Role = status.Role,
            RttMs = status.RttMs,
            LossPercent = status.LossPercent,
            WanIpAddress = status.WanIpAddress,
            ModemState = status.ModemState,
            PppStatus = status.PppStatus,
            State = status.State,
            LastAction = status.LastAction,
            LastResult = status.LastResult,
            LastError = status.LastError,
            CooldownRoundsRemaining = status.CooldownRoundsRemaining,
            UpdatedAtUtc = status.UpdatedAtUtc
        };
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    [GeneratedRegex(@"^\d+-\d+(?:\.\d+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex UsbDeviceNameRegex();

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}

public enum RecoveryIssue
{
    None,
    SocketOnly,
    ModemReset
}

public sealed class F50RecoveryProbeResponse
{
    [JsonPropertyName("network_type")]
    public string? NetworkType { get; init; }

    [JsonPropertyName("current_network_type")]
    public string? CurrentNetworkType { get; init; }

    [JsonPropertyName("signalbar")]
    public string? SignalBar { get; init; }

    [JsonPropertyName("network_provider")]
    public string? NetworkProvider { get; init; }

    [JsonPropertyName("operator")]
    public string? Operator { get; init; }

    [JsonPropertyName("ppp_status")]
    public string? PppStatus { get; init; }

    [JsonPropertyName("modem_main_state")]
    public string? ModemMainState { get; init; }

    [JsonPropertyName("wan_ipaddr")]
    public string? WanIpAddress { get; init; }

    [JsonPropertyName("Error")]
    public string? Error { get; init; }
}
