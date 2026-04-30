using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ProbeSettings>(builder.Configuration.GetSection("Probe"));
builder.Services.AddSingleton<ProbeState>();
builder.Services.AddSingleton<SpeedifyCli>();
builder.Services.AddSingleton<SpeedifyProbeService>();
builder.Services.AddHostedService<ProbeWorker>();

var app = builder.Build();

app.MapGet("/health", (ProbeState state) => Results.Ok(new
{
    status = "ok",
    isRunning = state.IsRunning,
    lastRunUtc = state.LastRunUtc,
    lastCompletedUtc = state.LastCompletedUtc,
    lastError = state.LastError,
    scoreCount = state.GetScores().Count,
    priorityHintCount = state.GetPriorityHints(TimeSpan.FromHours(1)).Count
}));

app.MapGet("/scores", (HttpContext context, ProbeState state, Microsoft.Extensions.Options.IOptions<ProbeSettings> options) =>
{
    if (!IsAuthorized(context, options.Value))
    {
        return Results.Unauthorized();
    }

    RecordCurrentServerHint(context, state);

    var country = context.Request.Query["country"].ToString();
    var city = context.Request.Query["city"].ToString();
    var maxAgeMinutes = TryReadInt(context.Request.Query["maxAgeMinutes"].ToString());
    var limit = TryReadInt(context.Request.Query["limit"].ToString()) ?? 20;
    var cutoff = DateTime.UtcNow.AddMinutes(-(maxAgeMinutes ?? Math.Max(1, options.Value.ScoreRetentionMinutes)));

    var scores = state.GetScores()
        .Where(score => score.TestedUtc >= cutoff)
        .Where(score => string.IsNullOrWhiteSpace(country) || string.Equals(score.Country, country, StringComparison.OrdinalIgnoreCase))
        .Where(score => string.IsNullOrWhiteSpace(city) || string.Equals(score.City, city, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(score => score.WasSuccessful)
        .ThenByDescending(score => score.Score)
        .ThenBy(score => score.LatencyMs)
        .Take(Math.Clamp(limit, 1, 100))
        .ToList();

    return Results.Ok(new ProbeScoresResponse
    {
        GeneratedUtc = DateTime.UtcNow,
        LastRunUtc = state.LastRunUtc,
        LastCompletedUtc = state.LastCompletedUtc,
        LastError = state.LastError,
        Scores = scores
    });
});

app.MapPost("/probe/run", async (HttpContext context, SpeedifyProbeService probeService, Microsoft.Extensions.Options.IOptions<ProbeSettings> options, CancellationToken cancellationToken) =>
{
    if (!IsAuthorized(context, options.Value))
    {
        return Results.Unauthorized();
    }

    var result = await probeService.RunOnceAsync(force: true, cancellationToken).ConfigureAwait(false);
    return Results.Ok(result);
});

app.Run();

static bool IsAuthorized(HttpContext context, ProbeSettings settings)
{
    if (string.IsNullOrWhiteSpace(settings.ApiKey))
    {
        return true;
    }

    return context.Request.Headers.TryGetValue("X-Api-Key", out var apiKey) &&
           string.Equals(apiKey.ToString(), settings.ApiKey, StringComparison.Ordinal);
}

static int? TryReadInt(string value)
{
    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
}

static void RecordCurrentServerHint(HttpContext context, ProbeState state)
{
    var tag = context.Request.Query["currentTag"].ToString();
    var country = context.Request.Query["currentCountry"].ToString();
    var city = context.Request.Query["currentCity"].ToString();
    var num = TryReadInt(context.Request.Query["currentNum"].ToString()) ?? 0;
    if (string.IsNullOrWhiteSpace(tag) && string.IsNullOrWhiteSpace(country))
    {
        return;
    }

    state.RecordPriorityHint(new ProbeServerHint(
        tag.Trim(),
        country.Trim(),
        city.Trim(),
        num,
        DateTime.UtcNow));
}

public sealed class ProbeWorker(
    SpeedifyProbeService probeService,
    ProbeState state,
    Microsoft.Extensions.Options.IOptions<ProbeSettings> options,
    ILogger<ProbeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRegularRunUtc = DateTime.MinValue;
        var nextPriorityRunUtc = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = options.Value;
                if (settings.Enabled)
                {
                    var now = DateTime.UtcNow;
                    if (now >= nextRegularRunUtc)
                    {
                        await probeService.RunOnceAsync(force: false, priorityOnly: false, stoppingToken).ConfigureAwait(false);
                        now = DateTime.UtcNow;
                        nextRegularRunUtc = now.AddMinutes(Math.Max(1, settings.IntervalMinutes));
                        nextPriorityRunUtc = now.AddSeconds(Math.Max(30, settings.PriorityProbeIntervalSeconds));
                    }
                    else if (now >= nextPriorityRunUtc && state.HasPriorityHints(TimeSpan.FromMinutes(Math.Max(1, settings.PriorityHintRetentionMinutes))))
                    {
                        await probeService.RunOnceAsync(force: false, priorityOnly: true, stoppingToken).ConfigureAwait(false);
                        nextPriorityRunUtc = DateTime.UtcNow.AddSeconds(Math.Max(30, settings.PriorityProbeIntervalSeconds));
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in Speedify probe worker");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

public sealed class SpeedifyProbeService(
    SpeedifyCli speedifyCli,
    ProbeState state,
    Microsoft.Extensions.Options.IOptions<ProbeSettings> options,
    ILogger<SpeedifyProbeService> logger)
{
    private static readonly HttpClient ThroughputHttpClient = new();
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private int _candidateCursor;

    public Task<ProbeRunResult> RunOnceAsync(bool force, CancellationToken cancellationToken)
    {
        return RunOnceAsync(force, priorityOnly: false, cancellationToken);
    }

    public async Task<ProbeRunResult> RunOnceAsync(bool force, bool priorityOnly, CancellationToken cancellationToken)
    {
        if (!force && state.IsRunning)
        {
            return new ProbeRunResult(false, "Probe already running", state.GetScores());
        }

        if (!await _probeLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new ProbeRunResult(false, "Probe already running", state.GetScores());
        }

        state.StartRun();
        try
        {
            var settings = options.Value;
            var directory = await speedifyCli.GetServersAsync(cancellationToken).ConfigureAwait(false);
            var candidates = SelectCandidates(directory, settings, priorityOnly);
            if (candidates.Count == 0)
            {
                var message = priorityOnly
                    ? "No current-server priority hint matched the Speedify server directory"
                    : "No Speedify servers matched the probe filters";
                state.CompleteRun(priorityOnly ? null : message);
                return new ProbeRunResult(priorityOnly, message, state.GetScores());
            }

            logger.LogInformation("Probing {Count} Speedify server candidates{Mode}", candidates.Count, priorityOnly ? " for current-server priority check" : "");
            var scores = new List<ServerHealthScore>();
            foreach (var server in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var score = await ProbeServerAsync(server, settings, cancellationToken).ConfigureAwait(false);
                state.RecordScore(score);
                scores.Add(score);
            }

            state.RemoveScoresOlderThan(TimeSpan.FromMinutes(Math.Max(1, settings.ScoreRetentionMinutes)));
            state.CompleteRun(null);
            return new ProbeRunResult(true, null, scores.OrderByDescending(score => score.Score).ToList());
        }
        catch (OperationCanceledException)
        {
            state.CompleteRun("Probe was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Speedify probe cycle failed");
            state.CompleteRun(ex.Message);
            return new ProbeRunResult(false, ex.Message, state.GetScores());
        }
        finally
        {
            _probeLock.Release();
        }
    }

    private List<ServerInfo> SelectCandidates(ServerDirectory directory, ProbeSettings settings, bool priorityOnly)
    {
        var allServers = directory.Public.AsEnumerable();
        if (settings.IncludePrivateServers)
        {
            allServers = allServers.Concat(directory.Private);
        }

        var allServerList = allServers
            .Where(server => !string.IsNullOrWhiteSpace(server.Tag) || !string.IsNullOrWhiteSpace(server.Country))
            .ToList();
        var priorityCandidates = FindPriorityCandidates(allServerList, settings);
        if (priorityOnly)
        {
            return priorityCandidates
                .Take(Math.Clamp(settings.MaxPriorityCandidatesPerCycle, 1, Math.Max(1, priorityCandidates.Count)))
                .ToList();
        }

        var countries = settings.Countries
            .Where(country => !string.IsNullOrWhiteSpace(country))
            .Select(country => country.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cities = settings.Cities
            .Where(city => !string.IsNullOrWhiteSpace(city))
            .Select(city => city.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var servers = allServerList.AsEnumerable();

        if (!settings.IncludePremiumServers)
        {
            servers = servers.Where(server => !server.IsPremium);
        }

        if (countries.Count > 0)
        {
            servers = servers.Where(server => countries.Contains(server.Country));
        }

        if (cities.Count > 0)
        {
            servers = servers.Where(server => cities.Contains(server.City));
        }

        var ordered = servers
            .Where(server => !string.IsNullOrWhiteSpace(server.Tag) || !string.IsNullOrWhiteSpace(server.Country))
            .OrderBy(server => server.Country)
            .ThenBy(server => server.City)
            .ThenBy(server => server.Num)
            .ToList();

        if (ordered.Count == 0)
        {
            return priorityCandidates
                .Take(Math.Clamp(settings.MaxCandidatesPerCycle, 1, Math.Max(1, priorityCandidates.Count)))
                .ToList();
        }

        var maxCandidates = Math.Clamp(settings.MaxCandidatesPerCycle, 1, ordered.Count);
        var start = Math.Abs(_candidateCursor) % ordered.Count;
        _candidateCursor = (_candidateCursor + maxCandidates) % ordered.Count;
        var regularCandidates = ordered
            .Skip(start)
            .Concat(ordered.Take(start))
            .Where(server => !priorityCandidates.Any(priority => IsSameServer(priority, server)))
            .ToList();

        return priorityCandidates
            .Concat(regularCandidates)
            .Take(maxCandidates)
            .ToList();
    }

    private List<ServerInfo> FindPriorityCandidates(List<ServerInfo> servers, ProbeSettings settings)
    {
        var hints = state.GetPriorityHints(TimeSpan.FromMinutes(Math.Max(1, settings.PriorityHintRetentionMinutes)));
        if (hints.Count == 0)
        {
            return new List<ServerInfo>();
        }

        var candidates = new List<ServerInfo>();
        foreach (var hint in hints)
        {
            var match = servers.FirstOrDefault(server => IsSameServer(server, hint));
            if (match != null && !candidates.Any(existing => IsSameServer(existing, match)))
            {
                candidates.Add(match);
            }
        }

        return candidates;
    }

    private static bool IsSameServer(ServerInfo server, ProbeServerHint hint)
    {
        if (!string.IsNullOrWhiteSpace(server.Tag) && !string.IsNullOrWhiteSpace(hint.Tag))
        {
            return string.Equals(server.Tag, hint.Tag, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(server.Country, hint.Country, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(server.City, hint.City, StringComparison.OrdinalIgnoreCase) &&
               server.Num == hint.Num;
    }

    private static bool IsSameServer(ServerInfo left, ServerInfo right)
    {
        if (!string.IsNullOrWhiteSpace(left.Tag) && !string.IsNullOrWhiteSpace(right.Tag))
        {
            return string.Equals(left.Tag, right.Tag, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.Country, right.Country, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.City, right.City, StringComparison.OrdinalIgnoreCase) &&
               left.Num == right.Num;
    }

    private async Task<ServerHealthScore> ProbeServerAsync(ServerInfo server, ProbeSettings settings, CancellationToken cancellationToken)
    {
        var friendlyName = FormatServer(server);
        try
        {
            logger.LogInformation("Connecting probe daemon to {Server}", friendlyName);
            await speedifyCli.ConnectToServerAsync(server, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.SettleSeconds, 3, 120)), cancellationToken).ConfigureAwait(false);

            var samples = new List<PingSample>();
            var sampleCount = Math.Clamp(settings.SampleCount, 1, 30);
            var interval = TimeSpan.FromSeconds(Math.Clamp(settings.SampleIntervalSeconds, 1, 30));
            for (var i = 0; i < sampleCount; i++)
            {
                samples.AddRange(await SampleTargetsAsync(settings.PingTargets, cancellationToken).ConfigureAwait(false));
                if (i < sampleCount - 1)
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
            }

            var throughput = await MeasureThroughputAsync(settings, cancellationToken).ConfigureAwait(false);
            return BuildScore(server, samples, settings, null, throughput);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Probe failed for {Server}", friendlyName);
            return BuildScore(server, new List<PingSample>(), settings, ex.Message, ThroughputMeasurement.Empty);
        }
    }

    private static async Task<List<PingSample>> SampleTargetsAsync(IEnumerable<string> targets, CancellationToken cancellationToken)
    {
        var samples = new List<PingSample>();
        foreach (var target in targets.Where(target => !string.IsNullOrWhiteSpace(target)))
        {
            using var ping = new Ping();
            try
            {
                var reply = await ping.SendPingAsync(target.Trim(), 2000).WaitAsync(cancellationToken).ConfigureAwait(false);
                samples.Add(reply.Status == IPStatus.Success
                    ? new PingSample(true, reply.RoundtripTime)
                    : new PingSample(false, 0));
            }
            catch
            {
                samples.Add(new PingSample(false, 0));
            }
        }

        return samples;
    }

    private async Task<ThroughputMeasurement> MeasureThroughputAsync(ProbeSettings settings, CancellationToken cancellationToken)
    {
        var urls = settings.ThroughputTestUrls.Where(url => !string.IsNullOrWhiteSpace(url)).ToList();
        if (urls.Count == 0)
        {
            return ThroughputMeasurement.Empty;
        }

        var targetBytes = Math.Clamp(settings.ThroughputSampleBytes, 256 * 1024, 10 * 1024 * 1024);
        var sampleCount = Math.Clamp(settings.ThroughputSampleCount, 1, 5);
        var parallelDownloads = Math.Clamp(settings.ThroughputParallelDownloads, 1, 4);
        var timeoutSeconds = Math.Clamp(settings.ThroughputTimeoutSeconds, 3, 30);
        var results = new List<ThroughputSampleResult>();

        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            foreach (var url in urls)
            {
                var result = await MeasureThroughputSampleAsync(url, targetBytes, parallelDownloads, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                if (result.Mbps > 0)
                {
                    results.Add(result);
                    break;
                }
            }
        }

        if (results.Count == 0)
        {
            return new ThroughputMeasurement(null, 0, 0, sampleCount, parallelDownloads, targetBytes);
        }

        var chosenMbps = SelectThroughputMbps(results.Select(result => result.Mbps).ToList());
        var confidence = Math.Clamp(results.Average(result => result.Confidence) * results.Count / sampleCount, 0, 1);
        return new ThroughputMeasurement(Math.Round(chosenMbps, 3), Math.Round(confidence, 2), results.Count, sampleCount, parallelDownloads, targetBytes);
    }

    private async Task<ThroughputSampleResult> MeasureThroughputSampleAsync(
        string url,
        int targetBytes,
        int parallelDownloads,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var stopwatch = Stopwatch.StartNew();
            var downloads = Enumerable.Range(0, parallelDownloads)
                .Select(_ => DownloadThroughputBytesAsync(url, targetBytes, timeoutCts.Token, cancellationToken))
                .ToArray();
            var byteCounts = await Task.WhenAll(downloads).ConfigureAwait(false);
            stopwatch.Stop();

            var totalBytes = byteCounts.Sum();
            if (totalBytes <= 0 || stopwatch.Elapsed.TotalSeconds <= 0)
            {
                return ThroughputSampleResult.Empty;
            }

            var mbps = totalBytes * 8.0 / stopwatch.Elapsed.TotalSeconds / 1_000_000.0;
            var confidence = Math.Clamp(totalBytes / (double)(targetBytes * parallelDownloads), 0, 1);
            return new ThroughputSampleResult(mbps, confidence);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Throughput sample URL {Url} failed", url);
            return ThroughputSampleResult.Empty;
        }
    }

    private async Task<int> DownloadThroughputBytesAsync(string url, int targetBytes, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url.Trim());
            request.Headers.ConnectionClose = true;
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            using var response = await ThroughputHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Throughput test URL {Url} returned {StatusCode}", url, response.StatusCode);
                return 0;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutToken).ConfigureAwait(false);
            var buffer = new byte[128 * 1024];
            var bytesRead = 0;
            while (bytesRead < targetBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, targetBytes - bytesRead)), timeoutToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
            }

            return bytesRead;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Throughput download URL {Url} failed", url);
            return 0;
        }
    }

    private static double SelectThroughputMbps(List<double> measurements)
    {
        var ordered = measurements.Order().ToList();
        return ordered.Count switch
        {
            0 => 0,
            1 => ordered[0],
            2 => ordered[1],
            _ => ordered[ordered.Count / 2]
        };
    }

    private static ServerHealthScore BuildScore(ServerInfo server, List<PingSample> samples, ProbeSettings settings, string? failureReason, ThroughputMeasurement throughput)
    {
        var successful = samples.Where(sample => sample.Success).Select(sample => (double)sample.LatencyMs).ToList();
        var successRate = samples.Count == 0 ? 0 : successful.Count * 100.0 / samples.Count;
        var latency = successful.Count == 0 ? 0 : successful.Average();
        var jitter = CalculateJitter(successful);
        var wasSuccessful = successful.Count > 0 && successRate > 0 && failureReason == null;

        return new ServerHealthScore
        {
            Tag = server.Tag,
            FriendlyName = FormatServer(server),
            Country = server.Country,
            City = server.City,
            Num = server.Num,
            IsPremium = server.IsPremium,
            IsPrivate = server.IsPrivate,
            DataCenter = server.DataCenter,
            TestedUtc = DateTime.UtcNow,
            LatencyMs = latency,
            JitterMs = jitter,
            SuccessRate = successRate,
            ThroughputMbps = throughput.Mbps,
            ThroughputConfidence = throughput.Confidence,
            ThroughputSampleCount = throughput.SuccessfulSamples,
            ThroughputAttemptCount = throughput.AttemptedSamples,
            ThroughputParallelDownloads = throughput.ParallelDownloads,
            ThroughputSampleBytes = throughput.SampleBytes,
            Score = wasSuccessful ? ProbeScoreCalculator.Calculate(latency, jitter, successRate, throughput.Mbps, settings, throughput.Confidence) : 0,
            WasSuccessful = wasSuccessful,
            FailureReason = failureReason
        };
    }

    private static double CalculateJitter(IReadOnlyList<double> latencies)
    {
        if (latencies.Count < 2)
        {
            return 0;
        }

        var totalDelta = 0.0;
        for (var i = 1; i < latencies.Count; i++)
        {
            totalDelta += Math.Abs(latencies[i] - latencies[i - 1]);
        }

        return totalDelta / (latencies.Count - 1);
    }

    private static string FormatServer(ServerInfo server)
    {
        return !string.IsNullOrWhiteSpace(server.FriendlyName)
            ? server.FriendlyName
            : $"{server.Country} {server.City} #{server.Num}".Trim();
    }
}

public sealed class SpeedifyCli(ILogger<SpeedifyCli> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<ServerDirectory> GetServersAsync(CancellationToken cancellationToken)
    {
        var json = await RunAsync("show servers", cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ServerDirectory>(json, JsonOptions) ?? new ServerDirectory();
    }

    public async Task ConnectToServerAsync(ServerInfo server, CancellationToken cancellationToken)
    {
        var command = !string.IsNullOrWhiteSpace(server.Tag)
            ? $"connect {QuoteCliArgument(server.Tag)}"
            : BuildConnectCommand(server);
        await RunAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "speedify_cli",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("speedify_cli failed to start");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                logger.LogDebug("speedify_cli stderr for {Arguments}: {Error}", arguments, error.Trim());
            }

            return output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
    }

    private static string BuildConnectCommand(ServerInfo server)
    {
        var args = new List<string> { "connect", QuoteCliArgument(server.Country.Trim().ToLowerInvariant()) };
        if (!string.IsNullOrWhiteSpace(server.City))
        {
            args.Add(QuoteCliArgument(server.City.Trim()));
        }

        if (server.Num > 0)
        {
            args.Add(server.Num.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(" ", args);
    }

    private static string QuoteCliArgument(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "\"\"" : $"\"{value.Replace("\"", "\\\"")}\"";
    }
}

public sealed class ProbeState
{
    private readonly ConcurrentDictionary<string, ServerHealthScore> _scores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProbeServerHint> _priorityHints = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning { get; private set; }

    public DateTime? LastRunUtc { get; private set; }

    public DateTime? LastCompletedUtc { get; private set; }

    public string? LastError { get; private set; }

    public void StartRun()
    {
        IsRunning = true;
        LastRunUtc = DateTime.UtcNow;
        LastError = null;
    }

    public void CompleteRun(string? error)
    {
        IsRunning = false;
        LastCompletedUtc = DateTime.UtcNow;
        LastError = error;
    }

    public void RecordScore(ServerHealthScore score)
    {
        var key = string.IsNullOrWhiteSpace(score.Tag)
            ? $"{score.Country}-{score.City}-{score.Num}"
            : score.Tag;
        _scores[key] = score;
    }

    public List<ServerHealthScore> GetScores()
    {
        return _scores.Values
            .OrderByDescending(score => score.WasSuccessful)
            .ThenByDescending(score => score.Score)
            .ThenByDescending(score => score.TestedUtc)
            .ToList();
    }

    public void RecordPriorityHint(ProbeServerHint hint)
    {
        var key = BuildServerKey(hint.Tag, hint.Country, hint.City, hint.Num);
        if (!string.IsNullOrWhiteSpace(key))
        {
            _priorityHints[key] = hint;
        }
    }

    public List<ProbeServerHint> GetPriorityHints(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var item in _priorityHints.Where(item => item.Value.ReceivedUtc < cutoff).ToList())
        {
            _priorityHints.TryRemove(item.Key, out _);
        }

        return _priorityHints.Values
            .OrderByDescending(hint => hint.ReceivedUtc)
            .ToList();
    }

    public bool HasPriorityHints(TimeSpan maxAge)
    {
        return GetPriorityHints(maxAge).Count > 0;
    }

    public void RemoveScoresOlderThan(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var score in _scores.Where(kvp => kvp.Value.TestedUtc < cutoff).ToList())
        {
            _scores.TryRemove(score.Key, out _);
        }
    }

    private static string BuildServerKey(string tag, string country, string city, int num)
    {
        return string.IsNullOrWhiteSpace(tag)
            ? $"{country}-{city}-{num}".ToLowerInvariant()
            : tag.Trim().ToLowerInvariant();
    }
}

public sealed record ProbeServerHint(string Tag, string Country, string City, int Num, DateTime ReceivedUtc);

public sealed class ProbeSettings
{
    public bool Enabled { get; set; } = true;

    public string ApiKey { get; set; } = string.Empty;

    public int IntervalMinutes { get; set; } = 10;

    public int PriorityProbeIntervalSeconds { get; set; } = 60;

    public int PriorityHintRetentionMinutes { get; set; } = 60;

    public List<string> Countries { get; set; } = new() { "sg", "hk", "jp", "ph" };

    public List<string> Cities { get; set; } = new();

    public bool IncludePremiumServers { get; set; }

    public bool IncludePrivateServers { get; set; }

    public int MaxCandidatesPerCycle { get; set; } = 6;

    public int MaxPriorityCandidatesPerCycle { get; set; } = 1;

    public int SettleSeconds { get; set; } = 15;

    public int SampleCount { get; set; } = 5;

    public int SampleIntervalSeconds { get; set; } = 2;

    public List<string> PingTargets { get; set; } = new() { "1.1.1.1", "8.8.8.8" };

    public List<string> ThroughputTestUrls { get; set; } = new() { "https://speed.cloudflare.com/__down?bytes=5000000" };

    public int ThroughputSampleBytes { get; set; } = 3_000_000;

    public int ThroughputSampleCount { get; set; } = 2;

    public int ThroughputParallelDownloads { get; set; } = 2;

    public int ThroughputTimeoutSeconds { get; set; } = 10;

    public double MaxThroughputMbps { get; set; } = 100;

    public double MaxLatencyMs { get; set; } = 250;

    public double MaxJitterMs { get; set; } = 80;

    public int ScoreRetentionMinutes { get; set; } = 180;
}

public sealed class ProbeScoresResponse
{
    public DateTime GeneratedUtc { get; set; }

    public DateTime? LastRunUtc { get; set; }

    public DateTime? LastCompletedUtc { get; set; }

    public string? LastError { get; set; }

    public List<ServerHealthScore> Scores { get; set; } = new();
}

public sealed record ProbeRunResult(bool Success, string? Message, List<ServerHealthScore> Scores);

public sealed record PingSample(bool Success, long LatencyMs);

public sealed record ThroughputMeasurement(
    double? Mbps,
    double Confidence,
    int SuccessfulSamples,
    int AttemptedSamples,
    int ParallelDownloads,
    int SampleBytes)
{
    public static ThroughputMeasurement Empty { get; } = new(null, 0, 0, 0, 0, 0);
}

public sealed record ThroughputSampleResult(double Mbps, double Confidence)
{
    public static ThroughputSampleResult Empty { get; } = new(0, 0);
}

public static class ProbeScoreCalculator
{
    public static double Calculate(double latency, double jitter, double successRate, double? throughputMbps, ProbeSettings settings, double throughputConfidence = 1)
    {
        var latencyScore = Math.Clamp(1 - latency / Math.Max(1, settings.MaxLatencyMs), 0, 1);
        var jitterScore = Math.Clamp(1 - jitter / Math.Max(1, settings.MaxJitterMs), 0, 1);
        var successScore = Math.Clamp(successRate / 100.0, 0, 1);

        if (throughputMbps.HasValue)
        {
            var throughputScore = Math.Clamp(throughputMbps.Value / Math.Max(1, settings.MaxThroughputMbps), 0, 1);
            var throughputWeight = 15 * Math.Clamp(throughputConfidence, 0, 1);
            var remainingWeight = 100 - throughputWeight;
            return Math.Round(
                successScore * remainingWeight * 0.60 +
                latencyScore * remainingWeight * 0.25 +
                jitterScore * remainingWeight * 0.15 +
                throughputScore * throughputWeight,
                1);
        }

        return Math.Round(successScore * 60 + latencyScore * 25 + jitterScore * 15, 1);
    }
}

public sealed class ServerDirectory
{
    [JsonPropertyName("public")]
    public List<ServerInfo> Public { get; set; } = new();

    [JsonPropertyName("private")]
    public List<ServerInfo> Private { get; set; } = new();
}

public sealed class ServerInfo
{
    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    [JsonPropertyName("datacenter")]
    public string DataCenter { get; set; } = string.Empty;

    [JsonPropertyName("friendlyName")]
    public string FriendlyName { get; set; } = string.Empty;

    [JsonPropertyName("isPremium")]
    public bool IsPremium { get; set; }

    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("num")]
    public int Num { get; set; }

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;
}

public sealed class ServerHealthScore
{
    public string Tag { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public int Num { get; set; }

    public bool IsPremium { get; set; }

    public bool IsPrivate { get; set; }

    public string DataCenter { get; set; } = string.Empty;

    public DateTime TestedUtc { get; set; }

    public double LatencyMs { get; set; }

    public double JitterMs { get; set; }

    public double SuccessRate { get; set; }

    public double? ThroughputMbps { get; set; }

    public double ThroughputConfidence { get; set; }

    public int ThroughputSampleCount { get; set; }

    public int ThroughputAttemptCount { get; set; }

    public int ThroughputParallelDownloads { get; set; }

    public int ThroughputSampleBytes { get; set; }

    public double Score { get; set; }

    public bool WasSuccessful { get; set; }

    public string? FailureReason { get; set; }
}
