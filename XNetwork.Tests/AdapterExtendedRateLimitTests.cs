using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Tests;

public class AdapterExtendedRateLimitTests
{
    [Fact]
    public void DeserializesSpeedifyRateLimitBpsFieldsAsBytesPerSecond()
    {
        const string json = """
            {
              "adapterID": "wan1",
              "rateLimit": {
                "downloadBps": 12500000,
                "uploadBps": 6250000
              }
            }
            """;

        var adapter = JsonSerializer.Deserialize<AdapterExtended>(json);

        Assert.NotNull(adapter);
        Assert.Equal(12_500_000, adapter.RateLimit.DownloadBytesPerSecond);
        Assert.Equal(6_250_000, adapter.RateLimit.UploadBytesPerSecond);
    }
}
