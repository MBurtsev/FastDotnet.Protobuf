using System;
using System.Buffers;
using Google.Protobuf.Fast.Pooling;

namespace Google.Protobuf.Fast.Proto;

#pragma warning disable CS1591

/// <summary>
/// Helpers for encoding/decoding nested protobuf messages for fast DTOs.
/// </summary>
public static class FastMessageCodec
{
    [ThreadStatic]
    static ArrayBufferWriter<byte> s_buffer;

    public static void WriteMessage(ref ProtoWriter writer, PooledObjectBase message)
    {
        var buffer = s_buffer ??= new ArrayBufferWriter<byte>(256);
        buffer.Clear();

        var w = new ProtoWriter(buffer);
        message.WriteTo(ref w);
        w.Flush();

        writer.WriteUInt32((uint)buffer.WrittenCount);
        writer.WriteRaw(buffer.WrittenSpan);
    }
}

