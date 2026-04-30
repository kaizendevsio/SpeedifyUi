using System.Net.Http.Headers;
using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

public class ProbeScoreClient(HttpClient httpClient, ILogger<ProbeScoreClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<ProbeScoresResponse?> GetScoresAsync(AutoServerSwitchSettings settings, ServerInfo? currentServer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ProbeApiBaseUrl))
        {
            return null;
        }

        var baseUri = new Uri(settings.ProbeApiBaseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildScoresUri(baseUri, settings, currentServer));
        if (!string.IsNullOrWhiteSpace(settings.ProbeApiKey))
        {
            request.Headers.Add("X-Api-Key", settings.ProbeApiKey.Trim());
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ProbeRequestTimeoutSeconds, 3, 60)));

        using var response = await httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Probe API returned {StatusCode}", response.StatusCode);
            return new ProbeScoresResponse
            {
                LastError = $"Probe API returned {(int)response.StatusCode} {response.ReasonPhrase}"
            };
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<ProbeScoresResponse>(stream, JsonOptions, timeoutCts.Token).ConfigureAwait(false);
    }

    private static Uri BuildScoresUri(Uri baseUri, AutoServerSwitchSettings settings, ServerInfo? currentServer)
    {
        var query = new List<string>
        {
            $"maxAgeMinutes={Math.Max(1, settings.ProbeMaxScoreAgeMinutes)}",
            "limit=50"
        };

        if (currentServer != null)
        {
            AddQueryValue(query, "currentTag", currentServer.Tag);
            AddQueryValue(query, "currentCountry", currentServer.Country);
            AddQueryValue(query, "currentCity", currentServer.City);
            if (currentServer.Num > 0)
            {
                query.Add($"currentNum={currentServer.Num}");
            }
        }

        return new Uri(baseUri, "scores?" + string.Join("&", query));
    }

    private static void AddQueryValue(List<string> query, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
        }
    }
}
