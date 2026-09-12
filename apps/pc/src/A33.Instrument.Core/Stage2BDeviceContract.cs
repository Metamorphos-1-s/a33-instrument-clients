namespace A33.Instrument.Core;

public static class Stage2BDeviceContract
{
    public const ushort FirmwareVersion = 0x0510;
    public const ushort SchemaVersion = 2;
    public const ushort PersistentFormatVersion = 3;
    public const ushort SlotSchemaVersion = 3;
    public const ushort SlotPayloadLength = 281;
    public const ushort RegisterMapVersion = 0x0104;
    public const byte UnitId = 1;
    public const string TcpHost = "192.168.1.100";
    public const int TcpPort = 502;
    public const string TcpEndpoint = "192.168.1.100:502";
    public const string Stm32ProductionCommit = "785ce21e181fcf00aa371290facec6b7a14e484e";
    public const string Stm32EvidenceCommit = "8ef44f5643b83bec1a677b047f7535e5668ce229";
    public const string Stm32ReleaseElfSha256 = "82E726F5B32A0DE36A5E686F62A937EC4FD9CBB488DB9733E83D2062673EF486";
    public const string SourceEvidenceCommit = "22c03070a7fb83cc1faca133e3ceb96271f663bf";
    public const string BaselineId = "a33-stage2c-fw0510-brightness3-20260912";
    public const string BaselineRoot = "Results/pc_stage2c_0510_baseline";
    public const string PersistenceEvidenceRoot = "Results/pc_stage2c_0510_hw";
    public const uint BatteryDividerTopOhm = 47000;
    public const uint BatteryDividerBottomOhm = 10000;
    public const ushort OriginalBrightness = 3;
    public const ushort TestBrightness = 4;
    public const ushort ExpectedBaselineSlot = 1;
    public const uint ExpectedBaselineSequence = 3;
    public const uint ExpectedBaselineRevision = 3;
    public const int PreconditioningSaveCount = 2;
    public const int PreconditioningPowerCycleCount = 2;
    public const string PcJsonActiveSha256 = "91D346E87BD112EFAC3B513A8CAFBBDDE9642069A15280DB7565374378BA43E1";
    public const string Stm32BinaryActiveSha256 = "F596F7460C911607FA8328A6D4BA5725EA5A0E5B14765D61EE7889A0D62F48A4";

    public static bool Matches(DeviceIdentity identity) => identity is
    {
        FirmwareVersion: FirmwareVersion,
        SchemaVersion: SchemaVersion,
        MapVersion: RegisterMapVersion,
        UnitId: UnitId
    };
}
