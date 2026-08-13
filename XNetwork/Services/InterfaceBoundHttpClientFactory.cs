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

                return await ConnectBoundAsync(context.DnsEndPoint, interfaceName, cancellationToken).ConfigureAwait(false);
            }
        };
    }

    /// <summary>
    /// Connects to the endpoint through <paramref name="interfaceName"/>, trying each resolved
    /// address on its own fresh socket. A socket that has failed a connect cannot be reused on
    /// Linux, so per-address sockets are required whenever a host resolves to more than one address.
    /// </summary>
    private static async Task<Stream> ConnectBoundAsync(
        DnsEndPoint endpoint,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Count == 0)
        {
            throw new IOException($"Could not resolve {endpoint.Host} for interface-bound access through {interfaceName}.");
        }

        var failures = new List<Exception>();
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                BindSocketToDevice(socket, interfaceName);
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();

                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                failures.Add(new IOException($"{address}: {ex.Message}", ex));
            }
        }

        throw new IOException(
            $"Could not connect to {endpoint.Host}:{endpoint.Port} through {interfaceName}. {string.Join("; ", failures.Select(failure => failure.Message))}",
            failures[0]);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAddressesAsync(
        string host,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        var resolved = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return PreferIPv4(resolved);
    }

    /// <summary>
    /// Orders resolved addresses IPv4 first. uLink path routing is IPv4-only on this branch, so an
    /// IPv6 answer would describe a different egress than the tunnel paths actually use.
    /// </summary>
    public static IReadOnlyList<IPAddress> PreferIPv4(IEnumerable<IPAddress> addresses)
    {
        return addresses
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();
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
