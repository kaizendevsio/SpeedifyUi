using System.Net.Http;
using System.Net.Sockets;

namespace XNetwork.Services;

/// <summary>
/// Distinguishes "the device on the other end is not answering" from a genuine defect.
/// </summary>
/// <remarks>
/// Background services on this router poll peers that are routinely absent: a Cudy behind an
/// unplugged LAN cable, a modem mid-reboot, a dish that is obstructed. Those attempts throw,
/// but they are expected operating conditions, not bugs. Logging them with a full stack trace
/// on every poll buries the journal and makes real faults hard to find, so callers use this to
/// decide between a one-line notice and a full exception dump.
/// </remarks>
public static class PeerReachability
{
    /// <summary>
    /// True when <paramref name="exception"/> means the peer could not be reached, rather than
    /// that something is wrong with our own logic or configuration.
    /// </summary>
    public static bool IsUnreachable(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SocketException:
                case HttpRequestException:
                // A cancellation that is not our own token firing is a connect or read timeout,
                // which is what an unplugged peer looks like to HttpClient.
                case TaskCanceledException:
                case TimeoutException:
                case IOException:
                    return true;
            }

            if (current is AggregateException aggregate &&
                aggregate.InnerExceptions.Any(IsUnreachable))
            {
                return true;
            }
        }

        return false;
    }
}
