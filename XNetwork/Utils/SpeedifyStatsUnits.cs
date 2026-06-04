namespace XNetwork.Utils;

public static class SpeedifyStatsUnits
{
    /// <summary>
    /// Converts Speedify CLI stats rate fields to Mbps.
    /// </summary>
    /// <remarks>
    /// The fields are named receiveBps/sendBps and older documentation describes them as bytes/sec,
    /// but live Speedify 16.8.0 measurements on xeon-network match Linux/F50 counters when treated
    /// as bits/sec rate values.
    /// </remarks>
    public static double RateFieldToMbps(double speedifyRate)
    {
        return speedifyRate / 1_000_000.0;
    }
}
