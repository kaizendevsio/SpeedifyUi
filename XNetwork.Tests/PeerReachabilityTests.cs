using System.Net.Sockets;
using XNetwork.Services;

namespace XNetwork.Tests;

public class PeerReachabilityTests
{
    [Fact]
    public void ARefusedConnectionIsTreatedAsUnreachable()
    {
        Assert.True(PeerReachability.IsUnreachable(
            new SocketException((int)SocketError.ConnectionRefused)));
    }

    [Fact]
    public void TheSocketFailureIsFoundThroughHttpClientsWrapping()
    {
        // What CudyLuciClient actually surfaces when the LAN cable is out: an
        // HttpRequestException wrapping a SocketException wrapping the real cause.
        var wrapped = new HttpRequestException(
            "Connection failure",
            new SocketException((int)SocketError.HostUnreachable));

        Assert.True(PeerReachability.IsUnreachable(wrapped));
    }

    [Fact]
    public void AConnectTimeoutIsTreatedAsUnreachable()
    {
        Assert.True(PeerReachability.IsUnreachable(
            new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")));
    }

    [Fact]
    public void AFailureBuriedInAnAggregateIsStillFound()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("unrelated"),
            new SocketException((int)SocketError.NetworkUnreachable));

        Assert.True(PeerReachability.IsUnreachable(aggregate));
    }

    [Fact]
    public void AMisconfigurationIsNotUnreachable()
    {
        // This one is our own fault and must keep its stack trace.
        Assert.False(PeerReachability.IsUnreachable(
            new InvalidOperationException("Cudy management base URL is not configured.")));
    }

    [Fact]
    public void AParsingBugIsNotUnreachable()
    {
        Assert.False(PeerReachability.IsUnreachable(
            new FormatException("unexpected LuCI token")));
    }

    [Fact]
    public void NoExceptionIsNotUnreachable()
    {
        Assert.False(PeerReachability.IsUnreachable(null));
    }
}
