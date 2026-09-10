using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace A33.Instrument.Core;

public sealed record ExcludedBaselineEvidence(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record PersistenceBaselineManifest(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("baseline_id")] string BaselineId,
    [property: JsonPropertyName("source_evidence_file")] string SourceEvidenceFile,
    [property: JsonPropertyName("source_client_commit")] string SourceClientCommit,
    [property: JsonPropertyName("source_evidence_commit")] string SourceEvidenceCommit,
    [property: JsonPropertyName("stm32_commit")] string Stm32Commit,
    [property: JsonPropertyName("firmware_version")] ushort FirmwareVersion,
    [property: JsonPropertyName("device_schema")] ushort DeviceSchema,
    [property: JsonPropertyName("register_map")] ushort RegisterMap,
    [property: JsonPropertyName("unit_id")] byte UnitId,
    [property: JsonPropertyName("tcp_endpoint")] string TcpEndpoint,
    [property: JsonPropertyName("active_start_address")] ushort ActiveStartAddress,
    [property: JsonPropertyName("register_count")] ushort RegisterCount,
    [property: JsonPropertyName("read_method")] string ReadMethod,
    [property: JsonPropertyName("active_registers")] ushort[] ActiveRegisters,
    [property: JsonPropertyName("brightness_address")] ushort BrightnessAddress,
    [property: JsonPropertyName("original_brightness")] ushort OriginalBrightness,
    [property: JsonPropertyName("evidence_started_at_utc")] DateTimeOffset EvidenceStartedAtUtc,
    [property: JsonPropertyName("evidence_completed_at_utc")] DateTimeOffset EvidenceCompletedAtUtc,
    [property: JsonPropertyName("proven_before_first_stage2b_write")] bool ProvenBeforeFirstStage2BWrite,
    [property: JsonPropertyName("first_stage2b_write_evidence_file")] string FirstStage2BWriteEvidenceFile,
    [property: JsonPropertyName("first_stage2b_write_evidence_commit")] string FirstStage2BWriteEvidenceCommit,
    [property: JsonPropertyName("excluded_evidence")] ExcludedBaselineEvidence[] ExcludedEvidence,
    [property: JsonPropertyName("hash_algorithm")] string HashAlgorithm,
    [property: JsonPropertyName("canonicalization")] string Canonicalization,
    [property: JsonPropertyName("active_array_sha256")] string ActiveArraySha256,
    [property: JsonPropertyName("audit_note")] string AuditNote)
{
    [JsonPropertyName("capture_workflow_id")] public string CaptureWorkflowId { get; init; } = "";
    [JsonPropertyName("capture_tool_assembly_version")] public string CaptureToolAssemblyVersion { get; init; } = "";
    [JsonPropertyName("capture_tool_sha256")] public string CaptureToolSha256 { get; init; } = "";
    [JsonPropertyName("capture_fc03_attempted")] public int CaptureFc03Attempted { get; init; }
    [JsonPropertyName("capture_fc03_succeeded")] public int CaptureFc03Succeeded { get; init; }
    [JsonPropertyName("capture_fc03_failed")] public int CaptureFc03Failed { get; init; }
    [JsonPropertyName("capture_fc06")] public int CaptureFc06 { get; init; }
    [JsonPropertyName("capture_fc16")] public int CaptureFc16 { get; init; }
    [JsonPropertyName("capture_command_count")] public int CaptureCommandCount { get; init; }
    [JsonPropertyName("capture_automatic_retries")] public int CaptureAutomaticRetries { get; init; }
    [JsonPropertyName("capture_staging_register_count")] public int CaptureStagingRegisterCount { get; init; }
    [JsonPropertyName("capture_mailbox_idle")] public bool CaptureMailboxIdle { get; init; }
    [JsonPropertyName("capture_config_store_state")] public int CaptureConfigStoreState { get; init; }
    [JsonPropertyName("capture_config_store_clean")] public bool CaptureConfigStoreClean { get; init; }
    [JsonPropertyName("stm32_production_commit")] public string Stm32ProductionCommit { get; init; } = "";
    [JsonPropertyName("stm32_evidence_commit")] public string Stm32EvidenceCommit { get; init; } = "";
    [JsonPropertyName("stm32_release_elf_sha256")] public string Stm32ReleaseElfSha256 { get; init; } = "";
    [JsonPropertyName("stm32_binary_active_sha256")] public string Stm32BinaryActiveSha256 { get; init; } = "";
    [JsonPropertyName("battery_divider_top_ohm")] public uint BatteryDividerTopOhm { get; init; }
    [JsonPropertyName("battery_divider_bottom_ohm")] public uint BatteryDividerBottomOhm { get; init; }
    [JsonPropertyName("active_slot")] public ushort ActiveSlot { get; init; }
    [JsonPropertyName("active_sequence")] public uint ActiveSequence { get; init; }
    [JsonPropertyName("current_revision")] public uint CurrentRevision { get; init; }
    [JsonPropertyName("saved_revision")] public uint SavedRevision { get; init; }
    [JsonPropertyName("preconditioning_save_count")] public int PreconditioningSaveCount { get; init; }
    [JsonPropertyName("preconditioning_power_cycle_count")] public int PreconditioningPowerCycleCount { get; init; }
    [JsonPropertyName("stage2b_save_count_at_capture")] public int Stage2BSaveCountAtCapture { get; init; }
    [JsonPropertyName("stage2b_reboot_count_at_capture")] public int Stage2BRebootCountAtCapture { get; init; }
    [JsonPropertyName("source_evidence_file_sha256")] public Dictionary<string, string> SourceEvidenceFileSha256 { get; init; } = [];
}

public sealed record TrustedPersistenceBaseline(PersistenceBaselineManifest Manifest, string ManifestPath, string ManifestSha256);

public static class PersistenceBaselineContract
{
    public const string RelativeManifestPath = Stage2BDeviceContract.BaselineRoot + "/persistence_baseline_manifest.json";
    public const string BaselineId = Stage2BDeviceContract.BaselineId;
    public const string ActiveSha256 = Stage2BDeviceContract.PcJsonActiveSha256;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };

    public static string ComputeActiveSha256(IReadOnlyList<ushort> registers)
    {
        if (registers.Count != 64) throw new InvalidDataException("The persistence baseline must contain exactly 64 registers.");
        var canonical = JsonSerializer.Serialize(registers.ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string ComputeFileSha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static string ComputeBinaryActiveSha256(IReadOnlyList<ushort> registers)
    {
        if (registers.Count != 64) throw new InvalidDataException("The persistence baseline must contain exactly 64 registers.");
        var bytes = new byte[128];
        for (var i = 0; i < registers.Count; i++)
        {
            bytes[i * 2] = (byte)(registers[i] >> 8);
            bytes[i * 2 + 1] = (byte)registers[i];
        }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static TrustedPersistenceBaseline LoadFromRepository(string repositoryRoot)
    {
        var path = Path.GetFullPath(Path.Combine(repositoryRoot, RelativeManifestPath));
        PersistenceBaselineManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PersistenceBaselineManifest>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Baseline Manifest is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("Baseline Manifest is damaged.", error); }
        Validate(manifest);
        ValidateSourceEvidence(repositoryRoot, manifest);
        return new TrustedPersistenceBaseline(manifest, path, ComputeFileSha256(path));
    }

    public static void Validate(PersistenceBaselineManifest manifest)
    {
        if (manifest.SchemaVersion != 2 || manifest.BaselineId != BaselineId ||
            manifest.Stm32Commit != Stage2BDeviceContract.Stm32ProductionCommit ||
            manifest.Stm32ProductionCommit != Stage2BDeviceContract.Stm32ProductionCommit ||
            manifest.Stm32EvidenceCommit != Stage2BDeviceContract.Stm32EvidenceCommit ||
            manifest.Stm32ReleaseElfSha256 != Stage2BDeviceContract.Stm32ReleaseElfSha256 ||
            manifest.Stm32BinaryActiveSha256 != Stage2BDeviceContract.Stm32BinaryActiveSha256 ||
            manifest is not { FirmwareVersion: Stage2BDeviceContract.FirmwareVersion,
                DeviceSchema: Stage2BDeviceContract.SchemaVersion,
                RegisterMap: Stage2BDeviceContract.RegisterMapVersion, UnitId: Stage2BDeviceContract.UnitId,
                TcpEndpoint: Stage2BDeviceContract.TcpEndpoint, ActiveStartAddress: 0x0100, RegisterCount: 64,
                BrightnessAddress: 0x0116, OriginalBrightness: Stage2BDeviceContract.OriginalBrightness,
                ProvenBeforeFirstStage2BWrite: true, ActiveSlot: Stage2BDeviceContract.ExpectedBaselineSlot,
                ActiveSequence: Stage2BDeviceContract.ExpectedBaselineSequence,
                CurrentRevision: Stage2BDeviceContract.ExpectedBaselineRevision,
                SavedRevision: Stage2BDeviceContract.ExpectedBaselineRevision,
                PreconditioningSaveCount: Stage2BDeviceContract.PreconditioningSaveCount,
                PreconditioningPowerCycleCount: Stage2BDeviceContract.PreconditioningPowerCycleCount,
                Stage2BSaveCountAtCapture: 0,
                Stage2BRebootCountAtCapture: 0, BatteryDividerTopOhm: Stage2BDeviceContract.BatteryDividerTopOhm,
                BatteryDividerBottomOhm: Stage2BDeviceContract.BatteryDividerBottomOhm })
            throw new InvalidDataException("Baseline Manifest does not match the fixed Stage 2B contract.");
        if (!manifest.SourceEvidenceFile.EndsWith("/baseline-capture-summary.json", StringComparison.Ordinal) ||
            manifest.SourceClientCommit != manifest.SourceEvidenceCommit ||
            manifest.SourceEvidenceCommit != Stage2BDeviceContract.SourceEvidenceCommit ||
            !string.IsNullOrEmpty(manifest.FirstStage2BWriteEvidenceFile) ||
            !string.IsNullOrEmpty(manifest.FirstStage2BWriteEvidenceCommit) ||
            manifest.EvidenceCompletedAtUtc <= manifest.EvidenceStartedAtUtc)
            throw new InvalidDataException("Baseline source provenance does not match the audited pre-write evidence chain.");
        if (manifest.ActiveRegisters is null || string.IsNullOrWhiteSpace(manifest.ReadMethod) ||
            !manifest.ReadMethod.Contains("4x16", StringComparison.Ordinal) ||
            manifest.ExcludedEvidence is null || manifest.ExcludedEvidence.Length != 0 ||
            manifest.SourceEvidenceFileSha256 is null || manifest.SourceEvidenceFileSha256.Count != 8)
            throw new InvalidDataException("Baseline provenance is incomplete.");
        if (!Guid.TryParse(manifest.CaptureWorkflowId, out _) ||
            manifest.CaptureToolAssemblyVersion != "1.0.0.0" || manifest.CaptureToolSha256.Length != 64 ||
            manifest.CaptureFc03Attempted != 17 || manifest.CaptureFc03Succeeded != 17 ||
            manifest.CaptureFc03Failed != 0 || manifest.CaptureFc06 != 0 || manifest.CaptureFc16 != 0 ||
            manifest.CaptureCommandCount != 0 || manifest.CaptureAutomaticRetries != 0 ||
            manifest.CaptureStagingRegisterCount != 64 || !manifest.CaptureMailboxIdle ||
            manifest.CaptureConfigStoreState != 0 || !manifest.CaptureConfigStoreClean)
            throw new InvalidDataException("Baseline capture statistics or state binding is incomplete.");
        var computed = ComputeActiveSha256(manifest.ActiveRegisters);
        if (computed != ActiveSha256 || manifest.ActiveArraySha256 != ActiveSha256)
            throw new InvalidDataException("Baseline Active SHA-256 mismatch.");
        if (ComputeBinaryActiveSha256(manifest.ActiveRegisters) != Stage2BDeviceContract.Stm32BinaryActiveSha256)
            throw new InvalidDataException("Baseline STM32 binary-register SHA-256 mismatch.");
        if (manifest.ActiveRegisters[ConfigurationPersistenceService.BrightnessOffset] != 3)
            throw new InvalidDataException("Baseline brightness is not 3.");
    }

    private static void ValidateSourceEvidence(string repositoryRoot, PersistenceBaselineManifest manifest)
    {
        var source = Path.GetFullPath(Path.Combine(repositoryRoot, manifest.SourceEvidenceFile));
        using var document = JsonDocument.Parse(File.ReadAllText(source));
        var root = document.RootElement;
        var directory = Path.GetDirectoryName(source) ?? throw new InvalidDataException("Baseline evidence directory is missing.");
        foreach (var item in manifest.SourceEvidenceFileSha256)
        {
            var evidencePath = Path.Combine(directory, item.Key);
            if (!File.Exists(evidencePath) || ComputeFileSha256(evidencePath) != item.Value)
                throw new InvalidDataException($"Baseline source evidence hash mismatch: {item.Key}");
        }
        var first = AtomicJsonFile.Read<ushort[]>(Path.Combine(directory, "active-snapshot-1.json"));
        var second = AtomicJsonFile.Read<ushort[]>(Path.Combine(directory, "active-snapshot-2.json"));
        if (!first.SequenceEqual(manifest.ActiveRegisters) || !second.SequenceEqual(manifest.ActiveRegisters) ||
            root.GetProperty("Identity").GetProperty("FirmwareVersion").GetUInt16() != manifest.FirmwareVersion ||
            root.GetProperty("Identity").GetProperty("MapVersion").GetUInt16() != manifest.RegisterMap ||
            root.GetProperty("UnitId").GetByte() != manifest.UnitId ||
            root.GetProperty("ClientCommit").GetString() != manifest.SourceClientCommit ||
            root.GetProperty("FinalStatus").GetString() != "PASS" ||
            root.GetProperty("PcJsonActiveSha256").GetString() != ActiveSha256 ||
            root.GetProperty("Stm32BinaryActiveSha256").GetString() != Stage2BDeviceContract.Stm32BinaryActiveSha256)
            throw new InvalidDataException("Baseline Manifest does not match its preserved source evidence.");
        var trace = AtomicJsonFile.Read<PreflightRequestTrace[]>(Path.Combine(directory, "request-trace.json"));
        if (trace.Length != 17 || trace.Any(x => x.FunctionCode != 3 || !x.Succeeded || x.RegisterCount is 0 or > 16))
            throw new InvalidDataException("Baseline capture trace is not the fixed successful FC03-only plan.");
        var summary = root;
        if (summary.GetProperty("WorkflowId").GetString() != manifest.CaptureWorkflowId ||
            summary.GetProperty("ClientCommit").GetString() != manifest.SourceEvidenceCommit ||
            summary.GetProperty("ToolAssemblyVersion").GetString() != manifest.CaptureToolAssemblyVersion ||
            summary.GetProperty("ToolSha256").GetString() != manifest.CaptureToolSha256 ||
            summary.GetProperty("Requests").GetProperty("Fc03Attempted").GetInt32() != manifest.CaptureFc03Attempted ||
            summary.GetProperty("Requests").GetProperty("Fc03Succeeded").GetInt32() != manifest.CaptureFc03Succeeded ||
            summary.GetProperty("Requests").GetProperty("Fc06").GetInt32() != manifest.CaptureFc06 ||
            summary.GetProperty("Requests").GetProperty("Fc16").GetInt32() != manifest.CaptureFc16 ||
            summary.GetProperty("ConfigStore").GetProperty("State").GetInt32() != manifest.CaptureConfigStoreState ||
            summary.GetProperty("ConfigStore").GetProperty("ConfigDirty").GetBoolean() == manifest.CaptureConfigStoreClean ||
            summary.GetProperty("FinalStatus").GetString() != "PASS")
            throw new InvalidDataException("Baseline capture summary does not match Manifest bindings.");
    }
}
