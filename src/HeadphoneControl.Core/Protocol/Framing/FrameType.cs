namespace HeadphoneControl.Protocol.Framing;

/// <summary>Logical frame type byte (spec section 4.4).</summary>
public enum FrameType : byte
{
    Data = 0,
    Ack = 1,
    DataMcNo1 = 2,
    DataIcd = 9,
    DataEv = 10,
    DataMdr = 12,
    DataCommon = 13,
    DataMdrNo2 = 14,
    Shot = 16,
    ShotMcNo1 = 18,
    ShotIcd = 25,
    ShotEv = 26,
    ShotMdr = 28,
    ShotCommon = 29,
    ShotMdrNo2 = 30,
    LargeDataCommon = 45,
}
