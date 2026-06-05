using XNetwork.Models;
using XNetwork.Utils;

namespace XNetwork.Tests;

public class AdapterEncryptionMapperTests
{
    [Fact]
    public void Apply_UsesPerAdapterEncryptionSettingsWhenPresent()
    {
        var adapters = new List<AdapterExtended>
        {
            new() { AdapterId = "wlan0" },
            new() { AdapterId = "enx-cell" },
            new() { AdapterId = "missing" }
        };
        var settings = new SpeedifySettings
        {
            Encrypted = true,
            PerConnectionEncryptionEnabled = true,
            PerConnectionEncryptionSettings =
            [
                new() { AdapterId = "wlan0", Encrypted = false },
                new() { AdapterId = "enx-cell", Encrypted = true }
            ]
        };

        AdapterEncryptionMapper.Apply(adapters, settings);

        Assert.False(adapters.Single(x => x.AdapterId == "wlan0").EncryptionEnabled);
        Assert.True(adapters.Single(x => x.AdapterId == "enx-cell").EncryptionEnabled);
        Assert.True(adapters.Single(x => x.AdapterId == "missing").EncryptionEnabled);
    }

    [Fact]
    public void Apply_FallsBackToGlobalEncryptionWhenPerAdapterSettingsAreDisabled()
    {
        var adapters = new List<AdapterExtended>
        {
            new() { AdapterId = "wlan0", EncryptionEnabled = true }
        };
        var settings = new SpeedifySettings
        {
            Encrypted = false,
            PerConnectionEncryptionEnabled = false,
            PerConnectionEncryptionSettings =
            [
                new() { AdapterId = "wlan0", Encrypted = true }
            ]
        };

        AdapterEncryptionMapper.Apply(adapters, settings);

        Assert.False(adapters.Single().EncryptionEnabled);
    }
}
