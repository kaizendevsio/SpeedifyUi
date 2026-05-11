using XNetwork.Services;

namespace XNetwork.Tests;

public class CudyXRouterClientParserTests
{
    [Fact]
    public void ParseClientsReadsCudyDeviceRows()
    {
        const string html = """
            <tr id="cbi-table-1" data-sid="1" data-store="cbi.sts.table.table">
                <td class="hidden-xs"><div id="cbi-table-1-hostname">
                    <p class="form-control-static hidden-xs">iPhone<br /><span class="text-primary">5G WiFi</span></p>
                </div></td>
                <td class="hidden-xs"><div id="cbi-table-1-ipmac">
                    <p class="form-control-static hidden-xs">192.168.10.86<br />F6:40:5F:74:F4:D8</p>
                </div></td>
                <td class="hidden-xs"><div id="cbi-table-1-speed">
                    <p class="form-control-static hidden-xs"><i class="fa fa-long-arrow-up"></i> 0.80 Kbps<br /><i class="fa fa-long-arrow-down"></i> 106.38 Kbps</p>
                </div></td>
                <td class="hidden-xs"><div id="cbi-table-1-signal"><p class="form-control-static hidden-xs">-37 dBm</p></div></td>
                <td class="hidden-xs"><div id="cbi-table-1-online"><p class="form-control-static hidden-xs">00:19:59</p></div></td>
                <td><div id="cbi-table-1-internet">
                    <input type="hidden" id="cbid.table.1.internet" name="cbid.table.1.internet" value="1" />
                </div></td>
                <td><div id="cbi-table-1-devinfo">
                    <button onclick='cbi_show_modal(this, "/cgi-bin/luci/admin/network/devices/devinfo", "macaddr=F6:40:5F:74:F4:D8&amp;hostname=iPhone&amp;internet=1&amp;vpn=0&amp;dnsfilter=1");return false;'></button>
                </div></td>
            </tr>
            """;

        var clients = CudyXRouterClientParser.ParseClients(html);

        var client = Assert.Single(clients);
        Assert.Equal("iPhone", client.Hostname);
        Assert.Equal("5G WiFi", client.ConnectionType);
        Assert.Equal("192.168.10.86", client.IpAddress);
        Assert.Equal("F6:40:5F:74:F4:D8", client.MacAddress);
        Assert.Equal(0.0008, client.UploadMbps, 4);
        Assert.Equal(0.10638, client.DownloadMbps, 5);
        Assert.Equal(-37, client.SignalDbm);
        Assert.Equal("00:19:59", client.OnlineDuration);
        Assert.True(client.InternetAllowed);
        Assert.False(client.VpnEnabled);
        Assert.True(client.DnsFilterEnabled);
    }
}
