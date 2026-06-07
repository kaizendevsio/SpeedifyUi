using XNetwork.Models;

namespace XNetwork.Services;

public interface IStarlinkTelemetryService
{
    StarlinkTelemetrySnapshot GetSnapshot();

    IReadOnlyList<StarlinkTelemetrySnapshot> GetHistory();

    StarlinkCapabilitySnapshot GetCapabilities();
}
