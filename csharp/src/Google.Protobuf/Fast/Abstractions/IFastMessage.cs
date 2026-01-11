namespace Google.Protobuf.Fast.Abstractions;

#pragma warning disable CS1591

/// <summary>
/// Minimal interface for fast protobuf messages generated for performance.
/// </summary>
/// <remarks>
/// IMPORTANT: <see cref="Clear"/> is required for safe pool reuse.
/// </remarks>
public interface IFastMessage
{
    /// <summary>Resets all fields to their default state</summary>
    void Clear();

    /// <summary>Serializes the message to protobuf wire format</summary>
    void WriteTo(ref Proto.ProtoWriter writer);

    /// <summary>Deserializes the message from protobuf wire format</summary>
    void MergeFrom(ref Proto.ProtoReader reader);
}

