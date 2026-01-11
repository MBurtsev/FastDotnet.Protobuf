using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Google.Protobuf.Fast.Proto;

#pragma warning disable CS1591

/// <summary>Minimal protobuf writer (fast path for generated DTOs)</summary>
public ref struct ProtoWriter
{
    IBufferWriter<byte> _writer;
    Span<byte> _span;
    int _pos;

    public ProtoWriter(IBufferWriter<byte> writer)
    {
        _writer = writer;
        _span   = writer.GetSpan();
        _pos    = 0;
    }

    public void Flush()
    {
        if (_pos == 0)
        {
            return;
        }

        _writer.Advance(_pos);
        _span = _writer.GetSpan();
        _pos = 0;
    }

    public void WriteTag(int fieldNumber, WireType wireType)
    {
        WriteUInt32((uint)((fieldNumber << 3) | (int)wireType));
    }

    public void WriteUInt32(uint value)
    {
        // varint
        while (value >= 0x80)
        {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        WriteByte((byte)value);
    }

    public void WriteUInt64(ulong value)
    {
        while (value >= 0x80)
        {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        WriteByte((byte)value);
    }

    public void WriteInt64(long value)
    {
        WriteUInt64((ulong)value);
    }

    public void WriteFixed32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_span.Slice(_pos, 4), value);
        _pos += 4;
    }

    public void WriteFixed64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_span.Slice(_pos, 8), value);
        _pos += 8;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        WriteUInt32((uint)bytes.Length);
        WriteRaw(bytes);
    }

    public void WriteString(string value)
    {
        // Fast path: empty string
        if (value.Length == 0)
        {
            WriteUInt32(0);
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteUInt32((uint)byteCount);

        // Write directly into the writer without an intermediate array
        Ensure(byteCount);
        var written = Encoding.UTF8.GetBytes(value, _span.Slice(_pos, byteCount));
        _pos += written;
    }

    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        var remaining = _span.Length - _pos;
        if (bytes.Length <= remaining)
        {
            bytes.CopyTo(_span.Slice(_pos));
            _pos += bytes.Length;
            return;
        }

        // If it does not fit - flush and write in chunks
        Flush();

        while (bytes.Length > 0)
        {
            _span = _writer.GetSpan();
            var take = bytes.Length <= _span.Length ? bytes.Length : _span.Length;
            bytes.Slice(0, take).CopyTo(_span);
            _writer.Advance(take);
            bytes = bytes.Slice(take);
        }

        _span = _writer.GetSpan();
        _pos = 0;
    }

    void WriteByte(byte value)
    {
        Ensure(1);
        _span[_pos++] = value;
    }

    void Ensure(int sizeHint)
    {
        if (_span.Length - _pos >= sizeHint)
        {
            return;
        }

        Flush();
        _span = _writer.GetSpan(sizeHint);
    }
}

