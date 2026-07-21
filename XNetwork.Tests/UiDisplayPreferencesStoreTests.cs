using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public sealed class UiDisplayPreferencesStoreTests
{
    [Fact]
    public async Task SaveAndLoad_PersistsAdapterTechnicalDetails()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ulink-ui-{Guid.NewGuid():N}");
        var filePath = Path.Combine(directory, "preferences.json");

        try
        {
            var store = new UiDisplayPreferencesStore(
                NullLogger<UiDisplayPreferencesStore>.Instance,
                filePath);
            await store.SaveAsync(new UiDisplayPreferences { AdapterTechnicalDetails = true });

            var loaded = new UiDisplayPreferences();
            store.Load(loaded);

            Assert.True(loaded.AdapterTechnicalDetails);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void MissingFile_LeavesTechnicalDetailsOffByDefault()
    {
        var preferences = new UiDisplayPreferences();
        var store = new UiDisplayPreferencesStore(
            NullLogger<UiDisplayPreferencesStore>.Instance,
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        store.Load(preferences);

        Assert.False(preferences.AdapterTechnicalDetails);
    }
}
