using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace XNetwork.Services;

/// <summary>
/// Creates HTTP clients whose TCP sockets are bound to a specific Linux network interface with
/// SO_BINDTODEVICE, so requests egress through that adapter instead of the default route.
/// </summary>
public static class InterfaceBoundHttpClientFactory
{
    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;

    public static HttpClient CreateClient(string interfaceName, TimeSpan timeout)
    {
        return new HttpClient(CreateHandler(interfaceName, timeout), disposeHandler: true)
        {
            Timeout = timeout
        };
    }

    public static SocketsHttpHandler CreateHandler(string interfaceName, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);

        return new SocketsHttpHandler
        {
            ConnectTimeout = timeout,
            EnableMultipleHttp2Connections = true,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!OperatingSystem.IsLinux())
                {
                    throw new InvalidOperationException("Interface-bound HTTP access requires Linux SO_BINDTODEVICE.");
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
        var result = setsockopt(
            socket.Handle,
            SolSocket,
            SoBindToDevice,
            value,
            (uint)value.Length);

        if (result != 0)
        {
            throw new SocketException(Marshal.GetLastPInvokeError());
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(
        IntPtr socket,
        int level,
        int optionName,
        byte[] optionValue,
        uint optionLength);
}
