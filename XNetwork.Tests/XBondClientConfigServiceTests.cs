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
            max_active_backups = 1
            realtime_deadline_ms = 500
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
        Assert.Equal(1, config.MaxActiveBackups);
        Assert.Equal(500, config.RealtimeDeadlineMs);
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
            max_active_backups = 1
            realtime_deadline_ms = 500
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
        Assert.Contains("runtime_status_path = \"/run/xbond/client-status.json\"", rendered);
        Assert.Contains("name = \"XNetwork Wi-Fi Asia\"", rendered);
        Assert.Contains("interface_name = \"wlan0\"", rendered);
        Assert.Equal(2, rendered.Split("[[paths]]").Length - 1);

        var reparsed = XBondClientConfigService.ParseConfig(rendered);
        Assert.Equal(["enx103c59f1039c", "wlan0"], reparsed.Paths.Select(path => path.InterfaceName).ToArray());
    }
}
