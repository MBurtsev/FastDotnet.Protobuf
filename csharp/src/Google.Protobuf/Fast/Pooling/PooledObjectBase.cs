using System;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using Google.Protobuf.Fast.Proto;

namespace Google.Protobuf.Fast.Pooling;

#pragma warning disable CS1591

/// <summary>
/// Abstract base class for pooled protobuf messages with serialization and logging support.
/// </summary>
/// <remarks>
/// The pool uses <see cref="_isInPool"/> to avoid double-returning the same instance.
/// This field is not a part of protobuf wire format and must not be serialized.
/// </remarks>
public abstract class PooledObjectBase
{
    [NonSerialized]
    [IgnoreDataMember]
    [JsonIgnore]
    internal int _isInPool;

    /// <summary>Size in bytes of the serialized message (set after MergeFrom)</summary>
    [NonSerialized]
    [IgnoreDataMember]
    [JsonIgnore]
    public int SerializedSize;

    /// <summary>
    /// Protects the object from being returned to pool on the next Return() call.
    /// After protection, the first Return() will be ignored, the second will return to pool.
    /// </summary>
    /// <remarks>
    /// Use case: async logging where the logger needs to read object data after the caller
    /// has already called Return(). The logger calls PoolProtectOneTime() before enqueueing,
    /// then calls Return() inside the log delegate after reading the data.
    /// <code>
    /// response.PoolProtectOneTime();  // _isInPool = -1
    /// logger.Info(sb => {
    ///     sb.Append(response.Data);   // safe to read
    ///     response.Return();          // first Return: _isInPool becomes 0, NOT returned to pool
    /// });
    /// // ... caller uses response ...
    /// Response.Return(response);      // second Return: _isInPool is 0, returned to pool
    /// </code>
    /// </remarks>
    public void PoolProtectOneTime()
    {
        Interlocked.Decrement(ref _isInPool);
    }

    /// <summary>
    /// Protects the object from being returned to pool for the specified number of Return() calls.
    /// </summary>
    /// <param name="times">Number of Return() calls to ignore before actually returning to pool.</param>
    /// <remarks>
    /// After calling PoolProtect(N), the object will ignore N Return() calls.
    /// The (N+1)th Return() call will actually return the object to the pool.
    /// </remarks>
    public void PoolProtect(int times)
    {
        Interlocked.Add(ref _isInPool, -times);
    }

    // Abstract methods for protobuf serialization and pool management
    public abstract void Clear();
    public abstract void WriteTo(ref ProtoWriter writer);
    public abstract void MergeFrom(ref ProtoReader reader);
    public abstract void ToLog(StringBuilder sb);
    public abstract void Return();
}

