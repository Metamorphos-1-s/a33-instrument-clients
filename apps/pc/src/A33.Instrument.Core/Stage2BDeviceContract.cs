namespace A33.Instrument.Core;

public static class Stage2BDeviceContract
{
    public const ushort FirmwareVersion = 0x050F;
    public const ushort SchemaVersion = 2;
    public const ushort RegisterMapVersion = 0x0104;
    public const byte UnitId = 1;
    public const string TcpHost = "192.168.1.100";
    public const int TcpPort = 502;
    public const string TcpEndpoint = "192.168.1.100:502";
    public const string Stm32ProductionCommit = "b119703cee70b228aa240e7f3477c7dca9946841";
    public const string Stm32EvidenceCommit = "5236da68341e8feed0c6f6aedbc5ee52cea91f3b";
    public const string Stm32ReleaseElfSha256 = "15C8269A80962E2CA7329A2623AB69F2286C2E3336373D0B658E2755E2B8DE8D";
    public const string SourceEvidenceCommit = "6e2a8387c28ded9e5281751aaedb43a316d9ebdc";
    public const string BaselineId = "a33-stage2b-fw050f-brightness3-20260911";
    public const string BaselineRoot = "Results/pc_stage2b_050f_baseline";
    public const string PersistenceEvidenceRoot = "Results/pc_stage2b_050f_hw";
    public const uint BatteryDividerTopOhm = 47000;
    public const uint BatteryDividerBottomOhm = 10000;
    public const ushort OriginalBrightness = 3;
    public const ushort TestBrightness = 4;
    public const ushort ExpectedBaselineSlot = 1;
    public const uint ExpectedBaselineSequence = 25;
    public const uint ExpectedBaselineRevision = 25;
    public const int PreconditioningSaveCount = 2;
    public const int PreconditioningPowerCycleCount = 1;
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
