using XNetwork.Services;

namespace XNetwork.Tests;

public class WifiServiceTests
{
    [Fact]
    public void ParseWifiInterfacesReadsManagedWifiDevices()
    {
        const string output = """
            wlan0:wifi:connected:XNetwork Wi-Fi Asia
            wlan1:wifi:disconnected:--
            p2p-dev-wlan0:wifi:disconnected:--
            eth0:ethernet:connected:netplan-eth0
            """;

        var devices = WifiService.ParseWifiInterfaces(output);

        Assert.Equal(2, devices.Count);
        Assert.Equal("wlan0", devices[0].InterfaceName);
        Assert.Equal("XNetwork Wi-Fi Asia", devices[0].ConnectionName);
        Assert.Equal("wlan1", devices[1].InterfaceName);
        Assert.Null(devices[1].ConnectionName);
    }

    [Fact]
    public void SelectDefaultInterfacePrefersConnectedThenWlan0ThenFirst()
    {
        var devices = WifiService.ParseWifiInterfaces("""
            wlan1:wifi:disconnected:--
            wlan0:wifi:disconnected:--
            usbwifi0:wifi:connected:Mobile AP
            """);

        Assert.Equal("usbwifi0", WifiService.SelectDefaultInterface(devices));
        Assert.Equal("wlan1", WifiService.SelectDefaultInterface(devices, "wlan1"));
    }
}
