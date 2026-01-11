namespace Google.Protobuf.Fast.Proto;

#pragma warning disable CS1591

public enum WireType : byte
{
    Varint          = 0,
    Fixed64         = 1,
    LengthDelimited = 2,
    StartGroup      = 3,
    EndGroup        = 4,
    Fixed32         = 5
}

