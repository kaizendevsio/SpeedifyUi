namespace XNetwork.Models;

public sealed class CudyWirelessStatus
{
    public bool IsConfigured { get; set; }

    public bool IsSmartConnect { get; set; }

    public bool? TwoGEnabled { get; set; }

    public bool? FiveGEnabled { get; set; }

    public string Message { get; set; } = "Cudy wireless state has not been loaded.";

    public DateTime? UpdatedUtc { get; set; }
}

public enum CudyWirelessBand
{
    TwoG,
    FiveG
}
