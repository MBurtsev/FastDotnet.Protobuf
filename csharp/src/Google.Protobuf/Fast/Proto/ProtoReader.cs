using System;
using System.Buffers.Binary;
using System.Text;

namespace Google.Protobuf.Fast.Proto;

#pragma warning disable CS1591

/// <summary>Minimal protobuf reader (fast path for generated DTOs)</summary>
public ref struct ProtoReader
{
    ReadOnlySpan<byte> _span;
    int _pos;

    public ProtoReader(ReadOnlySpan<byte> span)
    {
        _span = span;
        _pos  = 0;
    }

    /// <summary>Returns the total length of the underlying span (serialized message size)</summary>
    public int Length => _span.Length;

    public bool TryReadTag(out int fieldNumber, out WireType wireType)
    {
        if (_pos >= _span.Length)
        {
            fieldNumber = 0;
            wireType    = 0;
            return false;
        }

        var tag = (int)ReadUInt32();
        fieldNumber = tag >> 3;
        wireType    = (WireType)(tag & 7);
        return fieldNumber != 0;
    }

    public uint ReadUInt32()
    {
        uint result = 0;
        int shift = 0;

        while (true)
        {
            var b = ReadByte();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
            shift += 7;
        }
    }

    public ulong ReadUInt64()
    {
        ulong result = 0;
        int shift = 0;

        while (true)
        {
            var b = ReadByte();
            result |= (ulong)(b & 0x7FUL) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
            shift += 7;
        }
    }

    public long ReadInt64()
    {
        return (long)ReadUInt64();
    }

    public uint ReadFixed32()
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_span.Slice(_pos, 4));
        _pos += 4;
        return value;
    }

    public ulong ReadFixed64()
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_span.Slice(_pos, 8));
        _pos += 8;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        var len = (int)ReadUInt32();
        var slice = _span.Slice(_pos, len);
        _pos += len;
        return slice;
    }

    public string ReadString()
    {
        var bytes = ReadBytes();
        if (bytes.Length == 0)
        {
            return string.Empty;
        }
        return Encoding.UTF8.GetString(bytes);
    }

    public void SkipField(WireType wireType)
    {
        switch (wireType)
        {
            case WireType.Varint:
                ReadUInt64();
                return;
            case WireType.Fixed64:
                _pos += 8;
                return;
            case WireType.LengthDelimited:
                _pos += (int)ReadUInt32();
                return;
            case WireType.Fixed32:
                _pos += 4;
                return;
            default:
                throw new NotSupportedException("WireType " + wireType + " is not supported");
        }
    }

    byte ReadByte()
    {
        return _span[_pos++];
    }
}

