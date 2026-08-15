using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class TrafficBypassStartupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ulink-bypass-{Guid.NewGuid():N}");

    [Fact]
    public async Task SavingCreatesTheRulesFileTheHelperReads()
    {
        // The helper is handed this path directly, so it must exist before an apply is attempted.
        var path = Path.Combine(_directory, "traffic-bypass-rules.json");
        var store = new TrafficBypassSettingsStore(NullLogger<TrafficBypassSettingsStore>.Instance, path);

        Assert.False(File.Exists(path));

        await store.SaveAsync(new TrafficBypassSettings());

        Assert.True(File.Exists(path));

        var reloaded = new TrafficBypassSettings { Rules = { new TrafficBypassRule { DisplayName = "stale" } } };
        store.Load(reloaded);
        Assert.Empty(reloaded.Rules);
    }

    [Fact]
    public void LoadingAMissingFileLeavesRulesEmptyRatherThanThrowing()
    {
        var store = new TrafficBypassSettingsStore(
            NullLogger<TrafficBypassSettingsStore>.Instance,
            Path.Combine(_directory, "missing.json"));
        var settings = new TrafficBypassSettings();

        store.Load(settings);

        Assert.Empty(settings.Rules);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
