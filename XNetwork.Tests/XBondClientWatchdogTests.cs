using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class XBondClientWatchdogTests
{
    [Fact]
    public void Normalize_ClampsUnsafeRestartAndProbeValues()
    {
        var settings = new XBondClientWatchdogSettings
        {
            CheckIntervalSeconds = 1,
            ConsecutiveUnhealthyChecks = 99,
            TunnelRttThresholdMs = 10,
            TunnelLossThresholdPercent = 0,
            TunnelStaleAfterSeconds = 1,
            PhysicalProbeTarget = "http://45.77.241.247:8444/path",
            PhysicalMaxRttMs = 1,
            PhysicalMaxLossPercent = 100,
            MinimumHealthyPhysicalPaths = 0,
            ProbeCount = 99,
            ProbeTimeoutSeconds = 0,
            RestartCooldownMinutes = 0,
            PostRestartGraceSeconds = 1,
            MaxRestartsPerHour = 99
        };

        XBondClientWatchdogSettingsStore.Normalize(settings);

        Assert.Equal(5, settings.CheckIntervalSeconds);
        Assert.Equal(12, settings.ConsecutiveUnhealthyChecks);
        Assert.Equal(100, settings.TunnelRttThresholdMs);
        Assert.Equal(1, settings.TunnelLossThresholdPercent);
        Assert.Equal(3, settings.TunnelStaleAfterSeconds);
        Assert.Equal("45.77.241.247", settings.PhysicalProbeTarget);
        Assert.Equal(20, settings.PhysicalMaxRttMs);
        Assert.Equal(99, settings.PhysicalMaxLossPercent);
        Assert.Equal(1, settings.MinimumHealthyPhysicalPaths);
        Assert.Equal(5, settings.ProbeCount);
        Assert.Equal(1, settings.ProbeTimeoutSeconds);
        Assert.Equal(1, settings.RestartCooldownMinutes);
        Assert.Equal(10, settings.PostRestartGraceSeconds);
        Assert.Equal(12, settings.MaxRestartsPerHour);
    }

    [Fact]
    public void ParsePingOutput_ReturnsAverageAndLoss()
    {
        const string output = """
            3 packets transmitted, 3 received, 0% packet loss, time 2003ms
            rtt min/avg/max/mdev = 45.100/51.250/60.000/6.120 ms
            """;

        var result = XBondPhysicalPathProbeService.ParsePingOutput("wlan0", "45.77.241.247", output, 0);

        Assert.True(result.Responded);
        Assert.Equal(0, result.LossPercent);
        Assert.Equal(51.25, result.AverageRttMs);
    }

    [Theory]
    [InlineData("wlan0", true)]
    [InlineData("enx103c59f1039c", true)]
    [InlineData("xbond0", false)]
    [InlineData("tailscale0", false)]
    [InlineData("bad name", false)]
    public void IsSafePhysicalInterface_FiltersTunnelAndUnsafeNames(string name, bool expected)
    {
        Assert.Equal(expected, XBondPhysicalPathProbeService.IsSafePhysicalInterface(name));
    }

    [Fact]
    public void Evaluate_HealthyTunnelNeverRestarts()
    {
        var settings = DefaultSettings();
        var snapshot = Snapshot(rttMs: 55, lossRate: 0, lastSuccessAgeMs: 500);
        var probes = new[] { HealthyProbe("wlan0") };

        var decision = XBondClientWatchdogService.Evaluate(snapshot, probes, settings, 2);

        Assert.False(decision.TunnelUnhealthy);
        Assert.False(decision.ShouldRestart);
        Assert.Equal(0, decision.ConsecutiveMismatchChecks);
    }

    [Fact]
    public void Evaluate_SustainedTunnelMismatchRestartsAfterConfiguredRounds()
    {
        var settings = DefaultSettings();
        var snapshot = Snapshot(rttMs: 450, lossRate: 0, lastSuccessAgeMs: 500);
        var probes = new[] { HealthyProbe("wlan0") };

        var first = XBondClientWatchdogService.Evaluate(snapshot, probes, settings, 0);
        var second = XBondClientWatchdogService.Evaluate(snapshot, probes, settings, first.ConsecutiveMismatchChecks);
        var third = XBondClientWatchdogService.Evaluate(snapshot, probes, settings, second.ConsecutiveMismatchChecks);

        Assert.False(first.ShouldRestart);
        Assert.False(second.ShouldRestart);
        Assert.True(third.ShouldRestart);
        Assert.Equal(3, third.ConsecutiveMismatchChecks);
    }

    [Fact]
    public void Evaluate_BadTunnelWithoutHealthyPhysicalPathDoesNotRestart()
    {
        var settings = DefaultSettings();
        var snapshot = Snapshot(rttMs: 450, lossRate: 0.6, lastSuccessAgeMs: 12_000);
        var probes = new[]
        {
            new XBondPhysicalPathProbeResult
            {
                InterfaceName = "wlan0",
                Responded = false,
                Healthy = false,
                LossPercent = 100
            }
        };

        var decision = XBondClientWatchdogService.Evaluate(snapshot, probes, settings, 2);

        Assert.True(decision.TunnelUnhealthy);
        Assert.False(decision.ShouldRestart);
        Assert.Equal(0, decision.ConsecutiveMismatchChecks);
        Assert.Contains("restart skipped", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_StaleRuntimeStatusCountsAsTunnelFailure()
    {
        var settings = DefaultSettings();
        var snapshot = Snapshot(rttMs: 55, lossRate: 0, lastSuccessAgeMs: 500);
        snapshot.RawStatus.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(-30);

        var decision = XBondClientWatchdogService.Evaluate(
            snapshot,
            [HealthyProbe("wlan0")],
            settings,
            previousConsecutiveMismatchChecks: 2);

        Assert.True(decision.TunnelUnhealthy);
        Assert.True(decision.ShouldRestart);
        Assert.Contains("runtime status file", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsWithinStartupGrace_BlocksAutomaticEvaluationUntilGraceExpires()
    {
        var startedAt = DateTimeOffset.Parse("2026-07-20T00:00:00Z");

        Assert.True(XBondClientWatchdogService.IsWithinStartupGrace(
            startedAt,
            startedAt.AddSeconds(44),
            graceSeconds: 45));
        Assert.False(XBondClientWatchdogService.IsWithinStartupGrace(
            startedAt,
            startedAt.AddSeconds(45),
            graceSeconds: 45));
    }

    [Fact]
    public void ResolveProbeTarget_PrefersOverrideThenRuntimeServer()
    {
        Assert.Equal(
            "8.8.8.8",
            XBondClientWatchdogService.ResolveProbeTarget("8.8.8.8:53", "45.77.241.247:8444"));
        Assert.Equal(
            "45.77.241.247",
            XBondClientWatchdogService.ResolveProbeTarget("", "45.77.241.247:8444"));
    }

    [Fact]
    public async Task FailedRestart_PreservesMismatchAndDoesNotConsumeRestartGuards()
    {
        var harness = CreateRestartHarness(restartSucceeds: false);
        await harness.Cache.GetSnapshotAsync();
        SetPrivateField(harness.Service, "_consecutiveMismatchChecks", 3);
        var attemptedAt = DateTimeOffset.Parse("2026-07-20T02:00:00Z");

        var firstMessage = await InvokeRestartAttemptAsync(harness.Service, attemptedAt);
        var secondMessage = await InvokeRestartAttemptAsync(harness.Service, attemptedAt.AddSeconds(10));
        var cachedSnapshot = await harness.Cache.GetSnapshotAsync();
        var status = harness.Service.GetStatus();

        Assert.Contains("restart failed", firstMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart failed", secondMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, harness.RestartCalls());
        Assert.Equal(3, GetPrivateField<int>(harness.Service, "_consecutiveMismatchChecks"));
        Assert.Empty(GetPrivateField<Queue<DateTimeOffset>>(harness.Service, "_restartHistory"));
        Assert.Null(GetPrivateField<DateTimeOffset?>(harness.Service, "_suppressedUntilUtc"));
        Assert.Null(status.LastRestartAtUtc);
        Assert.Null(status.LastRestartReason);
        Assert.Equal(1, harness.Provider.Calls);
        Assert.Same(harness.Provider.LastSnapshot, cachedSnapshot);
    }

    [Fact]
    public async Task SuccessfulRestart_CommitsRestartGuardsAndInvalidatesSnapshot()
    {
        var harness = CreateRestartHarness(restartSucceeds: true);
        await harness.Cache.GetSnapshotAsync();
        SetPrivateField(harness.Service, "_consecutiveMismatchChecks", 3);
        var attemptedAt = DateTimeOffset.Parse("2026-07-20T02:00:00Z");

        var message = await InvokeRestartAttemptAsync(harness.Service, attemptedAt);
        await harness.Cache.GetSnapshotAsync();
        var status = harness.Service.GetStatus();

        Assert.Contains("Restarted XBond client", message, StringComparison.Ordinal);
        Assert.Equal(1, harness.RestartCalls());
        Assert.Equal(0, GetPrivateField<int>(harness.Service, "_consecutiveMismatchChecks"));
        Assert.Single(GetPrivateField<Queue<DateTimeOffset>>(harness.Service, "_restartHistory"));
        Assert.Equal(attemptedAt.AddMinutes(10), GetPrivateField<DateTimeOffset?>(harness.Service, "_suppressedUntilUtc"));
        Assert.Equal(attemptedAt, status.LastRestartAtUtc);
        Assert.Equal("test mismatch", status.LastRestartReason);
        Assert.Equal(2, harness.Provider.Calls);
    }

    private static XBondClientWatchdogSettings DefaultSettings() => new()
    {
        ConsecutiveUnhealthyChecks = 3,
        TunnelRttThresholdMs = 300,
        TunnelLossThresholdPercent = 25,
        TunnelStaleAfterSeconds = 10,
        MinimumHealthyPhysicalPaths = 1
    };

    private static XBondStatsSnapshot Snapshot(double rttMs, double lossRate, ulong lastSuccessAgeMs) => new()
    {
        RawStatus = new XBondStatus
        {
            Running = true,
            Tunnel = new XBondTunnelStatus
            {
                State = "running",
                RttMs = rttMs,
                LossRate = lossRate,
                LastSuccessAgeMs = lastSuccessAgeMs,
                Status = "good"
            }
        }
    };

    private static XBondPhysicalPathProbeResult HealthyProbe(string interfaceName) => new()
    {
        InterfaceName = interfaceName,
        Responded = true,
        Healthy = true,
        AverageRttMs = 60,
        LossPercent = 0
    };

    private static RestartHarness CreateRestartHarness(bool restartSucceeds)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"xbond-watchdog-{Guid.NewGuid():N}");
        var watchdogSettings = DefaultSettings();
        watchdogSettings.Enabled = true;
        watchdogSettings.RestartCooldownMinutes = 10;
        watchdogSettings.PostRestartGraceSeconds = 45;
        watchdogSettings.MaxRestartsPerHour = 1;
        var xbondSettings = new XBondSettings
        {
            AllowServiceControl = true,
            ClientServiceName = "xbond-client.service"
        };
        var restartCalls = 0;
        var provider = new CountingStatsProvider();
        var cache = new XBondSnapshotCache(provider, cacheDuration: TimeSpan.FromHours(1));
        var xbondStore = new XBondSettingsStore(
            NullLogger<XBondSettingsStore>.Instance,
            Path.Combine(tempDirectory, "xbond.json"));
        var trafficEngine = new XBondTrafficEngineService(
            NullLogger<XBondTrafficEngineService>.Instance,
            xbondSettings,
            xbondStore,
            TimeProvider.System,
            (arguments, _) =>
            {
                if (arguments[0] == "restart")
                {
                    restartCalls++;
                    return Task.FromResult(new XBondTrafficEngineService.ServiceCommandResult(
                        restartSucceeds ? 0 : 1,
                        restartSucceeds ? "" : "simulated restart failure"));
                }

                return Task.FromResult(new XBondTrafficEngineService.ServiceCommandResult(
                    0,
                    arguments[0] == "is-active" ? "active" : "enabled"));
            },
            () => true,
            TimeSpan.Zero);
        var service = new XBondClientWatchdogService(
            watchdogSettings,
            new XBondClientWatchdogSettingsStore(
                NullLogger<XBondClientWatchdogSettingsStore>.Instance,
                Path.Combine(tempDirectory, "watchdog.json")),
            cache,
            new XBondPhysicalPathProbeService(
                xbondSettings,
                NullLogger<XBondPhysicalPathProbeService>.Instance),
            trafficEngine,
            xbondSettings,
            NullLogger<XBondClientWatchdogService>.Instance);

        return new RestartHarness(service, cache, provider, () => restartCalls);
    }

    private static async Task<string> InvokeRestartAttemptAsync(
        XBondClientWatchdogService service,
        DateTimeOffset attemptedAt)
    {
        var method = typeof(XBondClientWatchdogService).GetMethod(
            "AttemptRestartAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(XBondClientWatchdogService), "AttemptRestartAsync");
        var task = (Task<string>?)method.Invoke(
            service,
            [
                new XBondClientWatchdogDecision(true, 1, 3, true, "test mismatch"),
                attemptedAt,
                CancellationToken.None
            ]);

        return await (task ?? throw new InvalidOperationException("Restart attempt did not return a task."));
    }

    private static T GetPrivateField<T>(XBondClientWatchdogService service, string fieldName)
    {
        var field = typeof(XBondClientWatchdogService).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(XBondClientWatchdogService), fieldName);
        return (T)field.GetValue(service)!;
    }

    private static void SetPrivateField<T>(XBondClientWatchdogService service, string fieldName, T value)
    {
        var field = typeof(XBondClientWatchdogService).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(XBondClientWatchdogService), fieldName);
        field.SetValue(service, value);
    }

    private sealed record RestartHarness(
        XBondClientWatchdogService Service,
        XBondSnapshotCache Cache,
        CountingStatsProvider Provider,
        Func<int> RestartCalls);

    private sealed class CountingStatsProvider : IXBondStatsProvider
    {
        public int Calls { get; private set; }

        public XBondStatsSnapshot? LastSnapshot { get; private set; }

        public Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            LastSnapshot = Snapshot(rttMs: 450, lossRate: 0, lastSuccessAgeMs: 500);
            return Task.FromResult(LastSnapshot);
        }
    }
}
