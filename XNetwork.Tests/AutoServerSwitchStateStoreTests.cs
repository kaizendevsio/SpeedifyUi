using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class AutoServerSwitchStateStoreTests
{
    [Fact]
    public void SaveAndLoad_PreservesSwitchCountersEventsAndAvoidList()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"auto-switch-state-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AutoServerSwitchStateStore(NullLogger<AutoServerSwitchStateStore>.Instance, filePath);
            var state = new AutoServerSwitchPersistentState
            {
                LastSwitchUtc = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc),
                LastSwitchServer = "Singapore - Singapore #23",
                ProbeCheckCount = 4,
                RecommendationCount = 2,
                SwitchAttemptCount = 1,
                SuccessfulSwitchCount = 1,
                AvoidServersUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sg-singapore-20"] = new DateTime(2026, 4, 29, 12, 30, 0, DateTimeKind.Utc)
                },
                RecentEvents = new List<AutoServerSwitchEvent>
                {
                    new()
                    {
                        TimestampUtc = new DateTime(2026, 4, 29, 12, 1, 0, DateTimeKind.Utc),
                        Type = "Switch",
                        Message = "Switched to Singapore - Singapore #23",
                        Server = "Singapore - Singapore #23",
                        Score = 82.5
                    }
                }
            };

            store.Save(state);
            var loaded = store.Load();

            Assert.Equal(state.LastSwitchUtc, loaded.LastSwitchUtc);
            Assert.Equal(state.LastSwitchServer, loaded.LastSwitchServer);
            Assert.Equal(4, loaded.ProbeCheckCount);
            Assert.Equal(2, loaded.RecommendationCount);
            Assert.Equal(1, loaded.SuccessfulSwitchCount);
            Assert.Equal(state.AvoidServersUntil["sg-singapore-20"], loaded.AvoidServersUntil["sg-singapore-20"]);
            Assert.Single(loaded.RecentEvents);
            Assert.Equal("Switch", loaded.RecentEvents[0].Type);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
