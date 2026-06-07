using System.Buffers.Binary;
using System.Text;
using XNetwork.Models;

namespace XNetwork.Services;

public static class StarlinkStatusParser
{
    private const int DishStatusResponseField = 2004;

    public static StarlinkTelemetrySnapshot ParseGrpcResponse(byte[] grpcResponse)
    {
        foreach (var message in ReadGrpcMessages(grpcResponse))
        {
            var span = message.Span;
            var offset = 0;

            while (TryReadField(span, ref offset, out var fieldNumber, out var wireType, out var varint, out var bytes, out var float32))
            {
                _ = varint;
                _ = float32;

                if (fieldNumber == DishStatusResponseField && wireType == 2)
                {
                    return ParseDishStatus(bytes);
                }
            }
        }

        throw new InvalidOperationException("Starlink status response did not include dish status.");
    }

    private static IEnumerable<ReadOnlyMemory<byte>> ReadGrpcMessages(byte[] content)
    {
        var offset = 0;

        while (offset + 5 <= content.Length)
        {
            var flags = content[offset];
            var length = BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(offset + 1, 4));
            offset += 5;

            if (length > content.Length - offset)
            {
                yield break;
            }

            var isTrailerFrame = (flags & 0x80) != 0;
            if (!isTrailerFrame)
            {
                if ((flags & 0x01) != 0)
                {
                    throw new InvalidOperationException("Compressed Starlink gRPC responses are not supported.");
                }

                yield return content.AsMemory(offset, (int)length);
            }

            offset += (int)length;
        }
    }

    private static StarlinkTelemetrySnapshot ParseDishStatus(ReadOnlySpan<byte> message)
    {
        var activeAlerts = new List<string>();
        string? deviceId = null;
        string? hardwareVersion = null;
        string? softwareVersion = null;
        string? dishState = null;
        long? uptimeSeconds = null;
        double? popPingDropRate = null;
        double? downlinkMbps = null;
        double? uplinkMbps = null;
        double? popPingLatencyMs = null;
        double? obstructionPercent = null;
        bool? currentlyObstructed = null;
        bool? gpsValid = null;
        int? gpsSatellites = null;
        double? boresightAzimuthDegrees = null;
        double? boresightElevationDegrees = null;
        double? alignmentErrorDegrees = null;

        var offset = 0;
        while (TryReadField(message, ref offset, out var fieldNumber, out var wireType, out var varint, out var bytes, out var float32))
        {
            switch (fieldNumber)
            {
                case 1 when wireType == 2:
                    (deviceId, hardwareVersion, softwareVersion) = ParseDeviceInfo(bytes);
                    break;
                case 2 when wireType == 2:
                    uptimeSeconds = ParseDeviceUptime(bytes);
                    break;
                case 1003 when wireType == 5:
                    popPingDropRate = float32;
                    break;
                case 1004 when wireType == 2:
                    (obstructionPercent, currentlyObstructed) = ParseObstructionStats(bytes);
                    break;
                case 1005 when wireType == 2:
                    activeAlerts.AddRange(ParseAlerts(bytes));
                    break;
                case 1006 when wireType == 0:
                    dishState = FormatDishState((int)varint);
                    break;
                case 1007 when wireType == 5:
                    downlinkMbps = BitsPerSecondToMbps(float32);
                    break;
                case 1008 when wireType == 5:
                    uplinkMbps = BitsPerSecondToMbps(float32);
                    break;
                case 1009 when wireType == 5:
                    popPingLatencyMs = float32;
                    break;
                case 1011 when wireType == 5:
                    boresightAzimuthDegrees = float32;
                    break;
                case 1012 when wireType == 5:
                    boresightElevationDegrees = float32;
                    break;
                case 1015 when wireType == 2:
                    (gpsValid, gpsSatellites) = ParseGpsStats(bytes);
                    break;
                case 1027 when wireType == 2:
                    alignmentErrorDegrees = ParseAlignmentError(bytes);
                    break;
            }
        }

        if (currentlyObstructed == true && activeAlerts.All(a => !a.Equals("Obstructed", StringComparison.OrdinalIgnoreCase)))
        {
            activeAlerts.Add("Obstructed");
        }

        return new StarlinkTelemetrySnapshot
        {
            IsAvailable = true,
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            DeviceId = deviceId,
            HardwareVersion = hardwareVersion,
            SoftwareVersion = softwareVersion,
            DishState = dishState,
            UptimeSeconds = uptimeSeconds,
            PopPingDropRate = popPingDropRate,
            DownlinkMbps = downlinkMbps,
            UplinkMbps = uplinkMbps,
            PopPingLatencyMs = popPingLatencyMs,
            ObstructionPercent = obstructionPercent,
            CurrentlyObstructed = currentlyObstructed,
            GpsValid = gpsValid,
            GpsSatellites = gpsSatellites,
            BoresightAzimuthDegrees = boresightAzimuthDegrees,
            BoresightElevationDegrees = boresightElevationDegrees,
            AlignmentErrorDegrees = alignmentErrorDegrees,
            ActiveAlerts = activeAlerts
        };
    }

    private static (string? DeviceId, string? HardwareVersion, string? SoftwareVersion) ParseDeviceInfo(ReadOnlySpan<byte> message)
    {
        string? deviceId = null;
        string? hardwareVersion = null;
        string? softwareVersion = null;

        var offset = 0;
        while (TryReadField(message, ref offset, out var fieldNumber, out var wireType, out _, out var bytes, out _))
        {
            if (wireType != 2)
            {
                continue;
            }

            switch (fieldNumber)
            {
                case 1:
                    deviceId = ToUtf8String(bytes);
                    break;
                case 2:
                    hardwareVersion = ToUtf8String(bytes);
                    break;
                case 3:
                    softwareVersion = ToUtf8String(bytes);
                    break;
            }
        }

        return (deviceId, hardwareVersion, softwareVersion);
    }

    private static long? ParseDeviceUptime(ReadOnlySpan<byte> message)
    {
        var offset = 0;
        while (TryReadField(message, ref offset, out var fieldNumber, out var wireType, out var varint, out _, out _))
        {
            if (fieldNumber == 1 && wireType == 0)
            {
                return (long)varint;
            }
        }

        return null;
    }

    private static (double? ObstructionPercent, bool? CurrentlyObstructed) ParseObstructionStats(ReadOnlySpan<byte> message)
    {
        double? obstructionPercent = null;
        bool? currentlyObstructed = null;

        var offset = 0;
        while (TryReadField(message, ref offset, out var fieldNumber, out var wireType, out var varint, out _, out var float32))
        {
            switch (fieldNumber)
            {
                case 1 when wireType == 5:
                    obstructionPercent = Math.Max(0, float32 * 100.0);
                    break;
                case 5 when wireType == 0:
                    currentlyObstructed = varint != 0;
                    break;
            }
        }

        return (obstructionPercent, currentlyObstructed);
    }

    private static (bool? GpsValid, int? GpsSatellites) ParseGpsStats(ReadOnlySpan<byte> message)
    {
        bool? gpsValid = null;
        int? gpsSatellites = null;

        var offset = 0;
        while (TryReadField(message, ref offset, out var fieldNumber, out var wireType, out var varint, out _, out _))
        {
            switch (fieldNumber)
            {
                case 1 when wireType == 0:
                    gpsValid = varint != 0;
                    break;
                case 2 when wireType == 0:
                    gpsSatellites = (int)varint;
                    break;
            }
        }

        return (gpsValid, gpsSatellites);
    }

    private static double? ParseAlignmentError(ReadOnlySpan<byte> message)
    {
        var offset = 0;
        while (TryReadField(message, ref offset, out var fieldNumber, out var wireType, out _, out _, out var float32))
        {
            if (fieldNumber == 3 && wireType == 5)
            {
                return float32;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ParseAlerts(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty)
        {
            return Array.Empty<string>();
        }

        var activeAlerts = new List<string>();
        var offset = 0;

        while (TryReadField(message, ref offset, out var fieldNumber, out var wireType, out var varint, out _, out _))
        {
            if (wireType != 0 || varint == 0)
            {
                continue;
            }

            var label = fieldNumber switch
            {
                1 => "Motors stuck",
                2 => "Thermal shutdown",
                3 => "Thermal throttle",
                4 => "Unexpected location",
                5 => "Mast not vertical",
                6 => "Slow Ethernet",
                7 => "Roaming",
                8 => "Install pending",
                9 => "Heating",
                10 => "Power thermal",
                11 => "Low motor current",
                12 => "Slow Ethernet 100",
                13 => "Moving too fast",
                _ => $"Alert {fieldNumber}"
            };

            activeAlerts.Add(label);
        }

        return activeAlerts;
    }

    private static bool TryReadField(
        ReadOnlySpan<byte> message,
        ref int offset,
        out int fieldNumber,
        out int wireType,
        out ulong varintValue,
        out ReadOnlySpan<byte> bytes,
        out float floatValue)
    {
        fieldNumber = 0;
        wireType = 0;
        varintValue = 0;
        bytes = default;
        floatValue = 0;

        if (offset >= message.Length)
        {
            return false;
        }

        if (!TryReadVarint(message, ref offset, out var key))
        {
            return false;
        }

        fieldNumber = (int)(key >> 3);
        wireType = (int)(key & 0x07);

        switch (wireType)
        {
            case 0:
                return TryReadVarint(message, ref offset, out varintValue);
            case 2:
                if (!TryReadVarint(message, ref offset, out var length) || length > int.MaxValue)
                {
                    return false;
                }

                var byteLength = (int)length;
                if (byteLength > message.Length - offset)
                {
                    return false;
                }

                bytes = message.Slice(offset, byteLength);
                offset += byteLength;
                return true;
            case 5:
                if (message.Length - offset < 4)
                {
                    return false;
                }

                floatValue = BinaryPrimitives.ReadSingleLittleEndian(message.Slice(offset, 4));
                offset += 4;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> message, ref int offset, out ulong value)
    {
        value = 0;
        var shift = 0;

        while (offset < message.Length && shift < 64)
        {
            var current = message[offset++];
            value |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    private static string ToUtf8String(ReadOnlySpan<byte> bytes)
    {
        return Encoding.UTF8.GetString(bytes);
    }

    private static double BitsPerSecondToMbps(double bitsPerSecond)
    {
        return bitsPerSecond / 1_000_000.0;
    }

    private static string FormatDishState(int state)
    {
        return state switch
        {
            1 => "Connected",
            2 => "Searching",
            3 => "Booting",
            _ => "Unknown"
        };
    }
}
