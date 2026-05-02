using XNetwork.Models;

namespace XNetwork.Services;

public class AutoServerSwitchService : BackgroundService
{
    private readonly ILogger<AutoServerSwitchService> _logger;
    private readonly SpeedifyService _speedifyService;
    private readonly IConnectionHealthService _connectionHealthService;
    private readonly ProbeScoreClient _probeScoreClient;
    private readonly ServerSwitchRecommendationSelector _recommendationSelector;
    private readonly LocalWanStabilityEvaluator _localWanStabilityEvaluator;
    private readonly RecommendationConfidenceTracker _recommendationConfidenceTracker;
    private readonly AutoServerSwitchStateStore _stateStore;
    private readonly RollingHealthWindow _healthWindow = new();
    private readonly AutoServerSwitchSettings _settings;
    private readonly AutoServerSwitchStatus _status = new();
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly SemaphoreSlim _probeCheckLock = new(1, 1);
    private readonly Dictionary<string, DateTime> _avoidServersUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _statusLock = new();
    private DateTime? _lastSwitchUtc;
    private DateTime? _degradedSinceUtc;
    private bool? _lastHealthWindowTriggered;

    public AutoServerSwitchService(
        ILogger<AutoServerSwitchService> logger,
        SpeedifyService speedifyService,
        IConnectionHealthService connectionHealthService,
        ProbeScoreClient probeScoreClient,
        ServerSwitchRecommendationSelector recommendationSelector,
        LocalWanStabilityEvaluator localWanStabilityEvaluator,
        RecommendationConfidenceTracker recommendationConfidenceTracker,
        AutoServerSwitchStateStore stateStore,
        AutoServerSwitchSettings settings)
    {
        _logger = logger;
        _speedifyService = speedifyService;
        _connectionHealthService = connectionHealthService;
        _probeScoreClient = probeScoreClient;
        _recommendationSelector = recommendationSelector;
        _localWanStabilityEvaluator = localWanStabilityEvaluator;
        _recommendationConfidenceTracker = recommendationConfidenceTracker;
        _stateStore = stateStore;
        _settings = settings;
        LoadPersistentState();
    }

    public AutoServerSwitchSettings Settings => _settings;

    public AutoServerSwitchStatus GetStatus()
    {
        lock (_statusLock)
        {
            NormalizeProbeCheckingStatusLocked();

            return new AutoServerSwitchStatus
            {
                IsEnabled = _status.IsEnabled,
                IsSwitching = _status.IsSwitching,
                IsCheckingProbe = _status.IsCheckingProbe,
                RecommendedServer = _status.RecommendedServer,
                Message = _status.Message,
                CurrentServer = _status.CurrentServer,
                LastSwitchReason = _status.LastSwitchReason,
                LastSwitchServer = _status.LastSwitchServer,
                LastSwitchUtc = _status.LastSwitchUtc,
                DegradedSinceUtc = _status.DegradedSinceUtc,
                HealthWindowSeconds = _status.HealthWindowSeconds,
                HealthWindowSampleCount = _status.HealthWindowSampleCount,
                HealthWindowBadSampleCount = _status.HealthWindowBadSampleCount,
                HealthWindowBadSampleRatio = _status.HealthWindowBadSampleRatio,
                IsHealthWindowTriggered = _status.IsHealthWindowTriggered,
                FirstBadSampleUtc = _status.FirstBadSampleUtc,
                HealthWindowMessage = _status.HealthWindowMessage,
                LastProbeCheckUtc = _status.LastProbeCheckUtc,
                LastProbeSuccessUtc = _status.LastProbeSuccessUtc,
                LastLatencyMs = _status.LastLatencyMs,
                LastJitterMs = _status.LastJitterMs,
                LastSuccessRate = _status.LastSuccessRate,
                LastProbeRunUtc = _status.LastProbeRunUtc,
                CurrentServerProbeScore = _status.CurrentServerProbeScore,
                CurrentServerProbeTestedUtc = _status.CurrentServerProbeTestedUtc,
                IsCurrentServerProbeConfirmedBad = _status.IsCurrentServerProbeConfirmedBad,
                CurrentServerProbeMessage = _status.CurrentServerProbeMessage,
                RecommendedServerProbeScore = _status.RecommendedServerProbeScore,
                ProbeApiError = _status.ProbeApiError,
                ProbeApiBaseUrl = _status.ProbeApiBaseUrl,
                ProbeCheckCount = _status.ProbeCheckCount,
                FailedProbeCheckCount = _status.FailedProbeCheckCount,
                LastProbeScoreCount = _status.LastProbeScoreCount,
                RecommendationCount = _status.RecommendationCount,
                LastRecommendationUtc = _status.LastRecommendationUtc,
                SwitchAttemptCount = _status.SwitchAttemptCount,
                SuccessfulSwitchCount = _status.SuccessfulSwitchCount,
                FailedSwitchCount = _status.FailedSwitchCount,
                ConnectedAdapterCount = _status.ConnectedAdapterCount,
                MinimumConnectedAdaptersBeforeSwitch = _status.MinimumConnectedAdaptersBeforeSwitch,
                IsLocalWanStableForSwitching = _status.IsLocalWanStableForSwitching,
                LocalWanStabilityMessage = _status.LocalWanStabilityMessage,
                PendingRecommendationServer = _status.PendingRecommendationServer,
                RecommendationConfirmationCount = _status.RecommendationConfirmationCount,
                RequiredRecommendationConfirmations = _status.RequiredRecommendationConfirmations,
                Adapters = _status.Adapters.ToList(),
                RecentScores = _status.RecentScores.Select(CopyScore).ToList(),
                RecentEvents = _status.RecentEvents.Select(CopyEvent).ToList()
            };
        }
    }

    public async Task<bool> SwitchNowAsync(string reason = "Manual external probe server switch", CancellationToken cancellationToken = default)
    {
        return await SwitchToBestServerAsync(reason, force: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProbeScoresResponse?> RefreshProbeScoresAsync(CancellationToken cancellationToken = default)
    {
        if (!await _probeCheckLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            UpdateStatus(status => status.Message = "External probe score refresh is already in progress");
            return null;
        }

        try
        {
            var currentServer = await _speedifyService.GetCurrentServerAsync(cancellationToken).ConfigureAwait(false);
            return await FetchProbeScoresAsync(currentServer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _probeCheckLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Auto server switch service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _settings.CheckIntervalSeconds)), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogWarning("Auto server switch loop operation timed out or was canceled unexpectedly");
                UpdateStatus(status =>
                {
                    status.IsCheckingProbe = false;
                    status.IsSwitching = false;
                    status.Message = "Auto switch operation timed out; will retry on the next check";
                });
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in auto server switch loop");
                UpdateStatus(status => status.Message = $"Auto switch error: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            _degradedSinceUtc = null;
            _healthWindow.Clear();
            UpdateStatus(status =>
            {
                status.IsEnabled = false;
                status.Message = "Auto server switching is disabled";
                status.DegradedSinceUtc = null;
                status.HealthWindowSampleCount = 0;
                status.HealthWindowBadSampleCount = 0;
                status.HealthWindowBadSampleRatio = 0;
                status.IsHealthWindowTriggered = false;
                status.FirstBadSampleUtc = null;
                status.HealthWindowMessage = null;
                status.ProbeApiBaseUrl = _settings.ProbeApiBaseUrl;
            });
            return;
        }

        var currentServer = await _speedifyService.GetCurrentServerAsync(cancellationToken).ConfigureAwait(false);
        var currentServerName = FormatServer(currentServer);
        var health = _connectionHealthService.GetOverallHealth().GetSnapshot();
        UpdateStatus(status =>
        {
            status.IsEnabled = true;
            status.CurrentServer = currentServerName;
            status.LastLatencyMs = health.latency;
            status.LastJitterMs = health.jitter;
            status.LastSuccessRate = health.successRate;
            status.ProbeApiBaseUrl = _settings.ProbeApiBaseUrl;
            status.MinimumConnectedAdaptersBeforeSwitch = Math.Max(0, _settings.MinimumConnectedAdaptersBeforeSwitch);
            status.RequiredRecommendationConfirmations = Math.Max(1, _settings.RequiredRecommendationConfirmations);
            status.HealthWindowSeconds = Math.Max(15, _settings.HealthWindowSeconds);
        });

        await UpdateLocalWanStatusAsync(blockOnUnstable: false, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(_settings.ProbeApiBaseUrl))
        {
            UpdateStatus(status => status.Message = "External probe API URL is not configured");
            return;
        }

        if (!_connectionHealthService.IsInitialized())
        {
            UpdateStatus(status => status.Message = "Waiting for health samples before auto switching");
            return;
        }

        var healthWindow = _healthWindow.Observe(
            health.status,
            health.latency,
            health.jitter,
            health.successRate,
            _settings,
            DateTime.UtcNow);
        UpdateHealthWindowStatus(healthWindow);
        LogHealthWindowTransition(healthWindow, currentServerName);

        if (IsInCooldown())
        {
            UpdateStatus(status => status.Message = $"Auto switching paused during {_settings.CooldownMinutes} minute cooldown after last switch");
            return;
        }

        if (!healthWindow.IsTriggered)
        {
            _degradedSinceUtc = null;
            UpdateStatus(status =>
            {
                status.Message = $"Health window acceptable: {healthWindow.Message}";
                status.DegradedSinceUtc = null;
            });
            return;
        }

        _degradedSinceUtc = healthWindow.FirstBadSampleUtc ?? DateTime.UtcNow;
        UpdateStatus(status =>
        {
            status.DegradedSinceUtc = _degradedSinceUtc;
            status.Message = $"Health window triggered: {healthWindow.Message}";
        });

        var reason = $"Rolling health window triggered with {healthWindow.BadSamples}/{healthWindow.TotalSamples} bad samples ({healthWindow.BadSampleRatio:P0}); latest health {health.latency:N0}ms latency, {health.jitter:N0}ms jitter, {health.successRate:N0}% success";
        await SwitchToBestServerAsync(reason, force: false, cancellationToken).ConfigureAwait(false);
    }

    private bool IsDegraded(ConnectionStatus status, double latency, double jitter, double successRate)
    {
        return status is ConnectionStatus.Poor or ConnectionStatus.Critical ||
               latency > _settings.MaxLatencyMs ||
               jitter > _settings.MaxJitterMs ||
               successRate < _settings.MinSuccessRate;
    }

    private bool IsInCooldown()
    {
        return _lastSwitchUtc.HasValue &&
               DateTime.UtcNow - _lastSwitchUtc.Value < TimeSpan.FromMinutes(Math.Max(1, _settings.CooldownMinutes));
    }

    private async Task<bool> SwitchToBestServerAsync(string reason, bool force, CancellationToken cancellationToken)
    {
        if (!force && IsInCooldown())
        {
            return false;
        }

        if (!await _switchLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            UpdateStatus(status => status.Message = "Server switch already in progress");
            return false;
        }

        try
        {
            UpdateStatus(status =>
            {
                status.IsSwitching = true;
                status.RecommendedServer = null;
                status.MinimumConnectedAdaptersBeforeSwitch = Math.Max(0, _settings.MinimumConnectedAdaptersBeforeSwitch);
                status.RequiredRecommendationConfirmations = Math.Max(1, _settings.RequiredRecommendationConfirmations);
            });

            var currentServer = await _speedifyService.GetCurrentServerAsync(cancellationToken).ConfigureAwait(false);

            if (!force && !await IsLocalWanStableForSwitchingAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var probeResponse = await FetchProbeScoresAsync(currentServer, cancellationToken).ConfigureAwait(false);
            if (probeResponse == null)
            {
                _recommendationConfidenceTracker.Reset();
                return false;
            }

            var recommendationResult = SelectRecommendation(probeResponse.Scores, currentServer);
            var recommendation = recommendationResult.Recommendation;
            if (recommendation == null)
            {
                _recommendationConfidenceTracker.Reset();
                var rejectionReason = recommendationResult.RejectionReason ?? "External probe has no better fresh server recommendation";
                UpdateStatus(status => status.Message = rejectionReason);
                AddEvent("Probe", rejectionReason, score: recommendationResult.CurrentScore?.Score);
                _logger.LogInformation(
                    "Auto server switch blocked: {Reason}. CurrentServer={CurrentServer}, CurrentProbeScore={CurrentProbeScore}, BadSamples={BadSamples}, TotalSamples={TotalSamples}, BadRatio={BadRatio:P0}",
                    rejectionReason,
                    FormatServer(currentServer),
                    recommendationResult.CurrentScore?.Score,
                    _status.HealthWindowBadSampleCount,
                    _status.HealthWindowSampleCount,
                    _status.HealthWindowBadSampleRatio);
                return false;
            }

            var confidence = _recommendationConfidenceTracker.Observe(recommendation, _settings.RequiredRecommendationConfirmations, force);
            UpdateStatus(status =>
            {
                status.PendingRecommendationServer = FormatScoreServer(recommendation);
                status.RecommendationConfirmationCount = confidence.ConfirmationCount;
                status.RequiredRecommendationConfirmations = confidence.RequiredConfirmations;
            });

            if (!confidence.HasConfidence)
            {
                UpdateStatus(status => status.Message = $"Waiting for recommendation confidence: {FormatScoreServer(recommendation)} confirmed {confidence.ConfirmationCount}/{confidence.RequiredConfirmations} times");
                AddEvent("Recommendation", $"Waiting for confidence on {FormatScoreServer(recommendation)} ({confidence.ConfirmationCount}/{confidence.RequiredConfirmations})", FormatScoreServer(recommendation), recommendation.Score);
                return false;
            }

            UpdateStatus(status =>
            {
                status.RecommendationCount++;
                status.LastRecommendationUtc = DateTime.UtcNow;
            });
            AddEvent("Recommendation", $"Recommended {FormatScoreServer(recommendation)} at {recommendation.Score:N1}/100", FormatScoreServer(recommendation), recommendation.Score);

            if (!string.IsNullOrWhiteSpace(currentServer?.Tag))
            {
                _avoidServersUntil[currentServer.Tag] = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.AvoidServerMinutes));
            }

            var targetServer = new ServerInfo
            {
                Tag = recommendation.Tag,
                FriendlyName = recommendation.FriendlyName,
                Country = recommendation.Country,
                City = recommendation.City,
                Num = recommendation.Num,
                IsPremium = recommendation.IsPremium,
                IsPrivate = recommendation.IsPrivate,
                DataCenter = recommendation.DataCenter
            };

            UpdateStatus(status =>
            {
                status.SwitchAttemptCount++;
                status.RecommendedServer = FormatScoreServer(recommendation);
                status.RecommendedServerProbeScore = recommendation.Score;
                status.Message = $"Switching to external probe recommendation {FormatScoreServer(recommendation)} ({recommendation.Score:N0}/100)";
            });
            AddEvent("Switch", $"Attempting switch to {FormatScoreServer(recommendation)}", FormatScoreServer(recommendation), recommendation.Score);
            _logger.LogWarning(
                "Attempting Speedify server switch from {CurrentServer} to {RecommendedServer}. CurrentProbeScore={CurrentProbeScore}, RecommendedProbeScore={RecommendedProbeScore}, BadSamples={BadSamples}, TotalSamples={TotalSamples}, BadRatio={BadRatio:P0}, Reason={Reason}",
                FormatServer(currentServer),
                FormatScoreServer(recommendation),
                recommendationResult.CurrentScore?.Score,
                recommendation.Score,
                _status.HealthWindowBadSampleCount,
                _status.HealthWindowSampleCount,
                _status.HealthWindowBadSampleRatio,
                reason);

            var connected = await _speedifyService.ConnectToServerAsync(targetServer, cancellationToken).ConfigureAwait(false);
            if (!connected)
            {
                UpdateStatus(status =>
                {
                    status.FailedSwitchCount++;
                    status.Message = $"Failed to connect to recommended server {FormatScoreServer(recommendation)}";
                });
                AddEvent("Error", $"Failed to connect to recommended server {FormatScoreServer(recommendation)}", FormatScoreServer(recommendation), recommendation.Score);
                return false;
            }

            _lastSwitchUtc = DateTime.UtcNow;
            _degradedSinceUtc = null;
            _recommendationConfidenceTracker.Reset();
            UpdateStatus(status =>
            {
                status.CurrentServer = FormatScoreServer(recommendation);
                status.LastSwitchReason = reason;
                status.LastSwitchServer = FormatScoreServer(recommendation);
                status.LastSwitchUtc = _lastSwitchUtc;
                status.DegradedSinceUtc = null;
                status.SuccessfulSwitchCount++;
                status.PendingRecommendationServer = null;
                status.RecommendationConfirmationCount = 0;
                status.Message = $"Switched to {FormatScoreServer(recommendation)} from external probe score {recommendation.Score:N0}/100";
            });
            AddEvent("Switch", $"Switched to {FormatScoreServer(recommendation)}", FormatScoreServer(recommendation), recommendation.Score);

            _logger.LogWarning("Switched Speedify server to {Server} from external probe score {Score}. Reason: {Reason}", FormatScoreServer(recommendation), recommendation.Score, reason);
            return true;
        }
        finally
        {
            UpdateStatus(status =>
            {
                status.IsSwitching = false;
                status.IsCheckingProbe = false;
            });
            _switchLock.Release();
        }
    }

    private async Task<ProbeScoresResponse?> FetchProbeScoresAsync(ServerInfo? currentServer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ProbeApiBaseUrl))
        {
            UpdateStatus(status =>
            {
                status.Message = "External probe API URL is not configured";
                status.ProbeApiError = status.Message;
            });
            AddEvent("Warning", "External probe API URL is not configured");
            return null;
        }

        UpdateStatus(status =>
        {
            status.IsCheckingProbe = true;
            status.Message = "Checking external Speedify probe scores";
            status.LastProbeCheckUtc = DateTime.UtcNow;
            status.ProbeApiBaseUrl = _settings.ProbeApiBaseUrl;
            status.ProbeApiError = null;
            status.ProbeCheckCount++;
        });

        try
        {
            var probeResponse = await _probeScoreClient.GetScoresAsync(_settings, currentServer, cancellationToken).ConfigureAwait(false);
            if (probeResponse == null)
            {
                UpdateStatus(status =>
                {
                    status.FailedProbeCheckCount++;
                    status.Message = "External probe API URL is not configured";
                    status.ProbeApiError = status.Message;
                });
                AddEvent("Warning", "External probe API URL is not configured");
                return null;
            }

            RecordProbeScores(probeResponse);
            if (!string.IsNullOrWhiteSpace(probeResponse.LastError))
            {
                UpdateStatus(status =>
                {
                    status.FailedProbeCheckCount++;
                    status.ProbeApiError = probeResponse.LastError;
                    status.Message = probeResponse.LastError;
                });
                AddEvent("Error", $"External probe API error: {probeResponse.LastError}");
            }
            else
            {
                UpdateStatus(status =>
                {
                    status.LastProbeSuccessUtc = DateTime.UtcNow;
                    status.LastProbeScoreCount = probeResponse.Scores.Count;
                    status.Message = $"Loaded {probeResponse.Scores.Count} external probe scores";
                });
                AddEvent("Probe", $"Loaded {probeResponse.Scores.Count} external probe scores");
            }

            return probeResponse;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateStatus(status =>
            {
                status.FailedProbeCheckCount++;
                status.ProbeApiError = ex.Message;
                status.Message = $"External probe check failed: {ex.Message}";
            });
            AddEvent("Error", $"External probe check failed: {ex.Message}");
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var timeoutSeconds = Math.Clamp(_settings.ProbeRequestTimeoutSeconds, 3, 60);
            var message = $"External probe check timed out after {timeoutSeconds}s";
            UpdateStatus(status =>
            {
                status.FailedProbeCheckCount++;
                status.ProbeApiError = message;
                status.Message = message;
            });
            AddEvent("Error", message);
            return null;
        }
        finally
        {
            UpdateStatus(status => status.IsCheckingProbe = false);
        }
    }

    private async Task<bool> IsLocalWanStableForSwitchingAsync(CancellationToken cancellationToken)
    {
        var result = await UpdateLocalWanStatusAsync(blockOnUnstable: true, cancellationToken).ConfigureAwait(false);
        return result?.IsStable ?? false;
    }

    private async Task<LocalWanStabilityResult?> UpdateLocalWanStatusAsync(bool blockOnUnstable, CancellationToken cancellationToken)
    {
        try
        {
            var adapters = await _speedifyService.GetAdaptersAsync(cancellationToken).ConfigureAwait(false);
            var result = _localWanStabilityEvaluator.Evaluate(adapters, _settings);
            UpdateStatus(status =>
            {
                status.Adapters = adapters.ToList();
                status.ConnectedAdapterCount = result.ConnectedAdapterCount;
                status.MinimumConnectedAdaptersBeforeSwitch = result.MinimumConnectedAdapters;
                status.IsLocalWanStableForSwitching = result.IsStable;
                status.LocalWanStabilityMessage = result.Message;
            });

            if (blockOnUnstable && !result.IsStable)
            {
                UpdateStatus(status => status.Message = $"Server switch blocked: {result.Message}");
                AddEvent("Warning", $"Server switch blocked: {result.Message}");
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateStatus(status =>
            {
                status.IsLocalWanStableForSwitching = false;
                status.LocalWanStabilityMessage = $"Could not read local WAN adapter state: {ex.Message}";
            });

            if (blockOnUnstable)
            {
                UpdateStatus(status => status.Message = $"Server switch blocked: {status.LocalWanStabilityMessage}");
                AddEvent("Error", $"Server switch blocked because local WAN state could not be read: {ex.Message}");
            }

            return null;
        }
    }

    private ServerSwitchRecommendationResult SelectRecommendation(IEnumerable<ServerHealthScore> scores, ServerInfo? currentServer)
    {
        RemoveExpiredAvoidedServers();
        var result = _recommendationSelector.Select(
            scores,
            currentServer,
            _settings,
            _avoidServersUntil.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            DateTime.UtcNow);

        var currentScore = result.CurrentScore;
        var best = result.Recommendation;
        UpdateStatus(status =>
        {
            status.CurrentServerProbeScore = currentScore?.Score;
            status.CurrentServerProbeTestedUtc = currentScore?.TestedUtc;
            status.IsCurrentServerProbeConfirmedBad = currentScore != null && (!currentScore.WasSuccessful || currentScore.Score < _settings.MinimumProbeScore);
            status.CurrentServerProbeMessage = BuildCurrentProbeMessage(currentScore, result.RejectionReason);
            status.RecommendedServer = best == null ? null : FormatScoreServer(best);
            status.RecommendedServerProbeScore = best?.Score;
        });

        if (best == null)
        {
            if (!string.IsNullOrWhiteSpace(result.RejectionReason))
            {
                UpdateStatus(status => status.Message = result.RejectionReason);
            }
            return result;
        }

        return result;
    }

    private void RemoveExpiredAvoidedServers()
    {
        var now = DateTime.UtcNow;
        foreach (var tag in _avoidServersUntil.Where(kvp => kvp.Value <= now).Select(kvp => kvp.Key).ToList())
        {
            _avoidServersUntil.Remove(tag);
        }
    }

    private void RecordProbeScores(ProbeScoresResponse response)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, _settings.ProbeMaxScoreAgeMinutes));
        var recentScores = response.Scores
            .Where(score => score.TestedUtc >= cutoff)
            .OrderByDescending(score => score.WasSuccessful)
            .ThenByDescending(score => score.Score)
            .Take(10)
            .Select(CopyScore)
            .ToList();

        UpdateStatus(status =>
        {
            status.LastProbeRunUtc = response.LastCompletedUtc ?? response.LastRunUtc ?? response.GeneratedUtc;
            status.RecentScores = recentScores;
        });
    }

    private static string FormatServer(ServerInfo? server)
    {
        if (server == null)
        {
            return "Unknown server";
        }

        return !string.IsNullOrWhiteSpace(server.FriendlyName)
            ? server.FriendlyName
            : $"{server.Country} {server.City} #{server.Num}".Trim();
    }

    private static string FormatScoreServer(ServerHealthScore score)
    {
        return !string.IsNullOrWhiteSpace(score.FriendlyName)
            ? score.FriendlyName
            : $"{score.Country} {score.City} #{score.Num}".Trim();
    }

    private static string FormatAge(DateTime timestampUtc)
    {
        var age = DateTime.UtcNow - timestampUtc;
        if (age.TotalSeconds < 60)
        {
            return $"{Math.Max(0, age.TotalSeconds):N0}s ago";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{age.TotalMinutes:N0}m ago";
        }

        return timestampUtc.ToLocalTime().ToString("g");
    }

    private void UpdateStatus(Action<AutoServerSwitchStatus> update)
    {
        lock (_statusLock)
        {
            update(_status);
        }
    }

    private void NormalizeProbeCheckingStatusLocked()
    {
        var timeoutSeconds = Math.Clamp(_settings.ProbeRequestTimeoutSeconds, 3, 60);
        if (_status.IsCheckingProbe && _status.LastProbeCheckUtc.HasValue &&
            DateTime.UtcNow - _status.LastProbeCheckUtc.Value > TimeSpan.FromSeconds(timeoutSeconds + 10))
        {
            _status.IsCheckingProbe = false;
            _status.ProbeApiError = $"External probe check timed out after {timeoutSeconds}s";
            _status.Message = _status.ProbeApiError;
        }

        if (!_status.IsCheckingProbe &&
            string.Equals(_status.Message, "Checking external Speedify probe scores", StringComparison.OrdinalIgnoreCase))
        {
            _status.Message = _status.LastProbeSuccessUtc.HasValue
                ? $"Last probe check succeeded {FormatAge(_status.LastProbeSuccessUtc.Value)}"
                : "Waiting for the next external probe check";
        }
    }

    private void UpdateHealthWindowStatus(HealthWindowSnapshot snapshot)
    {
        UpdateStatus(status =>
        {
            status.HealthWindowSeconds = (int)Math.Round(snapshot.Window.TotalSeconds);
            status.HealthWindowSampleCount = snapshot.TotalSamples;
            status.HealthWindowBadSampleCount = snapshot.BadSamples;
            status.HealthWindowBadSampleRatio = snapshot.BadSampleRatio;
            status.IsHealthWindowTriggered = snapshot.IsTriggered;
            status.FirstBadSampleUtc = snapshot.FirstBadSampleUtc;
            status.HealthWindowMessage = snapshot.Message;
        });
    }

    private void LogHealthWindowTransition(HealthWindowSnapshot snapshot, string currentServer)
    {
        if (_lastHealthWindowTriggered == snapshot.IsTriggered)
        {
            return;
        }

        _lastHealthWindowTriggered = snapshot.IsTriggered;
        if (snapshot.IsTriggered)
        {
            _logger.LogWarning(
                "Auto switch health window triggered for {CurrentServer}: {BadSamples}/{TotalSamples} bad samples ({BadRatio:P0}) in {WindowSeconds:N0}s",
                currentServer,
                snapshot.BadSamples,
                snapshot.TotalSamples,
                snapshot.BadSampleRatio,
                snapshot.Window.TotalSeconds);
        }
        else
        {
            _logger.LogInformation(
                "Auto switch health window recovered for {CurrentServer}: {BadSamples}/{TotalSamples} bad samples ({BadRatio:P0}) in {WindowSeconds:N0}s",
                currentServer,
                snapshot.BadSamples,
                snapshot.TotalSamples,
                snapshot.BadSampleRatio,
                snapshot.Window.TotalSeconds);
        }
    }

    private string BuildCurrentProbeMessage(ServerHealthScore? currentScore, string? rejectionReason)
    {
        if (currentScore == null)
        {
            return string.IsNullOrWhiteSpace(rejectionReason)
                ? "Probe has no fresh score for the current server"
                : rejectionReason;
        }

        var age = DateTime.UtcNow - currentScore.TestedUtc;
        var quality = currentScore.WasSuccessful && currentScore.Score >= _settings.MinimumProbeScore
            ? "acceptable"
            : "bad";
        return $"Current server probe score is {quality}: {currentScore.Score:N1}/100, tested {Math.Max(0, age.TotalMinutes):N0}m ago";
    }

    private void LoadPersistentState()
    {
        var persisted = _stateStore.Load();
        lock (_statusLock)
        {
            _status.LastSwitchUtc = persisted.LastSwitchUtc;
            _status.LastSwitchServer = persisted.LastSwitchServer;
            _status.LastSwitchReason = persisted.LastSwitchReason;
            _status.ProbeCheckCount = persisted.ProbeCheckCount;
            _status.FailedProbeCheckCount = persisted.FailedProbeCheckCount;
            _status.RecommendationCount = persisted.RecommendationCount;
            _status.LastRecommendationUtc = persisted.LastRecommendationUtc;
            _status.SwitchAttemptCount = persisted.SwitchAttemptCount;
            _status.SuccessfulSwitchCount = persisted.SuccessfulSwitchCount;
            _status.FailedSwitchCount = persisted.FailedSwitchCount;
            _status.RecentEvents = persisted.RecentEvents
                .OrderByDescending(item => item.TimestampUtc)
                .Take(50)
                .Select(CopyEvent)
                .ToList();
            _lastSwitchUtc = persisted.LastSwitchUtc;
        }

        foreach (var item in persisted.AvoidServersUntil.Where(item => item.Value > DateTime.UtcNow))
        {
            _avoidServersUntil[item.Key] = item.Value;
        }
    }

    private void PersistState()
    {
        AutoServerSwitchPersistentState snapshot;
        lock (_statusLock)
        {
            snapshot = new AutoServerSwitchPersistentState
            {
                LastSwitchUtc = _status.LastSwitchUtc,
                LastSwitchServer = _status.LastSwitchServer,
                LastSwitchReason = _status.LastSwitchReason,
                ProbeCheckCount = _status.ProbeCheckCount,
                FailedProbeCheckCount = _status.FailedProbeCheckCount,
                RecommendationCount = _status.RecommendationCount,
                LastRecommendationUtc = _status.LastRecommendationUtc,
                SwitchAttemptCount = _status.SwitchAttemptCount,
                SuccessfulSwitchCount = _status.SuccessfulSwitchCount,
                FailedSwitchCount = _status.FailedSwitchCount,
                AvoidServersUntil = _avoidServersUntil
                    .Where(item => item.Value > DateTime.UtcNow)
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                RecentEvents = _status.RecentEvents.Select(CopyEvent).ToList()
            };
        }

        _stateStore.Save(snapshot);
    }

    private void AddEvent(string type, string message, string? server = null, double? score = null)
    {
        var shouldPersist = false;
        lock (_statusLock)
        {
            var recentDuplicate = _status.RecentEvents.FirstOrDefault(item =>
                string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Message, message, StringComparison.Ordinal) &&
                DateTime.UtcNow - item.TimestampUtc < TimeSpan.FromMinutes(1));
            if (recentDuplicate != null)
            {
                return;
            }

            _status.RecentEvents.Insert(0, new AutoServerSwitchEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Type = type,
                Message = message,
                Server = server,
                Score = score
            });

            if (_status.RecentEvents.Count > 50)
            {
                _status.RecentEvents.RemoveRange(50, _status.RecentEvents.Count - 50);
            }

            shouldPersist = true;
        }

        if (shouldPersist)
        {
            PersistState();
        }
    }

    private static ServerHealthScore CopyScore(ServerHealthScore score)
    {
        return new ServerHealthScore
        {
            Tag = score.Tag,
            FriendlyName = score.FriendlyName,
            Country = score.Country,
            City = score.City,
            Num = score.Num,
            IsPremium = score.IsPremium,
            IsPrivate = score.IsPrivate,
            DataCenter = score.DataCenter,
            TestedUtc = score.TestedUtc,
            LatencyMs = score.LatencyMs,
            JitterMs = score.JitterMs,
            SuccessRate = score.SuccessRate,
            ThroughputMbps = score.ThroughputMbps,
            ThroughputConfidence = score.ThroughputConfidence,
            ThroughputSampleCount = score.ThroughputSampleCount,
            ThroughputAttemptCount = score.ThroughputAttemptCount,
            ThroughputParallelDownloads = score.ThroughputParallelDownloads,
            ThroughputSampleBytes = score.ThroughputSampleBytes,
            Score = score.Score,
            WasSuccessful = score.WasSuccessful,
            FailureReason = score.FailureReason
        };
    }

    private static AutoServerSwitchEvent CopyEvent(AutoServerSwitchEvent switchEvent)
    {
        return new AutoServerSwitchEvent
        {
            TimestampUtc = switchEvent.TimestampUtc,
            Type = switchEvent.Type,
            Message = switchEvent.Message,
            Server = switchEvent.Server,
            Score = switchEvent.Score
        };
    }

    public override void Dispose()
    {
        _switchLock.Dispose();
        _probeCheckLock.Dispose();
        base.Dispose();
    }
}
