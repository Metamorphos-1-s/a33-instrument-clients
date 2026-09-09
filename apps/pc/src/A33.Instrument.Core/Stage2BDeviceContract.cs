namespace A33.Instrument.Core;

public static class Stage2BDeviceContract
{
    public const ushort FirmwareVersion = 0x050B;
    public const ushort SchemaVersion = 2;
    public const ushort RegisterMapVersion = 0x0104;
    public const byte UnitId = 1;
    public const string TcpHost = "192.168.1.100";
    public const int TcpPort = 502;
    public const string TcpEndpoint = "192.168.1.100:502";
    public const string Stm32ProductionCommit = "a3cc744a6cae85d670008fe6a1bf96eae63bd2a7";
    public const string Stm32EvidenceCommit = "34cd363d30d6b0f271f6f9b9ba73c8bd91262dc6";
    public const string Stm32ReleaseElfSha256 = "F6607DE318CE03925C27D8F4B8AA98020F3FC016EF8221229BBEEA8BE16C6FAC";
    public const uint BatteryDividerTopOhm = 47000;
    public const uint BatteryDividerBottomOhm = 10000;
    public const ushort OriginalBrightness = 3;
    public const ushort TestBrightness = 4;
    public const ushort ExpectedBaselineSlot = 1;
    public const uint ExpectedBaselineSequence = 19;
    public const string PcJsonActiveSha256 = "B7D78D5BD4A6DE0BE2C0DA201C167A0178608FCFF49F6297664C87C79018EE73";
    public const string Stm32BinaryActiveSha256 = "4BA7DA269DECB38D631ED8076A4FE90B7EF70AA04123CF15D5662B7DBBD4DD98";

    public static bool Matches(DeviceIdentity identity) => identity is
    {
        FirmwareVersion: FirmwareVersion,
        SchemaVersion: SchemaVersion,
        MapVersion: RegisterMapVersion,
        UnitId: UnitId
    };
}
