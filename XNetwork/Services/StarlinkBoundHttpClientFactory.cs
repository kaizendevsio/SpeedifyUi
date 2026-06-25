using System.Net;
using System.Net.Sockets;
using System.Text;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class StarlinkHttpClientLease : IDisposable
{
    public StarlinkHttpClientLease(HttpClient client, string interfaceName, string reason)
    {
        Client = client;
        InterfaceName = interfaceName;
        Reason = reason;
    }

    public HttpClient Client { get; }

    public string InterfaceName { get; }

    public string Reason { get; }

    public void Dispose()
    {
        Client.Dispose();
    }
}

public interface IStarlinkHttpClientFactory
{
    Task<StarlinkHttpClientLease> CreateAsync(CancellationToken cancellationToken = default);
}

public sealed class StarlinkBoundHttpClientFactory(
    StarlinkTelemetrySettings settings,
    IStarlinkInterfaceResolver interfaceResolver) : IStarlinkHttpClientFactory
{
    private const int SoBindToDevice = 25;

    public async Task<StarlinkHttpClientLease> CreateAsync(CancellationToken cancellationToken = default)
    {
        var resolution = await interfaceResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable || string.IsNullOrWhiteSpace(resolution.InterfaceName))
        {
            throw new InvalidOperationException($"Starlink interface unavailable: {resolution.Reason}");
        }

        return new StarlinkHttpClientLease(
            CreateHttpClient(settings, resolution.InterfaceName),
            resolution.InterfaceName,
            resolution.Reason);
    }

    public static HttpClient CreateHttpClient(StarlinkTelemetrySettings settings, string interfaceName)
    {
        return new HttpClient(CreateHandler(settings, interfaceName), disposeHandler: true)
        {
            Timeout = settings.RequestTimeout
        };
    }

    public static SocketsHttpHandler CreateHandler(StarlinkTelemetrySettings settings, string interfaceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);

        return new SocketsHttpHandler
        {
            ConnectTimeout = settings.RequestTimeout,
            EnableMultipleHttp2Connections = true,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!OperatingSystem.IsLinux())
                {
                    throw new InvalidOperationException("Interface-bound Starlink access requires Linux SO_BINDTODEVICE.");
                }

                var socket = CreateSocket(context.DnsEndPoint);
                try
                {
                    BindSocketToDevice(socket, interfaceName);
                    await ConnectAsync(socket, context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    private static Socket CreateSocket(DnsEndPoint endpoint)
    {
        var family = IPAddress.TryParse(endpoint.Host, out var address)
            ? address.AddressFamily
            : AddressFamily.InterNetwork;

        return new Socket(family, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
    }

    private static Task ConnectAsync(Socket socket, DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        return IPAddress.TryParse(endpoint.Host, out var address)
            ? socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).AsTask()
            : socket.ConnectAsync(endpoint, cancellationToken).AsTask();
    }

    private static void BindSocketToDevice(Socket socket, string interfaceName)
    {
        var value = Encoding.ASCII.GetBytes(interfaceName + '\0');
        socket.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)SoBindToDevice, value);
    }
}
