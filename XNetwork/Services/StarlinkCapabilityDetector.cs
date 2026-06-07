using XNetwork.Models;

namespace XNetwork.Services;

public static class StarlinkCapabilityDetector
{
    public static StarlinkCapabilitySnapshot Detect(
        string host,
        bool statusAvailable,
        bool webUiReachable,
        string? rootHtml,
        string? scriptText,
        string? error = null)
    {
        var source = $"{rootHtml}\n{scriptText}";
        var webUiUrl = $"http://{host}/";

        var capabilities = new[]
        {
            new StarlinkCapability
            {
                Key = "status",
                Label = "Live dish telemetry",
                Description = "Current dish state, latency, throughput, obstruction, alignment, GPS, and alerts.",
                IsDetected = statusAvailable,
                IsActionable = false,
                IsDisruptive = false
            },
            new StarlinkCapability
            {
                Key = "web-ui",
                Label = "Starlink web UI",
                Description = "Open the local Starlink diagnostic interface.",
                IsDetected = webUiReachable,
                IsActionable = webUiReachable,
                IsDisruptive = false,
                ActionUrl = webUiReachable ? webUiUrl : null
            },
            new StarlinkCapability
            {
                Key = "diagnostics",
                Label = "Diagnostics",
                Description = "Starlink diagnostic screens are present in the local web UI.",
                IsDetected = ContainsAny(source, "Diagnostic", "Diagnostics"),
                IsActionable = webUiReachable,
                IsDisruptive = false,
                ActionUrl = webUiReachable ? webUiUrl : null
            },
            new StarlinkCapability
            {
                Key = "reboot",
                Label = "Reboot",
                Description = "Restart the Starlink terminal from the Starlink interface.",
                IsDetected = ContainsAny(source, "Reboot"),
                IsActionable = webUiReachable,
                IsDisruptive = true,
                ActionUrl = webUiReachable ? webUiUrl : null,
                DisabledReason = webUiReachable ? null : "Starlink web UI is not reachable"
            },
            new StarlinkCapability
            {
                Key = "stow",
                Label = "Stow",
                Description = "Move the dish to stow position from the Starlink interface.",
                IsDetected = ContainsAny(source, "Stow"),
                IsActionable = webUiReachable,
                IsDisruptive = true,
                ActionUrl = webUiReachable ? webUiUrl : null,
                DisabledReason = webUiReachable ? null : "Starlink web UI is not reachable"
            },
            new StarlinkCapability
            {
                Key = "unstow",
                Label = "Unstow",
                Description = "Return the dish from stow position from the Starlink interface.",
                IsDetected = ContainsAny(source, "Unstow"),
                IsActionable = webUiReachable,
                IsDisruptive = true,
                ActionUrl = webUiReachable ? webUiUrl : null,
                DisabledReason = webUiReachable ? null : "Starlink web UI is not reachable"
            }
        };

        return new StarlinkCapabilitySnapshot
        {
            IsAvailable = statusAvailable || webUiReachable || capabilities.Any(c => c.IsDetected),
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            Error = error,
            Capabilities = capabilities
        };
    }

    private static bool ContainsAny(string source, params string[] values)
    {
        return values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
}
