using System.Net;
using XNetwork.Services;

namespace XNetwork.Tests;

public class InterfaceBoundHttpClientFactoryTests
{
    [Fact]
    public void PreferIPv4_PutsIPv4AddressesFirst()
    {
        var ordered = InterfaceBoundHttpClientFactory.PreferIPv4(
        [
            IPAddress.Parse("2606:4700:3108::ac42:2a2b"),
            IPAddress.Parse("104.26.5.15"),
            IPAddress.Parse("2606:4700:3108::ac42:2a2c"),
            IPAddress.Parse("104.26.4.15")
        ]);

        Assert.Equal("104.26.5.15", ordered[0].ToString());
        Assert.Equal("104.26.4.15", ordered[1].ToString());
        Assert.Equal(4, ordered.Count);
        Assert.All(ordered.Take(2), address =>
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, address.AddressFamily));
    }

    [Fact]
    public void PreferIPv4_KeepsRelativeOrderWithinFamily()
    {
        var ordered = InterfaceBoundHttpClientFactory.PreferIPv4(
        [
            IPAddress.Parse("1.1.1.1"),
            IPAddress.Parse("8.8.8.8")
        ]);

        Assert.Equal("1.1.1.1", ordered[0].ToString());
        Assert.Equal("8.8.8.8", ordered[1].ToString());
    }

    [Fact]
    public void PreferIPv4_HandlesIPv6OnlyResults()
    {
        var ordered = InterfaceBoundHttpClientFactory.PreferIPv4([IPAddress.Parse("2001:db8::1")]);

        Assert.Single(ordered);
        Assert.Equal("2001:db8::1", ordered[0].ToString());
    }

    [Fact]
    public void CreateHandler_RequiresAnInterfaceName()
    {
        Assert.Throws<ArgumentException>(() =>
            InterfaceBoundHttpClientFactory.CreateHandler("  ", TimeSpan.FromSeconds(5)));
    }
}
