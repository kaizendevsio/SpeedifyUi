using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class TrafficBypassService(
    TrafficBypassSettings settings,
    TrafficBypassSettingsStore store,
    InterfaceMetadataService interfaceMetadataService,
    ILogger<TrafficBypassService> logger)
{
    private static readonly Regex SafeInterfaceName = new(@"^[A-Za-z0-9_.:@-]{1,64}$", RegexOptions.Compiled);
    private readonly object _lock = new();
    private TrafficBypassApplyStatus? _lastApplyStatus;

    public IReadOnlyList<TrafficBypassRule> GetRules()
    {
        lock (_lock)
        {
            return settings.Rules.Select(Clone).ToArray();
        }
    }

    public TrafficBypassApplyStatus? GetLastApplyStatus()
    {
        lock (_lock)
        {
            return _lastApplyStatus;
        }
    }

    public async Task<IReadOnlyList<TrafficBypassEgressAdapter>> GetEgressAdaptersAsync(CancellationToken cancellationToken = default)
    {
        var interfaces = await interfaceMetadataService.GetInterfacesAsync(cancellationToken).ConfigureAwait(false);
        return interfaces
            .Where(item => item.IsDashboardCandidate)
            .Select(item => new TrafficBypassEgressAdapter(
                item.Device,
                string.IsNullOrWhiteSpace(item.DisplayName) ? item.Device : item.DisplayName,
                item.State))
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<TrafficBypassApplyStatus> SaveRuleAsync(TrafficBypassRule rule, CancellationToken cancellationToken = default)
    {
        rule = NormalizeRule(rule);
        rule.Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id.Trim();

        var validation = ValidateRule(rule);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(" ", validation.Errors));
        }

        lock (_lock)
        {
            var index = settings.Rules.FindIndex(item => string.Equals(item.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                settings.Rules[index] = Clone(rule);
            }
            else
            {
                settings.Rules.Add(Clone(rule));
            }
        }

        await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return await ApplyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrafficBypassApplyStatus> DeleteRuleAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            settings.Rules.RemoveAll(rule => string.Equals(rule.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return await ApplyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrafficBypassApplyStatus> ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return SetLastApplyStatus(new TrafficBypassApplyStatus(
                IsSupported: false,
                Applied: false,
                Message: "Traffic bypass rules are only applied on Linux routers.",
                Error: null,
                UpdatedAtUtc: DateTimeOffset.UtcNow));
        }

        var helperPath = string.IsNullOrWhiteSpace(settings.ApplyHelperPath)
            ? "/usr/local/sbin/xnetwork-traffic-bypass-apply"
            : settings.ApplyHelperPath.Trim();
        if (!File.Exists(helperPath))
        {
            return SetLastApplyStatus(new TrafficBypassApplyStatus(
                IsSupported: true,
                Applied: false,
                Message: "Traffic bypass helper is not installed yet.",
                Error: helperPath,
                UpdatedAtUtc: DateTimeOffset.UtcNow));
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.CommandTimeoutSeconds, 3, 60)));

            var startInfo = new ProcessStartInfo
            {
                FileName = File.Exists("/usr/bin/sudo") ? "/usr/bin/sudo" : helperPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (startInfo.FileName.EndsWith("sudo", StringComparison.Ordinal))
            {
                startInfo.ArgumentList.Add("-n");
                startInfo.ArgumentList.Add(helperPath);
            }

            startInfo.ArgumentList.Add("apply");
            startInfo.ArgumentList.Add(store.FilePath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return SetLastApplyStatus(new TrafficBypassApplyStatus(
                    IsSupported: true,
                    Applied: false,
                    Message: "Traffic bypass helper could not be started.",
                    Error: null,
                    UpdatedAtUtc: DateTimeOffset.UtcNow));
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = (await outputTask.ConfigureAwait(false)).Trim();
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            if (process.ExitCode == 0)
            {
                return SetLastApplyStatus(new TrafficBypassApplyStatus(
                    IsSupported: true,
                    Applied: true,
                    Message: string.IsNullOrWhiteSpace(output) ? "Traffic bypass rules applied." : output,
                    Error: null,
                    UpdatedAtUtc: DateTimeOffset.UtcNow));
            }

            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            return SetLastApplyStatus(new TrafficBypassApplyStatus(
                IsSupported: true,
                Applied: false,
                Message: "Traffic bypass rules could not be applied.",
                Error: string.IsNullOrWhiteSpace(detail) ? $"helper exited {process.ExitCode}" : detail,
                UpdatedAtUtc: DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SetLastApplyStatus(new TrafficBypassApplyStatus(
                IsSupported: true,
                Applied: false,
                Message: "Traffic bypass helper timed out.",
                Error: null,
                UpdatedAtUtc: DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Traffic bypass helper failed");
            return SetLastApplyStatus(new TrafficBypassApplyStatus(
                IsSupported: true,
                Applied: false,
                Message: "Traffic bypass helper failed.",
                Error: ex.Message,
                UpdatedAtUtc: DateTimeOffset.UtcNow));
        }
    }

    public TrafficBypassValidationResult ValidateRule(TrafficBypassRule rule, string? currentId = null)
    {
        rule = NormalizeRule(rule);
        var result = new TrafficBypassValidationResult();

        if (string.IsNullOrWhiteSpace(rule.DisplayName))
        {
            result.Errors.Add("Name is required.");
        }

        if (rule.Destinations.Count == 0 && rule.Ports.Count == 0)
        {
            result.Errors.Add("Add at least one destination IP/CIDR or one destination port.");
        }

        foreach (var destination in rule.Destinations)
        {
            if (!IsValidDestination(destination))
            {
                result.Errors.Add($"Destination {destination} must be an IPv4 address or CIDR.");
            }
        }

        foreach (var port in rule.Ports)
        {
            if (!IsValidPortOrRange(port))
            {
                result.Errors.Add($"Port {port} must be a port number or range.");
            }
        }

        if (!TrafficBypassProtocols.IsKnown(rule.Protocol))
        {
            result.Errors.Add("Protocol must be Any, TCP, or UDP.");
        }

        if (rule.EgressMode == TrafficBypassEgressModes.Interface)
        {
            if (string.IsNullOrWhiteSpace(rule.InterfaceName))
            {
                result.Errors.Add("Choose an egress adapter or use Auto physical.");
            }
            else if (!SafeInterfaceName.IsMatch(rule.InterfaceName) || IsUnsafeInterface(rule.InterfaceName))
            {
                result.Errors.Add("Selected egress adapter is not allowed.");
            }
        }

        lock (_lock)
        {
            if (settings.Rules.Any(existing =>
                    !string.Equals(existing.Id, currentId ?? rule.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.DisplayName.Trim(), rule.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                result.Errors.Add("Rule name is already used.");
            }
        }

        return result;
    }

    public static TrafficBypassRule NormalizeRule(TrafficBypassRule rule)
    {
        var destinations = NormalizeList(rule.Destinations);
        var ports = NormalizeList(rule.Ports);
        var egressMode = TrafficBypassEgressModes.Normalize(rule.EgressMode);
        return new TrafficBypassRule
        {
            Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id.Trim(),
            DisplayName = rule.DisplayName?.Trim() ?? "",
            Enabled = rule.Enabled,
            Destinations = destinations,
            Protocol = TrafficBypassProtocols.Normalize(rule.Protocol),
            Ports = ports,
            EgressMode = egressMode,
            InterfaceName = egressMode == TrafficBypassEgressModes.Interface
                ? rule.InterfaceName.Trim()
                : ""
        };
    }

    public static IReadOnlyList<string> SplitTextList(string? value) =>
        (value ?? "")
            .Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool IsValidDestination(string value)
    {
        value = value.Trim();
        if (value.Contains('/'))
        {
            var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
            return parts.Length == 2 &&
                   IPAddress.TryParse(parts[0], out var address) &&
                   address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                   int.TryParse(parts[1], out var prefix) &&
                   prefix is >= 0 and <= 32;
        }

        return IPAddress.TryParse(value, out var ipAddress) &&
               ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    public static bool IsValidPortOrRange(string value)
    {
        value = value.Trim();
        if (value.Contains('-'))
        {
            var parts = value.Split('-', 2, StringSplitOptions.TrimEntries);
            return parts.Length == 2 &&
                   TryParsePort(parts[0], out var start) &&
                   TryParsePort(parts[1], out var end) &&
                   start <= end;
        }

        return TryParsePort(value, out _);
    }

    private TrafficBypassApplyStatus SetLastApplyStatus(TrafficBypassApplyStatus status)
    {
        lock (_lock)
        {
            _lastApplyStatus = status;
        }

        return status;
    }

    private static bool TryParsePort(string value, out int port) =>
        int.TryParse(value, out port) && port is >= 1 and <= 65535;

    private static List<string> NormalizeList(IEnumerable<string>? values) =>
        (values ?? [])
            .SelectMany(value => SplitTextList(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsUnsafeInterface(string interfaceName) =>
        interfaceName.StartsWith("xbond", StringComparison.OrdinalIgnoreCase) ||
        interfaceName.StartsWith("connectify", StringComparison.OrdinalIgnoreCase) ||
        interfaceName.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase) ||
        interfaceName.StartsWith("tun", StringComparison.OrdinalIgnoreCase) ||
        interfaceName.StartsWith("wg", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(interfaceName, "lo", StringComparison.OrdinalIgnoreCase);

    private static TrafficBypassRule Clone(TrafficBypassRule rule) => new()
    {
        Id = rule.Id,
        DisplayName = rule.DisplayName,
        Enabled = rule.Enabled,
        Destinations = rule.Destinations.ToList(),
        Protocol = rule.Protocol,
        Ports = rule.Ports.ToList(),
        EgressMode = rule.EgressMode,
        InterfaceName = rule.InterfaceName
    };
}
