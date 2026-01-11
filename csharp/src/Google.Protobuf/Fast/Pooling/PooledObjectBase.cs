using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Google.Protobuf.Fast.Pooling;

#pragma warning disable CS1591

/// <summary>
/// Base type for pooled objects.
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
}

