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
    [property: JsonPropertyName("audit_note")] string AuditNote);

public sealed record TrustedPersistenceBaseline(PersistenceBaselineManifest Manifest, string ManifestPath, string ManifestSha256);

public static class PersistenceBaselineContract
{
    public const string RelativeManifestPath = "Results/pc_stage2b_hw/persistence_baseline_manifest.json";
    public const string BaselineId = "a33-stage2b-prewrite-brightness3-20260906";
    public const string ActiveSha256 = "8C2E5BA6BF39436E5DF2956DE7E09A058DDA330C6462073483E4E70CAD1CACEE";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };

    public static string ComputeActiveSha256(IReadOnlyList<ushort> registers)
    {
        if (registers.Count != 64) throw new InvalidDataException("The persistence baseline must contain exactly 64 registers.");
        var canonical = JsonSerializer.Serialize(registers.ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string ComputeFileSha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

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
        if (manifest.SchemaVersion != 1 || manifest.BaselineId != BaselineId ||
            manifest.Stm32Commit != "71a61249645bff6249286ac801d7f468786cfe85" ||
            manifest is not { FirmwareVersion: 0x050A, DeviceSchema: 2, RegisterMap: 0x0104, UnitId: 1,
                TcpEndpoint: "192.168.1.100:502", ActiveStartAddress: 0x0100, RegisterCount: 64,
                BrightnessAddress: 0x0116, OriginalBrightness: 3, ProvenBeforeFirstStage2BWrite: true })
            throw new InvalidDataException("Baseline Manifest does not match the fixed Stage 2B contract.");
        if (manifest.SourceEvidenceFile != "Results/pc_stage2b_hw/tcp_strict_preflight.json" ||
            manifest.SourceClientCommit != "0fb02a7996d7cdb11f8d4b1074c1e3fa1df6dacf" ||
            manifest.SourceEvidenceCommit != "d17eac246e282b8c05dc8cd85407ced1d3a24edf" ||
            manifest.FirstStage2BWriteEvidenceFile != "Results/pc_stage2b_hw/tcp_cancel_transaction_trace.json" ||
            manifest.FirstStage2BWriteEvidenceCommit != "c1c330a797137264b5f050e67901f09d2518b4c2" ||
            manifest.EvidenceCompletedAtUtc <= manifest.EvidenceStartedAtUtc)
            throw new InvalidDataException("Baseline source provenance does not match the audited pre-write evidence chain.");
        if (manifest.ActiveRegisters is null || string.IsNullOrWhiteSpace(manifest.ReadMethod) || !manifest.ReadMethod.Contains("4x16", StringComparison.Ordinal) ||
            manifest.ExcludedEvidence is null || manifest.ExcludedEvidence.Length != 2 ||
            manifest.ExcludedEvidence.Any(x => x is null || x.Classification != "PRESERVED_NON_AUTHORITATIVE_EVIDENCE") ||
            !manifest.ExcludedEvidence.Select(x => x.File).Order().SequenceEqual(new[]
            {
                "Results/pc_stage2b_hw/tcp_active_config_raw_1.json",
                "Results/pc_stage2b_hw/tcp_active_config_raw_2.json"
            }))
            throw new InvalidDataException("Baseline provenance is incomplete.");
        var computed = ComputeActiveSha256(manifest.ActiveRegisters);
        if (computed != ActiveSha256 || manifest.ActiveArraySha256 != ActiveSha256)
            throw new InvalidDataException("Baseline Active SHA-256 mismatch.");
        if (manifest.ActiveRegisters[ConfigurationPersistenceService.BrightnessOffset] != 3)
            throw new InvalidDataException("Baseline brightness is not 3.");
    }

    private static void ValidateSourceEvidence(string repositoryRoot, PersistenceBaselineManifest manifest)
    {
        var source = Path.GetFullPath(Path.Combine(repositoryRoot, manifest.SourceEvidenceFile));
        using var document = JsonDocument.Parse(File.ReadAllText(source));
        var root = document.RootElement;
        var first = root.GetProperty("active_snapshot_1").EnumerateArray().Select(x => x.GetUInt16()).ToArray();
        var second = root.GetProperty("active_snapshot_2").EnumerateArray().Select(x => x.GetUInt16()).ToArray();
        if (!first.SequenceEqual(manifest.ActiveRegisters) || !second.SequenceEqual(manifest.ActiveRegisters) ||
            root.GetProperty("firmware_version").GetUInt16() != manifest.FirmwareVersion ||
            root.GetProperty("map_version").GetUInt16() != manifest.RegisterMap ||
            root.GetProperty("unit_id").GetByte() != manifest.UnitId ||
            !root.GetProperty("client_commit").GetString()!.StartsWith(manifest.SourceClientCommit[..7], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Baseline Manifest does not match its preserved source evidence.");
    }
}
