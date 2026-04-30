using XNetwork.Models;
using XNetwork.Services;
using XServerHealthScore = XNetwork.Models.ServerHealthScore;
using XServerInfo = XNetwork.Models.ServerInfo;

namespace XNetwork.Tests;

public class ServerSwitchRecommendationSelectorTests
{
    private static readonly DateTime Now = new(2026, 4, 26, 12, 0, 0, DateTimeKind.Utc);
    private readonly ServerSwitchRecommendationSelector _selector = new();

    [Fact]
    public void Select_ReturnsBestPreferredServer()
    {
        var settings = CreateSettings();
        var scores = new[]
        {
            Score("jp-tokyo-1", "jp", "tokyo", 1, 95, 60),
            Score("sg-singapore-20", "sg", "singapore", 20, 76, 34),
            Score("sg-singapore-23", "sg", "singapore", 23, 78, 32)
        };

        var result = _selector.Select(scores, currentServer: null, settings, EmptyAvoidedTags(), Now);

        Assert.Equal("sg-singapore-23", result.Recommendation?.Tag);
        Assert.Null(result.CurrentScore);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public void Select_IgnoresIneligibleScores()
    {
        var settings = CreateSettings();
        var scores = new[]
        {
            Score("sg-singapore-1", "sg", "singapore", 1, 99, 20, testedUtc: Now.AddMinutes(-31)),
            Score("sg-singapore-2", "sg", "singapore", 2, 98, 20, wasSuccessful: false),
            Score("sg-singapore-3", "sg", "singapore", 3, 69, 20),
            Score("sg-singapore-4", "sg", "singapore", 4, 97, 20),
            Score("sg-singapore-5", "sg", "singapore", 5, 75, 35)
        };

        var result = _selector.Select(
            scores,
            currentServer: null,
            settings,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sg-singapore-4" },
            Now);

        Assert.Equal("sg-singapore-5", result.Recommendation?.Tag);
    }

    [Fact]
    public void Select_ReturnsNullWhenBestServerIsNotEnoughBetterThanCurrentServer()
    {
        var settings = CreateSettings(minimumScoreImprovement: 5);
        var currentServer = Server("sg-singapore-20", "sg", "singapore", 20);
        var scores = new[]
        {
            Score("sg-singapore-20", "sg", "singapore", 20, 75, 35),
            Score("sg-singapore-23", "sg", "singapore", 23, 79, 32)
        };

        var result = _selector.Select(scores, currentServer, settings, EmptyAvoidedTags(), Now);

        Assert.Null(result.Recommendation);
        Assert.Equal("sg-singapore-20", result.CurrentScore?.Tag);
        Assert.Contains("not enough better", result.RejectionReason);
    }

    [Fact]
    public void Select_ReturnsRecommendationWhenBestServerImprovesEnough()
    {
        var settings = CreateSettings(minimumScoreImprovement: 5);
        var currentServer = Server("sg-singapore-20", "sg", "singapore", 20);
        var scores = new[]
        {
            Score("sg-singapore-20", "sg", "singapore", 20, 75, 35),
            Score("sg-singapore-23", "sg", "singapore", 23, 80, 32)
        };

        var result = _selector.Select(scores, currentServer, settings, EmptyAvoidedTags(), Now);

        Assert.Equal("sg-singapore-23", result.Recommendation?.Tag);
        Assert.Equal("sg-singapore-20", result.CurrentScore?.Tag);
    }

    [Fact]
    public void Select_MatchesCurrentServerByCountryCityAndNumberWhenTagIsMissing()
    {
        var settings = CreateSettings(minimumScoreImprovement: 5);
        var currentServer = Server(tag: "", "sg", "singapore", 20);
        var scores = new[]
        {
            Score(tag: "", "sg", "singapore", 20, 77, 35),
            Score("sg-singapore-23", "sg", "singapore", 23, 83, 32)
        };

        var result = _selector.Select(scores, currentServer, settings, EmptyAvoidedTags(), Now);

        Assert.Equal(77, result.CurrentScore?.Score);
        Assert.Equal("sg-singapore-23", result.Recommendation?.Tag);
    }

    [Fact]
    public void Select_FallsBackToFallbackCountriesWhenPreferredCountryHasNoEligibleScore()
    {
        var settings = CreateSettings();
        var scores = new[]
        {
            Score("us-newark-1", "us", "newark", 1, 99, 170),
            Score("jp-tokyo-24", "jp", "tokyo", 24, 73, 60)
        };

        var result = _selector.Select(scores, currentServer: null, settings, EmptyAvoidedTags(), Now);

        Assert.Equal("jp-tokyo-24", result.Recommendation?.Tag);
    }

    [Fact]
    public void Select_IgnoresPremiumAndPrivateServersUnlessAllowed()
    {
        var settings = CreateSettings(includePremium: false, includePrivate: false);
        var scores = new[]
        {
            Score("sg-singapore-premium", "sg", "singapore", 1, 99, 20, isPremium: true),
            Score("sg-singapore-private", "sg", "singapore", 2, 98, 20, isPrivate: true),
            Score("sg-singapore-public", "sg", "singapore", 3, 74, 35)
        };

        var result = _selector.Select(scores, currentServer: null, settings, EmptyAvoidedTags(), Now);

        Assert.Equal("sg-singapore-public", result.Recommendation?.Tag);
    }

    [Fact]
    public void Select_BlocksWhenCurrentServerScoreIsMissingAndConfirmationIsRequired()
    {
        var settings = CreateSettings(requireCurrentServerBad: true);
        var currentServer = Server("sg-singapore-20", "sg", "singapore", 20);
        var scores = new[]
        {
            Score("sg-singapore-23", "sg", "singapore", 23, 90, 30)
        };

        var result = _selector.Select(scores, currentServer, settings, EmptyAvoidedTags(), Now);

        Assert.Null(result.Recommendation);
        Assert.Null(result.CurrentScore);
        Assert.Contains("no fresh score", result.RejectionReason);
    }

    [Fact]
    public void Select_BlocksWhenProbeSaysCurrentServerIsAcceptable()
    {
        var settings = CreateSettings(requireCurrentServerBad: true);
        var currentServer = Server("sg-singapore-20", "sg", "singapore", 20);
        var scores = new[]
        {
            Score("sg-singapore-20", "sg", "singapore", 20, 78, 35),
            Score("sg-singapore-23", "sg", "singapore", 23, 95, 30)
        };

        var result = _selector.Select(scores, currentServer, settings, EmptyAvoidedTags(), Now);

        Assert.Null(result.Recommendation);
        Assert.Equal("sg-singapore-20", result.CurrentScore?.Tag);
        Assert.Contains("still acceptable", result.RejectionReason);
    }

    [Fact]
    public void Select_AllowsSwitchWhenProbeSaysCurrentServerIsBad()
    {
        var settings = CreateSettings(requireCurrentServerBad: true, minimumScoreImprovement: 5);
        var currentServer = Server("sg-singapore-20", "sg", "singapore", 20);
        var scores = new[]
        {
            Score("sg-singapore-20", "sg", "singapore", 20, 60, 90),
            Score("sg-singapore-23", "sg", "singapore", 23, 80, 30)
        };

        var result = _selector.Select(scores, currentServer, settings, EmptyAvoidedTags(), Now);

        Assert.Equal("sg-singapore-23", result.Recommendation?.Tag);
        Assert.Equal("sg-singapore-20", result.CurrentScore?.Tag);
    }

    private static AutoServerSwitchSettings CreateSettings(
        double minimumScore = 70,
        double minimumScoreImprovement = 5,
        bool includePremium = false,
        bool includePrivate = false,
        bool requireCurrentServerBad = false)
    {
        return new AutoServerSwitchSettings
        {
            PreferredCountry = "sg",
            PreferredCity = "singapore",
            FallbackCountries = new List<string> { "hk", "jp", "ph" },
            ProbeMaxScoreAgeMinutes = 30,
            MinimumProbeScore = minimumScore,
            MinimumScoreImprovement = minimumScoreImprovement,
            IncludePremiumServers = includePremium,
            IncludePrivateServers = includePrivate,
            RequireProbeCurrentServerBad = requireCurrentServerBad
        };
    }

    private static XServerHealthScore Score(
        string tag,
        string country,
        string city,
        int num,
        double score,
        double latencyMs,
        DateTime? testedUtc = null,
        bool wasSuccessful = true,
        bool isPremium = false,
        bool isPrivate = false)
    {
        return new XServerHealthScore
        {
            Tag = tag,
            FriendlyName = $"{country}-{city}-{num}",
            Country = country,
            City = city,
            Num = num,
            TestedUtc = testedUtc ?? Now,
            LatencyMs = latencyMs,
            JitterMs = 1,
            SuccessRate = 100,
            Score = score,
            WasSuccessful = wasSuccessful,
            IsPremium = isPremium,
            IsPrivate = isPrivate
        };
    }

    private static XServerInfo Server(string tag, string country, string city, int num)
    {
        return new XServerInfo
        {
            Tag = tag,
            Country = country,
            City = city,
            Num = num,
            FriendlyName = $"{country}-{city}-{num}"
        };
    }

    private static HashSet<string> EmptyAvoidedTags()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}
