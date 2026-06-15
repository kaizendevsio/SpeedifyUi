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
            mode = "anchor-duplicate-1"
            redundancy_policy = "balanced"
            max_active_backups = 1
            realtime_deadline_ms = 500
            interactive_packet_threshold_bytes = 768
            duplicate_loss_threshold = 0.02
            backup_loss_disable_threshold = 0.35
            reorder_hold_ms = 25
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
        Assert.Equal("anchor-duplicate-1", config.Mode);
        Assert.Equal("balanced", config.RedundancyPolicy);
        Assert.Equal(1, config.MaxActiveBackups);
        Assert.Equal(500, config.RealtimeDeadlineMs);
        Assert.Equal(768, config.InteractivePacketThresholdBytes);
        Assert.Equal(0.02, config.DuplicateLossThreshold);
        Assert.Equal(0.35, config.BackupLossDisableThreshold);
        Assert.Equal(25, config.ReorderHoldMs);
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
        Assert.Contains("redundancy_policy = \"fast\"", rendered);
        Assert.Contains("interactive_packet_threshold_bytes = 512", rendered);
        Assert.Contains("duplicate_loss_threshold = 0.05", rendered);
        Assert.Contains("backup_loss_disable_threshold = 0.25", rendered);
        Assert.Contains("reorder_hold_ms = 15", rendered);
        Assert.Contains("runtime_status_path = \"/run/xbond/client-status.json\"", rendered);
        Assert.Contains("name = \"XNetwork Wi-Fi Asia\"", rendered);
        Assert.Contains("interface_name = \"wlan0\"", rendered);
        Assert.Equal(2, rendered.Split("[[paths]]").Length - 1);

        var reparsed = XBondClientConfigService.ParseConfig(rendered);
        Assert.Equal(["enx103c59f1039c", "wlan0"], reparsed.Paths.Select(path => path.InterfaceName).ToArray());
    }
}
