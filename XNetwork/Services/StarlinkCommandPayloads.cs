using System.Buffers;

namespace XNetwork.Services;

public static class StarlinkCommandPayloads
{
    private const int RebootField = 1001;
    private const int DishStowField = 2002;
    private const int DishStowUnstowField = 1;
    private const int DishClearObstructionMapField = 2017;

    public static byte[] CreatePayload(string command)
    {
        return command.Trim().ToLowerInvariant() switch
        {
            "reboot" => CreateMessageField(RebootField, Array.Empty<byte>()),
            "stow" => CreateMessageField(DishStowField, Array.Empty<byte>()),
            "unstow" => CreateMessageField(DishStowField, CreateBoolField(DishStowUnstowField, true)),
            "dish_clear_obstruction_map" => CreateMessageField(DishClearObstructionMapField, Array.Empty<byte>()),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported Starlink command")
        };
    }

    private static byte[] CreateMessageField(int fieldNumber, byte[] message)
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteVarint(writer, (ulong)((fieldNumber << 3) | 2));
        WriteVarint(writer, (ulong)message.Length);
        writer.Write(message);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] CreateBoolField(int fieldNumber, bool value)
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteVarint(writer, (ulong)(fieldNumber << 3));
        WriteVarint(writer, value ? 1UL : 0UL);
        return writer.WrittenSpan.ToArray();
    }

    private static void WriteVarint(IBufferWriter<byte> writer, ulong value)
    {
        Span<byte> buffer = stackalloc byte[10];
        var index = 0;

        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            buffer[index++] = current;
        } while (value != 0);

        writer.Write(buffer[..index]);
    }
}
