using XNetwork.Services;

namespace XNetwork.Tests;

public class RouteTransitionDirectionServiceTests
{
    [Fact]
    public void NotifyNavigated_UsesTabOrderForForwardAndBackTransitions()
    {
        var service = new RouteTransitionDirectionService();
        service.Initialize("http://xnetwork/");

        service.NotifyNavigated("http://xnetwork/xrouter");

        Assert.False(service.IsBackwards);

        service.NotifyNavigated("http://xnetwork/live");

        Assert.True(service.IsBackwards);
    }

    [Fact]
    public void GetRouteOrder_TreatsLiveAsPrimaryTabBetweenDashboardAndAnalytics()
    {
        Assert.True(RouteTransitionDirectionService.GetRouteOrder("/live") > RouteTransitionDirectionService.GetRouteOrder("/"));
        Assert.True(RouteTransitionDirectionService.GetRouteOrder("/live") < RouteTransitionDirectionService.GetRouteOrder("/details"));
    }

    [Fact]
    public void GetRouteOrder_TreatsWifiAndXRouterAsSameTab()
    {
        Assert.Equal(RouteTransitionDirectionService.GetRouteOrder("/xrouter"), RouteTransitionDirectionService.GetRouteOrder("/wifi"));
    }

    [Fact]
    public void GetRouteOrder_TreatsXBondAsSettingsDetail()
    {
        Assert.Equal(RouteTransitionDirectionService.GetRouteOrder("/settings"), RouteTransitionDirectionService.GetRouteOrder("/xbond"));
    }

    [Theory]
    [InlineData("http://xnetwork/")]
    [InlineData("/")]
    [InlineData("/details")]
    public void GetRouteOrder_NormalizesRootAndRelativePathsWithoutRecursion(string uriOrPath)
    {
        var order = RouteTransitionDirectionService.GetRouteOrder(uriOrPath);

        Assert.InRange(order, 0, 4);
    }
}
