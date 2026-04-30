using XNetwork.Models;

namespace XNetwork.Services;

public class RecommendationConfidenceTracker
{
    private string? _lastRecommendationTag;
    private int _confirmationCount;

    public RecommendationConfidenceResult Observe(ServerHealthScore recommendation, int requiredConfirmations, bool force)
    {
        var required = force ? 1 : Math.Max(1, requiredConfirmations);
        var key = GetRecommendationKey(recommendation);

        if (string.Equals(_lastRecommendationTag, key, StringComparison.OrdinalIgnoreCase))
        {
            _confirmationCount++;
        }
        else
        {
            _lastRecommendationTag = key;
            _confirmationCount = 1;
        }

        return new RecommendationConfidenceResult(
            HasConfidence: _confirmationCount >= required,
            ConfirmationCount: _confirmationCount,
            RequiredConfirmations: required,
            PendingRecommendationKey: key);
    }

    public void Reset()
    {
        _lastRecommendationTag = null;
        _confirmationCount = 0;
    }

    private static string GetRecommendationKey(ServerHealthScore recommendation)
    {
        return !string.IsNullOrWhiteSpace(recommendation.Tag)
            ? recommendation.Tag
            : $"{recommendation.Country}-{recommendation.City}-{recommendation.Num}";
    }
}

public sealed record RecommendationConfidenceResult(
    bool HasConfidence,
    int ConfirmationCount,
    int RequiredConfirmations,
    string PendingRecommendationKey);
