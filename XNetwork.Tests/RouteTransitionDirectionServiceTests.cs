using XNetwork.Services;

namespace XNetwork.Tests;

public class RouteTransitionDirectionServiceTests
{
    [Fact]
    public void NotifyNavigated_UsesTabOrderForForwardAndBackTransitions()
    {
        var service = new RouteTransitionDirectionService();
        service.Initialize("http://xnetwork/");

        service.NotifyNavigated("http://xnetwork/xbond");

        Assert.False(service.IsBackwards);

        service.NotifyNavigated("http://xnetwork/details");

        Assert.True(service.IsBackwards);
    }

    [Fact]
    public void GetRouteOrder_TreatsWifiAndXRouterAsSameTab()
    {
        Assert.Equal(RouteTransitionDirectionService.GetRouteOrder("/xrouter"), RouteTransitionDirectionService.GetRouteOrder("/wifi"));
    }
}
