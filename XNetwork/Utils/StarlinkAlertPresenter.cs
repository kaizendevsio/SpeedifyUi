namespace XNetwork.Utils;

/// <summary>
/// Human-readable labels and explanations for Starlink alert keys, shared by the dashboard card
/// strip and the Starlink details section.
/// </summary>
public static class StarlinkAlertPresenter
{
    public static string GetLabel(string alert)
    {
        return alert switch
        {
            "obstruction map reset" => "Obstruction map reset",
            "slow ethernet" or "slow ethernet 100" => "Slow Ethernet",
            _ => alert
        };
    }

    public static string GetDescription(string alert)
    {
        return alert.ToLowerInvariant() switch
        {
            "obstruction map reset" => "The learned obstruction map was cleared and Starlink is rebuilding obstruction history.",
            "slow ethernet" or "slow ethernet 100" => "The Ethernet link is negotiating below the expected speed.",
            "obstructed" => "The dish currently sees an obstruction.",
            "roaming" => "Starlink reports it is roaming outside its registered service area.",
            "moving too fast" => "Starlink reports the terminal is moving faster than the active plan or hardware allows.",
            "thermal shutdown" => "Starlink has shut down because of temperature.",
            "thermal throttle" or "power thermal" => "Starlink is reducing performance because of temperature or power constraints.",
            "low motor current" => "Starlink reported a motor current issue.",
            "signal lower than expected" => "Starlink reports signal quality below the expected level.",
            "telemetry stale" => "uLink has not received fresh Starlink telemetry recently.",
            _ => alert
        };
    }
}
