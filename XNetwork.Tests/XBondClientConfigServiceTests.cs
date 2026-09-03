using XNetwork.Services;

namespace XNetwork.Tests;

public class XBondClientConfigServiceTests
{
    [Fact]
    public void ParseConfig_ReadsTopLevelSettingsAndPaths()
    {
        var config = XBondClientConfigService.ParseConfig(
            """
            enabled = true
            session_id = 42
            server_addr = "45.77.241.247:8444"
            traffic_mode = "adaptive"
            mode = "anchor-duplicate-1"
            redundancy_policy = "balanced"
            max_active_backups = 1
            realtime_deadline_ms = 500
            interactive_packet_threshold_bytes = 768
            duplicate_loss_threshold = 0.02
            backup_loss_disable_threshold = 0.35
            reorder_hold_ms = 25
            heartbeat_interval_ms = 200
            heartbeat_health_window_samples = 100
            heartbeat_min_quality_samples = 20
            heartbeat_failure_consecutive = 4
            heartbeat_recovery_consecutive = 15
            recovery_enabled = true
            recovery_enter_degraded_ticks = 4
            recovery_exit_clean_ticks = 30
            recovery_degraded_loss_threshold = 0.12
            recovery_degraded_late_threshold = 0.04
            recovery_degraded_jitter_ms = 90
            recovery_degraded_stale_ack_ms = 1700
            recovery_degraded_queue_pressure = 0.8
            recovery_clean_loss_threshold = 0.015
            recovery_clean_late_threshold = 0.005
            recovery_clean_jitter_ms = 35
            recovery_clean_stale_ack_ms = 900
            recovery_clean_queue_pressure = 0.4
            recovery_path_loss_exclude_threshold = 0.9
            runtime_status_path = "/run/xbond/client-status.json"

            [[paths]]
            id = 2
            name = "Smart Communications"
            interface_name = "enx103c59f1039c"
            enabled = true

            [[paths]]
            id = 5
            name = "Wi-Fi \"WAN\""
            interface_name = "wlan0"
            enabled = false
            """);

        Assert.True(config.Enabled);
        Assert.Equal((ulong)42, config.SessionId);
        Assert.Equal("45.77.241.247:8444", config.ServerAddress);
        Assert.Equal("adaptive", config.TrafficMode);
        Assert.Equal("anchor-duplicate-1", config.Mode);
        Assert.Equal("balanced", config.RedundancyPolicy);
        Assert.Equal(1, config.MaxActiveBackups);
        Assert.Equal(500, config.RealtimeDeadlineMs);
        Assert.Equal(768, config.InteractivePacketThresholdBytes);
        Assert.Equal(0.02, config.DuplicateLossThreshold);
        Assert.Equal(0.35, config.BackupLossDisableThreshold);
        Assert.Equal(25, config.ReorderHoldMs);
        Assert.Equal(200, config.HeartbeatIntervalMs);
        Assert.Equal(100, config.HeartbeatHealthWindowSamples);
        Assert.Equal(20, config.HeartbeatMinQualitySamples);
        Assert.Equal(4, config.HeartbeatFailureConsecutive);
        Assert.Equal(15, config.HeartbeatRecoveryConsecutive);
        Assert.True(config.RecoveryEnabled);
        Assert.Equal(4, config.RecoveryEnterDegradedTicks);
        Assert.Equal(30, config.RecoveryExitCleanTicks);
        Assert.Equal(0.12, config.RecoveryDegradedLossThreshold);
        Assert.Equal(0.04, config.RecoveryDegradedLateThreshold);
        Assert.Equal(90, config.RecoveryDegradedJitterMs);
        Assert.Equal((ulong)1700, config.RecoveryDegradedStaleAckMs);
        Assert.Equal(0.8, config.RecoveryDegradedQueuePressure);
        Assert.Equal(0.015, config.RecoveryCleanLossThreshold);
        Assert.Equal(0.005, config.RecoveryCleanLateThreshold);
        Assert.Equal(35, config.RecoveryCleanJitterMs);
        Assert.Equal((ulong)900, config.RecoveryCleanStaleAckMs);
        Assert.Equal(0.4, config.RecoveryCleanQueuePressure);
        Assert.Equal(0.9, config.RecoveryPathLossExcludeThreshold);
        Assert.Equal("/run/xbond/client-status.json", config.RuntimeStatusPath);

        Assert.Collection(
            config.Paths,
            path =>
            {
                Assert.Equal(2, path.Id);
                Assert.Equal("Smart Communications", path.Name);
                Assert.Equal("enx103c59f1039c", path.InterfaceName);
                Assert.True(path.Enabled);
            },
            path =>
            {
                Assert.Equal(5, path.Id);
                Assert.Equal("Wi-Fi \"WAN\"", path.Name);
                Assert.Equal("wlan0", path.InterfaceName);
                Assert.False(path.Enabled);
            });
    }

    [Fact]
    public void RenderConfig_WritesStableClientToml()
    {
        var config = XBondClientConfigService.ParseConfig(
            """
            enabled = true
            session_id = 1
            server_addr = "45.77.241.247:8444"
            mode = "anchor-duplicate-1"
            redundancy_policy = "fast"
            max_active_backups = 1
            realtime_deadline_ms = 500
            interactive_packet_threshold_bytes = 512
            duplicate_loss_threshold = 0.05
            backup_loss_disable_threshold = 0.25
            reorder_hold_ms = 15
            heartbeat_interval_ms = 250
            heartbeat_health_window_samples = 80
            heartbeat_min_quality_samples = 16
            heartbeat_failure_consecutive = 5
            heartbeat_recovery_consecutive = 12
            recovery_enabled = true
            recovery_enter_degraded_ticks = 5
            recovery_exit_clean_ticks = 25
            recovery_degraded_loss_threshold = 0.1
            recovery_degraded_late_threshold = 0.05
            recovery_degraded_jitter_ms = 100
            recovery_degraded_stale_ack_ms = 2000
            recovery_degraded_queue_pressure = 0.75
            recovery_clean_loss_threshold = 0.01
            recovery_clean_late_threshold = 0.004
            recovery_clean_jitter_ms = 30
            recovery_clean_stale_ack_ms = 800
            recovery_clean_queue_pressure = 0.35
            recovery_path_loss_exclude_threshold = 0.85
            runtime_status_path = "/run/xbond/client-status.json"

            [[paths]]
            id = 2
            name = "Smart Communications"
            interface_name = "enx103c59f1039c"
            enabled = true
            """);
        config.Paths.Add(new()
        {
            Id = 9,
            Name = "XNetwork Wi-Fi Asia",
            InterfaceName = "wlan0",
            Enabled = true
        });

        var rendered = XBondClientConfigService.RenderConfig(config);

        Assert.Contains("server_addr = \"45.77.241.247:8444\"", rendered);
        Assert.Contains("traffic_mode = \"tunnel\"", rendered);
        Assert.Contains("redundancy_policy = \"fast\"", rendered);
        Assert.Contains("interactive_packet_threshold_bytes = 512", rendered);
        Assert.Contains("duplicate_loss_threshold = 0.05", rendered);
        Assert.Contains("backup_loss_disable_threshold = 0.25", rendered);
        Assert.Contains("reorder_hold_ms = 15", rendered);
        Assert.Contains("heartbeat_interval_ms = 250", rendered);
        Assert.Contains("heartbeat_health_window_samples = 80", rendered);
        Assert.Contains("heartbeat_min_quality_samples = 16", rendered);
        Assert.Contains("heartbeat_failure_consecutive = 5", rendered);
        Assert.Contains("heartbeat_recovery_consecutive = 12", rendered);
        Assert.Contains("recovery_enabled = true", rendered);
        Assert.Contains("recovery_enter_degraded_ticks = 5", rendered);
        Assert.Contains("recovery_exit_clean_ticks = 25", rendered);
        Assert.Contains("recovery_degraded_loss_threshold = 0.1", rendered);
        Assert.Contains("recovery_degraded_late_threshold = 0.05", rendered);
        Assert.Contains("recovery_degraded_jitter_ms = 100", rendered);
        Assert.Contains("recovery_degraded_stale_ack_ms = 2000", rendered);
        Assert.Contains("recovery_degraded_queue_pressure = 0.75", rendered);
        Assert.Contains("recovery_clean_loss_threshold = 0.01", rendered);
        Assert.Contains("recovery_clean_late_threshold = 0.004", rendered);
        Assert.Contains("recovery_clean_jitter_ms = 30", rendered);
        Assert.Contains("recovery_clean_stale_ack_ms = 800", rendered);
        Assert.Contains("recovery_clean_queue_pressure = 0.35", rendered);
        Assert.Contains("recovery_path_loss_exclude_threshold = 0.85", rendered);
        Assert.Contains("runtime_status_path = \"/run/xbond/client-status.json\"", rendered);
        Assert.Contains("name = \"XNetwork Wi-Fi Asia\"", rendered);
        Assert.Contains("interface_name = \"wlan0\"", rendered);
        Assert.Equal(2, rendered.Split("[[paths]]").Length - 1);

        var reparsed = XBondClientConfigService.ParseConfig(rendered);
        Assert.Equal(["enx103c59f1039c", "wlan0"], reparsed.Paths.Select(path => path.InterfaceName).ToArray());
    }

    [Fact]
    public void ParseConfig_LegacyConfigDefaultsToTunnelTrafficMode()
    {
        var config = XBondClientConfigService.ParseConfig(
            """
            enabled = true
            server_addr = "45.77.241.247:8444"
            mode = "anchor-duplicate-1"
            redundancy_policy = "balanced"
            """);

        Assert.Equal("tunnel", config.TrafficMode);
        Assert.Equal("balanced", config.RedundancyPolicy);
    }

    [Theory]
    [InlineData("direct-failover")]
    [InlineData("adaptive")]
    [InlineData("tunnel")]
    public void RenderConfig_RoundTripsTrafficMode(string trafficMode)
    {
        var config = new XNetwork.Models.XBondClientConfig { TrafficMode = trafficMode };

        var reparsed = XBondClientConfigService.ParseConfig(XBondClientConfigService.RenderConfig(config));

        Assert.Equal(trafficMode, reparsed.TrafficMode);
    }

    [Fact]
    public void ApplyPathInterfaceBinding_UpdatesOnlySelectedPathAndClearsStaleAddress()
    {
        var config = new XNetwork.Models.XBondClientConfig
        {
            Paths =
            [
                new() { Id = 1, Name = "Starlink", InterfaceName = "enx-old", BindAddress = "192.168.254.101" },
                new() { Id = 2, Name = "Mobile", InterfaceName = "enx-mobile" }
            ]
        };

        var result = XBondClientConfigService.ApplyPathInterfaceBinding(config, 1, "enx-new");

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal("enx-new", config.Paths[0].InterfaceName);
        Assert.Null(config.Paths[0].BindAddress);
        Assert.Equal("enx-mobile", config.Paths[1].InterfaceName);
    }

    [Fact]
    public void ApplyPathInterfaceBinding_RejectsInterfaceAlreadyOwnedByAnotherPath()
    {
        var config = new XNetwork.Models.XBondClientConfig
        {
            Paths =
            [
                new() { Id = 1, Name = "Starlink", InterfaceName = "enx-old" },
                new() { Id = 2, Name = "Mobile", InterfaceName = "enx-new" }
            ]
        };

        var result = XBondClientConfigService.ApplyPathInterfaceBinding(config, 1, "enx-new");

        Assert.False(result.Success);
        Assert.False(result.Changed);
        Assert.Equal("enx-old", config.Paths[0].InterfaceName);
    }
}
