using System.Buffers.Binary;
using System.Text;

namespace XNetwork.Services;

public static class StarlinkCommandResponseParser
{
    public static void EnsureSuccess(byte[] grpcWebResponse)
    {
        if (grpcWebResponse.Length == 0)
        {
            return;
        }

        var sawTrailer = false;
        var offset = 0;

        while (offset + 5 <= grpcWebResponse.Length)
        {
            var flags = grpcWebResponse[offset];
            var length = BinaryPrimitives.ReadUInt32BigEndian(grpcWebResponse.AsSpan(offset + 1, 4));
            offset += 5;

            if (length > grpcWebResponse.Length - offset)
            {
                return;
            }

            if ((flags & 0x80) != 0)
            {
                sawTrailer = true;
                var trailer = Encoding.ASCII.GetString(grpcWebResponse, offset, (int)length);
                var status = ReadTrailerValue(trailer, "grpc-status");
                if (!string.IsNullOrWhiteSpace(status) && status != "0")
                {
                    var message = ReadTrailerValue(trailer, "grpc-message");
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(message)
                            ? $"Starlink command failed with gRPC status {status}"
                            : $"Starlink command failed with gRPC status {status}: {Uri.UnescapeDataString(message)}");
                }
            }

            offset += (int)length;
        }

        if (sawTrailer)
        {
            return;
        }
    }

    private static string? ReadTrailerValue(string trailer, string key)
    {
        foreach (var line in trailer.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var currentKey = line[..separator].Trim();
            if (currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        return null;
    }
}
