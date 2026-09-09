namespace A33.Instrument.Core;

public static class Stage2BDeviceContract
{
    public const ushort FirmwareVersion = 0x050C;
    public const ushort SchemaVersion = 2;
    public const ushort RegisterMapVersion = 0x0104;
    public const byte UnitId = 1;
    public const string TcpHost = "192.168.1.100";
    public const int TcpPort = 502;
    public const string TcpEndpoint = "192.168.1.100:502";
    public const string Stm32ProductionCommit = "2af4abe39ddb3336d91be64fe8c75425c0dbc1aa";
    public const string Stm32EvidenceCommit = "de181c0b3feea020b224915b070dbb2cf5b6219f";
    public const string Stm32ReleaseElfSha256 = "895999B7547935827FC64DFF70EE5F4DF7B00E1FD413706E1BFDBEFAD5925E44";
    public const string SourceEvidenceCommit = "b853dc5aa75628a3c2ff32cab003a0b15c7ecd0a";
    public const string BaselineId = "a33-stage2b-fw050c-brightness3-20260909";
    public const string BaselineRoot = "Results/pc_stage2b_050c_baseline";
    public const string PersistenceEvidenceRoot = "Results/pc_stage2b_050c_hw";
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
