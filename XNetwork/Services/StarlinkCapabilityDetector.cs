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
                IsActionable = ContainsAny(source, "RebootRequest", "setReboot", "Reboot"),
                IsDisruptive = true,
                DirectCommand = "reboot",
                DisabledReason = ContainsAny(source, "RebootRequest", "setReboot", "Reboot") ? null : "Reboot command was not detected in this Starlink firmware"
            },
            new StarlinkCapability
            {
                Key = "stow",
                Label = "Stow",
                Description = "Move the dish to stow position from the Starlink interface.",
                IsDetected = ContainsAny(source, "DishStowRequest", "setDishStow", "Stow"),
                IsActionable = ContainsAny(source, "DishStowRequest", "setDishStow", "Stow"),
                IsDisruptive = true,
                DirectCommand = "stow",
                DisabledReason = ContainsAny(source, "DishStowRequest", "setDishStow", "Stow") ? null : "Stow command was not detected in this Starlink firmware"
            },
            new StarlinkCapability
            {
                Key = "unstow",
                Label = "Unstow",
                Description = "Return the dish from stow position from the Starlink interface.",
                IsDetected = ContainsAny(source, "DishStowRequest", "setUnstow", "Unstow"),
                IsActionable = ContainsAny(source, "DishStowRequest", "setUnstow", "Unstow"),
                IsDisruptive = true,
                DirectCommand = "unstow",
                DisabledReason = ContainsAny(source, "DishStowRequest", "setUnstow", "Unstow") ? null : "Unstow command was not detected in this Starlink firmware"
            },
            new StarlinkCapability
            {
                Key = "dish-clear-obstruction-map",
                Label = "Reset obstruction map",
                Description = "Clear the learned obstruction map. Starlink may take hours or days to rebuild obstruction history.",
                IsDetected = statusAvailable || webUiReachable || ContainsAny(source, "DishClearObstructionMapRequest", "dish_clear_obstruction_map"),
                IsActionable = statusAvailable || webUiReachable,
                IsDisruptive = true,
                DirectCommand = "dish_clear_obstruction_map",
                DisabledReason = statusAvailable || webUiReachable ? null : "Starlink local API was not reachable"
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
