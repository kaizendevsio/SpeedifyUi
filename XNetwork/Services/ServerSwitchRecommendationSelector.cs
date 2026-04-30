using XNetwork.Models;

namespace XNetwork.Services;

public class ServerSwitchRecommendationSelector
{
    public ServerSwitchRecommendationResult Select(
        IEnumerable<ServerHealthScore> scores,
        ServerInfo? currentServer,
        AutoServerSwitchSettings settings,
        IReadOnlySet<string> avoidedTags,
        DateTime utcNow)
    {
        var restrictRegions = !string.IsNullOrWhiteSpace(settings.PreferredCountry) || settings.FallbackCountries.Count > 0;
        var freshScores = scores
            .Where(score => score.TestedUtc >= utcNow.AddMinutes(-Math.Max(1, settings.ProbeMaxScoreAgeMinutes)))
            .ToList();

        var currentScore = FindCurrentServerScore(freshScores, currentServer);
        if (settings.RequireProbeCurrentServerBad)
        {
            if (currentServer == null)
            {
                return new ServerSwitchRecommendationResult(null, null, "Current server is unknown; external probe cannot confirm it is bad");
            }

            if (currentScore == null)
            {
                return new ServerSwitchRecommendationResult(null, null, "External probe has no fresh score for the current server; waiting for current-server probe confirmation");
            }

            if (currentScore.WasSuccessful && currentScore.Score >= settings.MinimumProbeScore)
            {
                return new ServerSwitchRecommendationResult(
                    null,
                    currentScore,
                    $"External probe says current server score {currentScore.Score:N0}/100 is still acceptable; treating router health as local WAN issue");
            }
        }

        var scoreList = freshScores
            .Where(score => score.WasSuccessful)
            .Where(score => score.Score >= settings.MinimumProbeScore)
            .Where(score => settings.IncludePremiumServers || !score.IsPremium)
            .Where(score => settings.IncludePrivateServers || !score.IsPrivate)
            .Where(score => !restrictRegions || PreferredRank(score, settings) >= 0)
            .Where(score => !IsAvoided(score.Tag, avoidedTags))
            .OrderByDescending(score => PreferredRank(score, settings))
            .ThenByDescending(score => score.Score)
            .ThenBy(score => score.LatencyMs)
            .ToList();

        var best = scoreList.FirstOrDefault(score => !IsSameServer(score, currentServer));
        if (best == null)
        {
            return new ServerSwitchRecommendationResult(null, currentScore, "No eligible external probe recommendation");
        }

        if (currentScore != null && best.Score < currentScore.Score + settings.MinimumScoreImprovement)
        {
            return new ServerSwitchRecommendationResult(
                null,
                currentScore,
                $"External probe best score {best.Score:N0}/100 is not enough better than current score {currentScore.Score:N0}/100");
        }

        return new ServerSwitchRecommendationResult(best, currentScore, null);
    }

    private static int PreferredRank(ServerHealthScore score, AutoServerSwitchSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PreferredCountry) &&
            string.Equals(score.Country, settings.PreferredCountry.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(settings.PreferredCity) &&
                string.Equals(score.City, settings.PreferredCity.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            return 1;
        }

        return settings.FallbackCountries.Any(country => string.Equals(country, score.Country, StringComparison.OrdinalIgnoreCase)) ? 0 : -1;
    }

    private static ServerHealthScore? FindCurrentServerScore(IEnumerable<ServerHealthScore> scores, ServerInfo? currentServer)
    {
        return scores.FirstOrDefault(score => IsSameServer(score, currentServer));
    }

    private static bool IsSameServer(ServerHealthScore score, ServerInfo? server)
    {
        if (server == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(score.Tag) && !string.IsNullOrWhiteSpace(server.Tag))
        {
            return string.Equals(score.Tag, server.Tag, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(score.Country, server.Country, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(score.City, server.City, StringComparison.OrdinalIgnoreCase) &&
               score.Num == server.Num;
    }

    private static bool IsAvoided(string tag, IReadOnlySet<string> avoidedTags)
    {
        return !string.IsNullOrWhiteSpace(tag) && avoidedTags.Contains(tag);
    }
}

public sealed record ServerSwitchRecommendationResult(
    ServerHealthScore? Recommendation,
    ServerHealthScore? CurrentScore,
    string? RejectionReason);
