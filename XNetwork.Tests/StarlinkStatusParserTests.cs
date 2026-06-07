using System.Buffers.Binary;
using XNetwork.Services;

namespace XNetwork.Tests;

public class StarlinkStatusParserTests
{
    [Fact]
    public void ParseGrpcResponse_ReadsDirectDishTelemetryFields()
    {
        var deviceInfo = Message(
            StringField(1, "ut-test"),
            StringField(2, "mini1_prod2"),
            StringField(3, "2026.05.28.test"));

        var deviceState = Message(VarintField(1, 3661));
        var obstruction = Message(
            Fixed32Field(1, 0.025f),
            VarintField(5, 1));
        var alerts = Message(VarintField(6, 1));
        var gps = Message(
            VarintField(1, 1),
            VarintField(2, 15));
        var alignment = Message(Fixed32Field(3, 16.5f));

        var dishStatus = Message(
            BytesField(1, deviceInfo),
            BytesField(2, deviceState),
            Fixed32Field(1003, 0.01f),
            BytesField(1004, obstruction),
            BytesField(1005, alerts),
            Fixed32Field(1007, 50_000_000f),
            Fixed32Field(1008, 5_000_000f),
            Fixed32Field(1009, 42.5f),
            Fixed32Field(1011, 120.5f),
            Fixed32Field(1012, 75.25f),
            BytesField(1015, gps),
            BytesField(1027, alignment));

        var response = GrpcFrame(Message(BytesField(2004, dishStatus)));

        var snapshot = StarlinkStatusParser.ParseGrpcResponse(response);

        Assert.True(snapshot.IsAvailable);
        Assert.Equal("ut-test", snapshot.DeviceId);
        Assert.Equal("mini1_prod2", snapshot.HardwareVersion);
        Assert.Equal("2026.05.28.test", snapshot.SoftwareVersion);
        Assert.Equal(3661, snapshot.UptimeSeconds);
        Assert.Equal(42.5, snapshot.PopPingLatencyMs!.Value, precision: 4);
        Assert.Equal(0.01, snapshot.PopPingDropRate!.Value, precision: 4);
        Assert.Equal(50, snapshot.DownlinkMbps!.Value, precision: 4);
        Assert.Equal(5, snapshot.UplinkMbps!.Value, precision: 4);
        Assert.Equal(2.5, snapshot.ObstructionPercent!.Value, precision: 4);
        Assert.True(snapshot.CurrentlyObstructed);
        Assert.True(snapshot.GpsValid);
        Assert.Equal(15, snapshot.GpsSatellites);
        Assert.Equal(120.5, snapshot.BoresightAzimuthDegrees!.Value, precision: 4);
        Assert.Equal(75.25, snapshot.BoresightElevationDegrees!.Value, precision: 4);
        Assert.Equal(16.5, snapshot.AlignmentErrorDegrees!.Value, precision: 4);
        Assert.Contains("Slow Ethernet", snapshot.ActiveAlerts);
        Assert.Contains("Obstructed", snapshot.ActiveAlerts);
    }

    private static byte[] GrpcFrame(byte[] message)
    {
        var frame = new byte[5 + message.Length];
        frame[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)message.Length);
        message.CopyTo(frame.AsSpan(5));
        return frame;
    }

    private static byte[] Message(params byte[][] fields)
    {
        return fields.SelectMany(field => field).ToArray();
    }

    private static byte[] StringField(int fieldNumber, string value)
    {
        return BytesField(fieldNumber, System.Text.Encoding.UTF8.GetBytes(value));
    }

    private static byte[] BytesField(int fieldNumber, byte[] bytes)
    {
        return Message(
            Varint((ulong)((fieldNumber << 3) | 2)),
            Varint((ulong)bytes.Length),
            bytes);
    }

    private static byte[] VarintField(int fieldNumber, ulong value)
    {
        return Message(
            Varint((ulong)(fieldNumber << 3)),
            Varint(value));
    }

    private static byte[] Fixed32Field(int fieldNumber, float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);

        return Message(
            Varint((ulong)((fieldNumber << 3) | 5)),
            bytes);
    }

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            bytes.Add(current);
        } while (value != 0);

        return bytes.ToArray();
    }
}
