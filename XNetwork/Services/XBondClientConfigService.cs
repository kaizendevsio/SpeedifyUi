using System.Diagnostics;
using System.Globalization;
using System.Text;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class XBondClientConfigService(
    ILogger<XBondClientConfigService> logger,
    XBondSettings settings,
    InterfaceMetadataService interfaceMetadataService,
    XBondTrafficEngineService trafficEngineService)
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public async Task<XBondAdapterConfigStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            var interfaces = await interfaceMetadataService.GetInterfacesAsync(cancellationToken).ConfigureAwait(false);
            return BuildStatus(config, interfaces, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Unable to read XBond client adapter config");
            return new XBondAdapterConfigStatus
            {
                CanEdit = false,
                Error = ex.Message,
                Message = "XBond adapter config is unavailable."
            };
        }
    }

    public async Task<XBondAdapterConfigStatus> SetInterfaceEnabledAsync(
        string interfaceName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (!settings.AllowServiceControl)
        {
            return ErrorStatus("XBond adapter changes are locked by configuration.");
        }

        if (!IsSafeInterfaceName(interfaceName))
        {
            return ErrorStatus("Invalid network adapter name.");
        }

        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ErrorStatus("Another XBond adapter change is already running.");
        }

        try
        {
            var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            var interfaces = await interfaceMetadataService.GetInterfacesAsync(cancellationToken).ConfigureAwait(false);
            var candidates = interfaces
                .Where(item => item.IsDashboardCandidate)
                .ToDictionary(item => item.Device, StringComparer.OrdinalIgnoreCase);

            var existing = config.Paths.FirstOrDefault(path =>
                string.Equals(path.InterfaceName, interfaceName, StringComparison.OrdinalIgnoreCase));

            if (enabled)
            {
                if (!candidates.TryGetValue(interfaceName, out var metadata))
                {
                    return BuildStatus(config, interfaces, "Only connected ethernet or Wi-Fi adapters can be added to XBond.");
                }

                if (existing is null)
                {
                    config.Paths.Add(new XBondClientPathConfig
                    {
                        Id = NextPathId(config),
                        Name = DisplayNameFor(metadata),
                        InterfaceName = metadata.Device,
                        Enabled = true
                    });
                }
                else
                {
                    existing.Enabled = true;
                    existing.Name = DisplayNameFor(metadata);
                }
            }
            else
            {
                if (existing is null)
                {
                    return BuildStatus(config, interfaces, "Adapter is already outside XBond.");
                }

                if (config.Paths.Count(path => path.Enabled && !string.Equals(path.InterfaceName, interfaceName, StringComparison.OrdinalIgnoreCase)) == 0)
                {
                    return BuildStatus(config, interfaces, "Keep at least one XBond adapter configured.");
                }

                config.Paths.Remove(existing);
            }

            await WriteConfigAsync(config, cancellationToken).ConfigureAwait(false);
            var serviceStatus = await trafficEngineService.RestartAsync(cancellationToken).ConfigureAwait(false);
            var refreshed = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            var refreshedInterfaces = await interfaceMetadataService.GetInterfacesAsync(cancellationToken).ConfigureAwait(false);
            var status = BuildStatus(refreshed, refreshedInterfaces, enabled ? "Adapter added to XBond." : "Adapter removed from XBond.");
            if (serviceStatus.HasError)
            {
                status.Error = serviceStatus.Error;
                status.Message = "XBond config was saved, but restarting the tunnel failed.";
            }

            return status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex, "Failed to change XBond adapter membership for {Interface}", interfaceName);
            return ErrorStatus(ex.Message);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<XBondAdapterConfigStatus> SetPolicyAsync(
        string redundancyPolicy,
        int interactivePacketThresholdBytes,
        double duplicateLossThreshold,
        double backupLossDisableThreshold,
        int reorderHoldMs,
        CancellationToken cancellationToken = default)
    {
        if (!settings.AllowServiceControl)
        {
            return ErrorStatus("XBond policy changes are locked by configuration.");
        }

        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ErrorStatus("Another XBond config change is already running.");
        }

        try
        {
            var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            config.RedundancyPolicy = NormalizePolicy(redundancyPolicy);
            config.InteractivePacketThresholdBytes = Math.Clamp(interactivePacketThresholdBytes, 64, 1_500);
            config.DuplicateLossThreshold = Math.Clamp(duplicateLossThreshold, 0.0, 1.0);
            config.BackupLossDisableThreshold = Math.Clamp(backupLossDisableThreshold, 0.0, 1.0);
            config.ReorderHoldMs = Math.Clamp(reorderHoldMs, 0, 250);

            await WriteConfigAsync(config, cancellationToken).ConfigureAwait(false);
            var serviceStatus = await trafficEngineService.RestartAsync(cancellationToken).ConfigureAwait(false);
            var refreshed = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            var refreshedInterfaces = await interfaceMetadataService.GetInterfacesAsync(cancellationToken).ConfigureAwait(false);
            var status = BuildStatus(refreshed, refreshedInterfaces, "XBond policy saved.");
            if (serviceStatus.HasError)
            {
                status.Error = serviceStatus.Error;
                status.Message = "XBond policy was saved, but restarting the tunnel failed.";
            }

            return status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex, "Failed to change XBond policy");
            return ErrorStatus(ex.Message);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<XBondClientConfig> ReadConfigAsync(CancellationToken cancellationToken)
    {
        var path = ConfigPath();
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsLinux())
        {
            text = await ReadRootFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (OperatingSystem.IsLinux())
        {
            text = await ReadRootFileAsync(path, cancellationToken).ConfigureAwait(false);
        }

        var config = ParseConfig(text);
        ApplyRuntimeDefaults(config);
        return config;
    }

    private async Task WriteConfigAsync(XBondClientConfig config, CancellationToken cancellationToken)
    {
        var path = ConfigPath();
        var text = RenderConfig(config);
        if (!OperatingSystem.IsLinux())
        {
            await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await RunProcessAsync(
            settings.SudoPath,
            ["-n", "/usr/bin/tee", path],
            text,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? $"Unable to write {path}."
                : result.Error.Trim());
        }

        var chmod = await RunProcessAsync(
            settings.SudoPath,
            ["-n", "/bin/chmod", "0644", path],
            null,
            cancellationToken).ConfigureAwait(false);
        if (chmod.ExitCode != 0)
        {
            logger.LogDebug("Unable to chmod XBond client config {Path}: {Error}", path, chmod.Error.Trim());
        }
    }

    private async Task<string> ReadRootFileAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            settings.SudoPath,
            ["-n", "/bin/cat", path],
            null,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? $"Unable to read {path}."
                : result.Error.Trim());
        }

        return result.Output;
    }

    private XBondAdapterConfigStatus BuildStatus(
        XBondClientConfig config,
        IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> interfaces,
        string? message)
    {
        var rows = new Dictionary<string, XBondAdapterConfigRow>(StringComparer.OrdinalIgnoreCase);
        var metadataByDevice = interfaces.ToDictionary(item => item.Device, StringComparer.OrdinalIgnoreCase);

        foreach (var path in config.Paths.Where(path => !string.IsNullOrWhiteSpace(path.InterfaceName)))
        {
            metadataByDevice.TryGetValue(path.InterfaceName, out var metadata);
            rows[path.InterfaceName] = new XBondAdapterConfigRow
            {
                PathId = path.Id,
                Name = metadata is null ? path.Name : DisplayNameFor(metadata, path.Name),
                InterfaceName = path.InterfaceName,
                Type = metadata?.Type ?? "configured",
                State = metadata?.State ?? "not present",
                IsConfigured = path.Enabled,
                IsConnected = metadata?.IsConnected == true,
                IsEligible = metadata?.IsDashboardCandidate == true,
                Detail = metadata is null
                    ? "Configured path is not currently visible to NetworkManager."
                    : path.Enabled
                        ? "Included in XBond."
                        : "Configured but disabled."
            };
        }

        foreach (var metadata in interfaces.Where(item => item.IsDashboardCandidate))
        {
            if (rows.ContainsKey(metadata.Device))
            {
                continue;
            }

            rows[metadata.Device] = new XBondAdapterConfigRow
            {
                Name = DisplayNameFor(metadata),
                InterfaceName = metadata.Device,
                Type = metadata.Type,
                State = metadata.State,
                IsConfigured = false,
                IsConnected = metadata.IsConnected,
                IsEligible = true,
                Detail = "Connected and available to add."
            };
        }

        return new XBondAdapterConfigStatus
        {
            CanEdit = settings.AllowServiceControl && OperatingSystem.IsLinux(),
            Message = message ?? "Choose which connected adapters XBond should use.",
            Mode = config.Mode,
            RedundancyPolicy = config.RedundancyPolicy,
            MaxActiveBackups = config.MaxActiveBackups,
            RealtimeDeadlineMs = config.RealtimeDeadlineMs,
            InteractivePacketThresholdBytes = config.InteractivePacketThresholdBytes,
            DuplicateLossThreshold = config.DuplicateLossThreshold,
            BackupLossDisableThreshold = config.BackupLossDisableThreshold,
            ReorderHoldMs = config.ReorderHoldMs,
            Adapters = rows.Values
                .OrderByDescending(row => row.IsConfigured)
                .ThenByDescending(row => row.IsConnected)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public static XBondClientConfig ParseConfig(string text)
    {
        var config = new XBondClientConfig();
        XBondClientPathConfig? currentPath = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Equals("[[paths]]", StringComparison.OrdinalIgnoreCase))
            {
                if (currentPath is not null)
                {
                    config.Paths.Add(currentPath);
                }

                currentPath = new XBondClientPathConfig();
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (currentPath is null)
            {
                ApplyTopLevel(config, key, value);
            }
            else
            {
                ApplyPath(currentPath, key, value);
            }
        }

        if (currentPath is not null)
        {
            config.Paths.Add(currentPath);
        }

        config.Paths = config.Paths
            .Where(path => path.Id > 0 && !string.IsNullOrWhiteSpace(path.InterfaceName))
            .ToList();
        return config;
    }

    public static string RenderConfig(XBondClientConfig config)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"enabled = {FormatBool(config.Enabled)}");
        builder.AppendLine($"session_id = {config.SessionId}");
        builder.AppendLine($"server_addr = {Quote(config.ServerAddress)}");
        builder.AppendLine($"mode = {Quote(config.Mode)}");
        builder.AppendLine($"redundancy_policy = {Quote(config.RedundancyPolicy)}");
        builder.AppendLine($"max_active_backups = {Math.Max(0, config.MaxActiveBackups)}");
        builder.AppendLine($"realtime_deadline_ms = {Math.Max(1, config.RealtimeDeadlineMs)}");
        builder.AppendLine($"interactive_packet_threshold_bytes = {Math.Clamp(config.InteractivePacketThresholdBytes, 64, 1_500)}");
        builder.AppendLine($"duplicate_loss_threshold = {FormatDouble(Math.Clamp(config.DuplicateLossThreshold, 0.0, 1.0))}");
        builder.AppendLine($"backup_loss_disable_threshold = {FormatDouble(Math.Clamp(config.BackupLossDisableThreshold, 0.0, 1.0))}");
        builder.AppendLine($"reorder_hold_ms = {Math.Clamp(config.ReorderHoldMs, 0, 250)}");
        builder.AppendLine($"runtime_status_path = {Quote(config.RuntimeStatusPath)}");

        foreach (var path in config.Paths.OrderBy(path => path.Id))
        {
            builder.AppendLine();
            builder.AppendLine("[[paths]]");
            builder.AppendLine($"id = {path.Id}");
            builder.AppendLine($"name = {Quote(path.Name)}");
            builder.AppendLine($"interface_name = {Quote(path.InterfaceName)}");
            if (!string.IsNullOrWhiteSpace(path.BindAddress))
            {
                builder.AppendLine($"bind_addr = {Quote(path.BindAddress)}");
            }

            builder.AppendLine($"enabled = {FormatBool(path.Enabled)}");
        }

        return builder.ToString();
    }

    private static void ApplyTopLevel(XBondClientConfig config, string key, string value)
    {
        switch (key)
        {
            case "enabled":
                config.Enabled = ParseBool(value, config.Enabled);
                break;
            case "session_id":
                config.SessionId = ParseUlong(value, config.SessionId);
                break;
            case "server_addr":
                config.ServerAddress = Unquote(value);
                break;
            case "mode":
                config.Mode = Unquote(value);
                break;
            case "redundancy_policy":
                config.RedundancyPolicy = NormalizePolicy(Unquote(value));
                break;
            case "max_active_backups":
                config.MaxActiveBackups = ParseInt(value, config.MaxActiveBackups);
                break;
            case "realtime_deadline_ms":
                config.RealtimeDeadlineMs = ParseInt(value, config.RealtimeDeadlineMs);
                break;
            case "interactive_packet_threshold_bytes":
                config.InteractivePacketThresholdBytes = ParseInt(value, config.InteractivePacketThresholdBytes);
                break;
            case "duplicate_loss_threshold":
                config.DuplicateLossThreshold = ParseDouble(value, config.DuplicateLossThreshold);
                break;
            case "backup_loss_disable_threshold":
                config.BackupLossDisableThreshold = ParseDouble(value, config.BackupLossDisableThreshold);
                break;
            case "reorder_hold_ms":
                config.ReorderHoldMs = ParseInt(value, config.ReorderHoldMs);
                break;
            case "runtime_status_path":
                config.RuntimeStatusPath = Unquote(value);
                break;
        }
    }

    private static void ApplyPath(XBondClientPathConfig path, string key, string value)
    {
        switch (key)
        {
            case "id":
                path.Id = ParseInt(value, path.Id);
                break;
            case "name":
                path.Name = Unquote(value);
                break;
            case "interface_name":
                path.InterfaceName = Unquote(value);
                break;
            case "bind_addr":
                path.BindAddress = Unquote(value);
                break;
            case "enabled":
                path.Enabled = ParseBool(value, path.Enabled);
                break;
        }
    }

    private void ApplyRuntimeDefaults(XBondClientConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ServerAddress))
        {
            config.ServerAddress = settings.PublicTestServerAddress;
        }

        if (string.IsNullOrWhiteSpace(config.Mode))
        {
            config.Mode = settings.ScheduleMode;
        }

        config.RedundancyPolicy = NormalizePolicy(config.RedundancyPolicy);

        if (config.MaxActiveBackups < 0)
        {
            config.MaxActiveBackups = settings.MaxActiveBackups;
        }

        if (config.RealtimeDeadlineMs <= 0)
        {
            config.RealtimeDeadlineMs = 500;
        }

        if (config.InteractivePacketThresholdBytes <= 0)
        {
            config.InteractivePacketThresholdBytes = 768;
        }

        if (config.DuplicateLossThreshold <= 0)
        {
            config.DuplicateLossThreshold = 0.02;
        }

        if (config.BackupLossDisableThreshold <= 0)
        {
            config.BackupLossDisableThreshold = 0.35;
        }

        if (config.ReorderHoldMs < 0)
        {
            config.ReorderHoldMs = 25;
        }

        if (string.IsNullOrWhiteSpace(config.RuntimeStatusPath))
        {
            config.RuntimeStatusPath = settings.RuntimeStatusPath;
        }
    }

    private string ConfigPath() => string.IsNullOrWhiteSpace(settings.ClientConfigPath)
        ? "/etc/xbond/client.toml"
        : settings.ClientConfigPath;

    private static int NextPathId(XBondClientConfig config) =>
        config.Paths.Count == 0 ? 1 : config.Paths.Max(path => path.Id) + 1;

    private static string DisplayNameFor(InterfaceMetadataService.InterfaceMetadata metadata, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(metadata.DisplayName))
        {
            return metadata.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(metadata.ConnectionName) && metadata.ConnectionName != "--")
        {
            return metadata.ConnectionName;
        }

        return string.IsNullOrWhiteSpace(fallback) ? metadata.Device : fallback;
    }

    private static XBondAdapterConfigStatus ErrorStatus(string error) => new()
    {
        CanEdit = false,
        Error = error,
        Message = error,
        UpdatedAtUtc = DateTime.UtcNow
    };

    private static bool IsSafeInterfaceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return false;
        }

        return value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');
    }

    private static string StripComment(string line)
    {
        var inString = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString && ch == '#')
            {
                return line[..index];
            }
        }

        return line;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string FormatBool(bool value) => value ? "true" : "false";

    private static string FormatDouble(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool ParseBool(string value, bool fallback) =>
        bool.TryParse(value.Trim(), out var parsed) ? parsed : fallback;

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static double ParseDouble(string value, double fallback) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static ulong ParseUlong(string value, ulong fallback) =>
        ulong.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static string NormalizePolicy(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "reliable" => "reliable",
            "fast" => "fast",
            "diagnostic" => "diagnostic",
            _ => "balanced"
        };
    }

    private async Task<CommandResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ServiceCommandTimeoutSeconds, 3, 60)));

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {fileName}.");
        }

        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new CommandResult(
                process.ExitCode,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{fileName} timed out.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
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
            // Best effort cleanup.
        }
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
