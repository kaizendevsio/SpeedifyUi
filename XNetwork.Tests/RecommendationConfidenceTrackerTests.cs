using XNetwork.Models;
using XNetwork.Services;
using XServerHealthScore = XNetwork.Models.ServerHealthScore;

namespace XNetwork.Tests;

public class RecommendationConfidenceTrackerTests
{
    [Fact]
    public void Observe_RequiresSameRecommendationForConfiguredConfirmations()
    {
        var tracker = new RecommendationConfidenceTracker();
        var recommendation = Score("sg-singapore-23");

        var first = tracker.Observe(recommendation, requiredConfirmations: 2, force: false);
        var second = tracker.Observe(recommendation, requiredConfirmations: 2, force: false);

        Assert.False(first.HasConfidence);
        Assert.Equal(1, first.ConfirmationCount);
        Assert.True(second.HasConfidence);
        Assert.Equal(2, second.ConfirmationCount);
    }

    [Fact]
    public void Observe_ResetsConfirmationCountWhenRecommendationChanges()
    {
        var tracker = new RecommendationConfidenceTracker();

        tracker.Observe(Score("sg-singapore-23"), requiredConfirmations: 2, force: false);
        var changed = tracker.Observe(Score("sg-singapore-24"), requiredConfirmations: 2, force: false);

        Assert.False(changed.HasConfidence);
        Assert.Equal(1, changed.ConfirmationCount);
        Assert.Equal("sg-singapore-24", changed.PendingRecommendationKey);
    }

    [Fact]
    public void Observe_ForceRequiresOnlyOneConfirmation()
    {
        var tracker = new RecommendationConfidenceTracker();

        var result = tracker.Observe(Score("sg-singapore-23"), requiredConfirmations: 3, force: true);

        Assert.True(result.HasConfidence);
        Assert.Equal(1, result.RequiredConfirmations);
    }

    private static XServerHealthScore Score(string tag)
    {
        return new XServerHealthScore
        {
            Tag = tag,
            Country = "sg",
            City = "singapore",
            Num = 23,
            FriendlyName = tag,
            WasSuccessful = true,
            Score = 80,
            TestedUtc = DateTime.UtcNow
        };
    }
}
