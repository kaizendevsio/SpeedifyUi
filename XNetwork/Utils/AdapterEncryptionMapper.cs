using XNetwork.Models;

namespace XNetwork.Utils;

public static class AdapterEncryptionMapper
{
    public static void Apply(IReadOnlyCollection<AdapterExtended>? adapters, SpeedifySettings? settings)
    {
        if (adapters == null || settings == null)
        {
            return;
        }

        var perAdapterSettings = settings.PerConnectionEncryptionEnabled
            ? settings.PerConnectionEncryptionSettings
                .Where(item => !string.IsNullOrWhiteSpace(item.AdapterId))
                .GroupBy(item => item.AdapterId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Encrypted, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in adapters)
        {
            adapter.EncryptionEnabled = perAdapterSettings.TryGetValue(adapter.AdapterId, out var encrypted)
                ? encrypted
                : settings.Encrypted;
        }
    }
}
