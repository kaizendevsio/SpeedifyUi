using System.Buffers.Binary;
using System.Text;
using XNetwork.Services;

namespace XNetwork.Tests;

public class StarlinkCommandResponseParserTests
{
    [Fact]
    public void EnsureSuccess_AllowsGrpcStatusZero()
    {
        var response = TrailerFrame("grpc-status: 0\r\n");

        StarlinkCommandResponseParser.EnsureSuccess(response);
    }

    [Fact]
    public void EnsureSuccess_ThrowsForNonZeroGrpcStatus()
    {
        var response = TrailerFrame("grpc-status: 13\r\ngrpc-message: command%20failed\r\n");

        var exception = Assert.Throws<InvalidOperationException>(
            () => StarlinkCommandResponseParser.EnsureSuccess(response));

        Assert.Contains("command failed", exception.Message);
    }

    private static byte[] TrailerFrame(string trailer)
    {
        var bytes = Encoding.ASCII.GetBytes(trailer);
        var frame = new byte[5 + bytes.Length];
        frame[0] = 0x80;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)bytes.Length);
        bytes.CopyTo(frame.AsSpan(5));
        return frame;
    }
}
