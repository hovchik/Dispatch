using System.Buffers.Binary;
using System.Text;

namespace Dispatch.Application.Grpc;

public enum WireType
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    StartGroup = 3,
    EndGroup = 4,
    Fixed32 = 5
}

/// <summary>Reads the protobuf binary wire format.</summary>
public ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public bool End => _position >= _data.Length;

    public (int Field, WireType Wire) ReadTag()
    {
        var tag = ReadVarint();
        var field = (int)(tag >> 3);
        if (field <= 0)
            throw new FormatException("Invalid field number in protobuf data.");
        return (field, (WireType)(tag & 7));
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_position >= _data.Length)
                throw new FormatException("Truncated varint in protobuf data.");
            var b = _data[_position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
        }
        throw new FormatException("Malformed varint in protobuf data.");
    }

    public uint ReadFixed32()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
        _position += 4;
        return v;
    }

    public ulong ReadFixed64()
    {
        Need(8);
        var v = BinaryPrimitives.ReadUInt64LittleEndian(_data[_position..]);
        _position += 8;
        return v;
    }

    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        var length = (int)ReadVarint();
        Need(length);
        var span = _data.Slice(_position, length);
        _position += length;
        return span;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadLengthDelimited());

    public void Skip(WireType wire, int field = 0)
    {
        switch (wire)
        {
            case WireType.Varint: ReadVarint(); break;
            case WireType.Fixed64: Need(8); _position += 8; break;
            case WireType.Fixed32: Need(4); _position += 4; break;
            case WireType.LengthDelimited: ReadLengthDelimited(); break;
            case WireType.StartGroup:
                while (true)
                {
                    var (f, w) = ReadTag();
                    if (w == WireType.EndGroup)
                    {
                        if (field != 0 && f != field)
                            throw new FormatException("Mismatched group end tag.");
                        break;
                    }
                    Skip(w, f);
                }
                break;
            default: throw new FormatException($"Unsupported wire type {wire}.");
        }
    }

    /// <summary>The raw bytes of a group (after its start tag), up to and excluding its end tag.</summary>
    public ReadOnlySpan<byte> ReadGroup(int field)
    {
        var start = _position;
        while (true)
        {
            var before = _position;
            var (f, w) = ReadTag();
            if (w == WireType.EndGroup && f == field)
                return _data[start..before];
            Skip(w, f);
        }
    }

    private void Need(int count)
    {
        if (_position + count > _data.Length || count < 0)
            throw new FormatException("Truncated protobuf data.");
    }
}

/// <summary>Writes the protobuf binary wire format.</summary>
public sealed class ProtoWriter
{
    private readonly MemoryStream _stream = new();

    public void WriteTag(int field, WireType wire) => WriteVarint(((ulong)field << 3) | (uint)wire);

    public void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _stream.WriteByte((byte)value);
    }

    public void WriteFixed32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void WriteFixed64(ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        WriteVarint((ulong)bytes.Length);
        _stream.Write(bytes);
    }

    public void WriteString(string value) => WriteBytes(Encoding.UTF8.GetBytes(value));

    public void WriteRaw(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    public byte[] ToArray() => _stream.ToArray();

    public static ulong ZigZag64(long v) => (ulong)((v << 1) ^ (v >> 63));
    public static uint ZigZag32(int v) => (uint)((v << 1) ^ (v >> 31));
    public static long UnZigZag64(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);
    public static int UnZigZag32(uint v) => (int)(v >> 1) ^ -(int)(v & 1);
}
