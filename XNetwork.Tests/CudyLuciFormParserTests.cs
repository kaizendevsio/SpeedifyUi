using XNetwork.Services;

namespace XNetwork.Tests;

public class CudyLuciFormParserTests
{
    [Fact]
    public void ParseFieldsReadsCudyWirelessInputsAndSelectedOptions()
    {
        const string html = """
            <form method="post" action="/cgi-bin/luci/admin/network/wireless/config/uncombine/embedded/nomodal">
                <input type="hidden" name="token" value="abc123" />
                <input type="hidden" name="cbi.submit" value="1" />
                <input type="hidden" value="1" name="cbi.cbe.wireless.wlan00.disabled" />
                <input type="hidden" id="cbid.wireless.wlan00.disabled" name="cbid.wireless.wlan00.disabled" value="0" />
                <input type="text" name="cbid.wireless.wlan00.ssid" value="Cudy-200C" />
                <select id="cbid.wireless.wlan00.encryption" name="cbid.wireless.wlan00.encryption">
                    <option value="none">No Encryption</option>
                    <option value="psk-mixed" selected="selected">WPA-PSK/WPA2-PSK</option>
                </select>
                <button type="submit" name="cbi.apply">Save &amp; Apply</button>
            </form>
            """;

        var fields = CudyLuciFormParser.ParseFields(html);

        Assert.Equal("abc123", fields["token"]);
        Assert.Equal("1", fields["cbi.submit"]);
        Assert.Equal("1", fields["cbi.cbe.wireless.wlan00.disabled"]);
        Assert.Equal("0", fields["cbid.wireless.wlan00.disabled"]);
        Assert.Equal("Cudy-200C", fields["cbid.wireless.wlan00.ssid"]);
        Assert.Equal("psk-mixed", fields["cbid.wireless.wlan00.encryption"]);
        Assert.DoesNotContain("cbi.apply", fields.Keys);
    }

    [Fact]
    public void ParseFormActionIgnoresPlaceholderActions()
    {
        const string html = """
            <form action="#"></form>
            <form action="/cgi-bin/luci/admin/network/wireless/config/combine/embedded/nomodal"></form>
            """;

        var action = CudyLuciFormParser.ParseFormAction(html);

        Assert.Equal("/cgi-bin/luci/admin/network/wireless/config/combine/embedded/nomodal", action);
    }
}
