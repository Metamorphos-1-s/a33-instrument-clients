using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace A33.Instrument.Core;

public sealed record PreflightSummary(
    int SchemaVersion, string WorkflowId, string ClientCommit, string ToolAssemblyVersion,
    string ToolSha256, string Stm32Commit, string BaselineId, string BaselineSha256,
    string BaselineManifestSha256, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    double DurationMs, double FreshnessWaitMs, uint InitialSampleSequence, uint FinalSampleSequence, string Endpoint, byte UnitId,
    DeviceIdentity Identity,
    PreflightConnectionStatistics Connections, PreflightRequestStatistics Requests,
    PreflightErrorCounters Errors, IReadOnlyDictionary<string, bool> Gates,
    string FinalStatus, string[] FailureReasons, IReadOnlyDictionary<string, string> EvidenceFileSha256);

public sealed record StrictPreflightEvidence(
    PreflightSummary Summary, IReadOnlyList<PreflightRequestTrace> RequestTrace,
    StrictPreflightReport? Report, PreflightEnvironmentEvidence Environment);

public sealed record ValidatedPreflightBinding(
    string WorkflowId, string SummaryPath, string SummarySha256, PreflightSummary Summary,
    ConfigStoreSnapshot ConfigStore, MailboxSnapshot Mailbox, ushort[] ActiveConfiguration);

public static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("Evidence path has no directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(fullPath)) throw new IOException("Existing evidence will not be overwritten.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static T Read<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException($"Evidence file is empty: {path}"); }
        catch (JsonException error) { throw new InvalidDataException($"Evidence file is damaged: {path}", error); }
    }
}

public static class PreflightEvidenceStore
{
    public static readonly string[] RequiredFiles =
    [
        "preflight-summary.json", "request-trace.json", "active-snapshot-1.json",
        "active-snapshot-2.json", "staging-snapshot.json", "mailbox-snapshot.json",
        "config-store-snapshot.json", "environment.json"
    ];

    public static async Task<PreflightSummary> WriteAsync(
        string directory, PreflightSummary summary, IReadOnlyList<PreflightRequestTrace> trace,
        StrictPreflightReport? report, PreflightEnvironmentEvidence environment,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, object?>
        {
            ["request-trace.json"] = trace,
            ["active-snapshot-1.json"] = report?.ActiveSnapshot1,
            ["active-snapshot-2.json"] = report?.ActiveSnapshot2,
            ["staging-snapshot.json"] = report is null ? null : new { registers = report.StagingSnapshot, differences_from_active = report.StagingDifferences },
            ["mailbox-snapshot.json"] = report?.Mailbox,
            ["config-store-snapshot.json"] = report?.ConfigStoreEvidence,
            ["environment.json"] = environment
        };
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            var path = Path.Combine(directory, item.Key);
            await AtomicJsonFile.WriteAsync(path, item.Value, cancellationToken);
            hashes[item.Key] = PersistenceBaselineContract.ComputeFileSha256(path);
        }
        var completedSummary = summary with { EvidenceFileSha256 = hashes };
        await AtomicJsonFile.WriteAsync(Path.Combine(directory, "preflight-summary.json"), completedSummary, cancellationToken);
        return completedSummary;
    }
}

public static class PreflightEvidenceValidator
{
    public static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromMinutes(15);

    public static ValidatedPreflightBinding Validate(
        string evidenceRoot, string workflowId, string currentClientCommit,
        TrustedPersistenceBaseline baseline, DateTimeOffset now, bool requireFresh = true,
        string? currentToolVersion = null, string? currentToolSha256 = null)
    {
        if (!Guid.TryParse(workflowId, out _)) throw new InvalidDataException("Preflight workflow ID must be a GUID.");
        if (!Directory.Exists(evidenceRoot)) throw new InvalidDataException("Preflight evidence root does not exist.");
        var directories = Directory.GetDirectories(evidenceRoot)
            .Where(path => Path.GetFileName(path).EndsWith("_" + workflowId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (directories.Length != 1) throw new InvalidDataException("Preflight evidence directory is missing or not unique.");
        var directory = directories[0];
        foreach (var file in PreflightEvidenceStore.RequiredFiles)
            if (!File.Exists(Path.Combine(directory, file))) throw new InvalidDataException($"Required preflight evidence is missing: {file}");
        var summaryPath = Path.Combine(directory, "preflight-summary.json");
        var summary = AtomicJsonFile.Read<PreflightSummary>(summaryPath);
        if (summary.SchemaVersion != 2 || summary.WorkflowId != workflowId || summary.FinalStatus != "PASS")
            throw new InvalidDataException("Preflight summary is not an authorized PASS.");
        if (summary.ClientCommit != currentClientCommit || summary.Stm32Commit != ConfigurationPersistenceService.FixedStm32Commit ||
            summary.BaselineId != baseline.Manifest.BaselineId || summary.BaselineSha256 != baseline.Manifest.ActiveArraySha256 ||
            summary.BaselineManifestSha256 != baseline.ManifestSha256 || summary.Endpoint != "192.168.1.100:502" || summary.UnitId != 1)
            throw new InvalidDataException("Preflight evidence identity or baseline binding mismatch.");
        if (summary.Identity is not { FirmwareVersion: 0x050A, SchemaVersion: 2, MapVersion: 0x0104, UnitId: 1 })
            throw new InvalidDataException("Preflight device identity does not match the fixed contract.");
        if ((currentToolVersion is not null && summary.ToolAssemblyVersion != currentToolVersion) ||
            (currentToolSha256 is not null && summary.ToolSha256 != currentToolSha256))
            throw new InvalidDataException("Preflight evidence was not produced by the current HardwareValidation tool binary.");
        if (summary.CompletedAtUtc > now || (requireFresh && now - summary.CompletedAtUtc > MaximumEvidenceAge))
            throw new InvalidDataException("Preflight evidence is expired or from the future.");
        if (summary.CompletedAtUtc < summary.StartedAtUtc || summary.DurationMs < 0 || summary.FreshnessWaitMs < 0 ||
            summary.InitialSampleSequence == summary.FinalSampleSequence)
            throw new InvalidDataException("Preflight timing or freshness evidence is invalid.");
        if (summary.Connections is not { ConnectionAttempts: 1, ConnectionSucceeded: 1, ConnectionFailed: 0, Disconnects: 1, AutomaticRetries: 0 } ||
            summary.Requests is not { Fc03Failed: 0, Fc06: 0, Fc16: 0, MailboxWrites: 0, Begin: 0, Validate: 0, Apply: 0, Cancel: 0, Save: 0, Reboots: 0 } ||
            summary.Errors is null || !summary.Errors.IsClean || summary.Gates is null ||
            RequiredGates.Any(name => !summary.Gates.TryGetValue(name, out var passed) || !passed))
            throw new InvalidDataException("Preflight evidence contains a failed safety counter or gate.");
        if (summary.EvidenceFileSha256 is null) throw new InvalidDataException("Preflight evidence hash set is missing.");
        foreach (var item in summary.EvidenceFileSha256)
        {
            var path = Path.Combine(directory, item.Key);
            if (!PreflightEvidenceStore.RequiredFiles.Contains(item.Key) || item.Key == "preflight-summary.json" ||
                !File.Exists(path) || PersistenceBaselineContract.ComputeFileSha256(path) != item.Value)
                throw new InvalidDataException($"Preflight evidence hash mismatch: {item.Key}");
        }
        if (summary.EvidenceFileSha256.Count != PreflightEvidenceStore.RequiredFiles.Length - 1)
            throw new InvalidDataException("Preflight evidence hash set is incomplete.");
        var environment = AtomicJsonFile.Read<PreflightEnvironmentEvidence>(Path.Combine(directory, "environment.json"));
        ValidateEnvironment(environment, summary, currentClientCommit);
        var trace = AtomicJsonFile.Read<PreflightRequestTrace[]>(Path.Combine(directory, "request-trace.json"));
        ValidateTraceShape(trace);
        var requests = PreflightRequestStatistics.FromTrace(trace);
        if (requests != summary.Requests || requests.Fc03Attempted != trace.Length || requests.Fc03Succeeded != trace.Count(x => x.Succeeded))
            throw new InvalidDataException("Preflight request trace and summary disagree.");
        var active = AtomicJsonFile.Read<ushort[]>(Path.Combine(directory, "active-snapshot-1.json"));
        var active2 = AtomicJsonFile.Read<ushort[]>(Path.Combine(directory, "active-snapshot-2.json"));
        if (!active.SequenceEqual(active2) || !active.SequenceEqual(baseline.Manifest.ActiveRegisters) ||
            PersistenceBaselineContract.ComputeActiveSha256(active) != baseline.Manifest.ActiveArraySha256)
            throw new InvalidDataException("Preflight Active configuration does not match the trusted baseline.");
        var storeEvidence = AtomicJsonFile.Read<ConfigStorePreflightEvidence>(Path.Combine(directory, "config-store-snapshot.json"));
        var store = storeEvidence.Parsed;
        var mailbox = AtomicJsonFile.Read<MailboxSnapshot>(Path.Combine(directory, "mailbox-snapshot.json"));
        if (!Enum.IsDefined(storeEvidence.WordOrder) || storeEvidence.DiagnosticsRaw?.Length != 7 || storeEvidence.StorageRaw?.Length != 5 ||
            ConfigStoreContract.Decode(storeEvidence.DiagnosticsRaw, storeEvidence.StorageRaw, storeEvidence.WordOrder) != store)
            throw new InvalidDataException("ConfigStore raw and parsed evidence disagree.");
        if (mailbox.Raw?.Length != 12 || mailbox.ResponseToken != mailbox.Raw[0] || mailbox.ResultCode != mailbox.Raw[1] ||
            mailbox.CommandState != mailbox.Raw[2] || mailbox.LastCommandId != mailbox.Raw[3])
            throw new InvalidDataException("Mailbox raw and parsed evidence disagree.");
        if (!store.StatesKnown || !store.StatesConsistent || store.State != ConfigStoreState.Idle || store.ConfigDirty ||
            store.CurrentRevision != store.SavedRevision || store.ActiveSlot is not (1 or 2) || mailbox.Busy || mailbox.Pending)
            throw new InvalidDataException("Preflight Mailbox or ConfigStore evidence is not a clean persistence baseline.");
        return new ValidatedPreflightBinding(workflowId, summaryPath,
            PersistenceBaselineContract.ComputeFileSha256(summaryPath), summary, store, mailbox, active);
    }

    private static readonly string[] RequiredGates =
    [
        "identity", "fresh_sample_sequence", "active_complete", "active_stable", "active_matches_baseline",
        "active_hash_matches_baseline", "brightness", "staging_complete", "mailbox_idle",
        "config_store_known_consistent_idle", "config_store_clean", "request_trace_consistent", "errors_clean", "read_only"
    ];

    private static void ValidateEnvironment(PreflightEnvironmentEvidence environment, PreflightSummary summary, string currentClientCommit)
    {
        if (environment is null || environment.SchemaVersion != 1 || environment.WorkflowId != summary.WorkflowId ||
            environment.StartedAtUtc.Offset != TimeSpan.Zero || environment.CompletedAtUtc.Offset != TimeSpan.Zero ||
            environment.StartedAtUtc != summary.StartedAtUtc || environment.CompletedAtUtc != summary.CompletedAtUtc ||
            environment.CompletedAtUtc < environment.StartedAtUtc ||
            environment.ClientCommit != summary.ClientCommit || environment.ClientCommit != currentClientCommit ||
            environment.ClientCommit.Length != 40 || environment.ClientCommit.Any(x => !Uri.IsHexDigit(x)) ||
            environment.ToolAssemblyVersion != summary.ToolAssemblyVersion || environment.ToolSha256 != summary.ToolSha256 ||
            environment.ToolSha256.Length != 64 || environment.ToolSha256.Any(x => !Uri.IsHexDigit(x)) ||
            string.IsNullOrWhiteSpace(environment.OsDescription) || string.IsNullOrWhiteSpace(environment.FrameworkDescription) ||
            string.IsNullOrWhiteSpace(environment.ProcessArchitecture) || string.IsNullOrWhiteSpace(environment.MachineName) ||
            string.IsNullOrWhiteSpace(environment.ToolAssemblyVersion) || environment.ToolAssemblyVersion == "unknown")
            throw new InvalidDataException("Preflight environment evidence does not match its summary, build, UTC run, or tool identity.");
        if (Math.Abs((environment.CompletedAtUtc - environment.StartedAtUtc).TotalMilliseconds - summary.DurationMs) > 1)
            throw new InvalidDataException("Preflight environment duration does not match its summary.");
    }

    private static void ValidateTraceShape(IReadOnlyList<PreflightRequestTrace> trace)
    {
        if (trace.Count == 0 || trace.Any(x => x is null || x.FunctionCode != 3 || !x.Succeeded || x.RegisterCount is 0 or > 16 ||
            x.ActualRegisterCount != x.RegisterCount || string.IsNullOrWhiteSpace(x.RequestHex) || string.IsNullOrWhiteSpace(x.ResponseHex)))
            throw new InvalidDataException("Preflight trace contains a failed, non-FC03, or oversized request.");
        foreach (var purpose in Enum.GetValues<PreflightReadPurpose>())
            if (!trace.Any(x => x.Purpose == purpose)) throw new InvalidDataException($"Preflight trace is missing {purpose} reads.");
        if (trace.Count(x => x.Purpose == PreflightReadPurpose.Active && x.RegisterCount == 16) != 8 ||
            trace.Count(x => x.Purpose == PreflightReadPurpose.Staging && x.RegisterCount == 16) != 4 ||
            !trace.Any(x => x.Purpose == PreflightReadPurpose.Mailbox && x.StartAddress == 0x004C && x.RegisterCount == 12) ||
            !trace.Any(x => x.Purpose == PreflightReadPurpose.ConfigStore && x.StartAddress == 0x0030 && x.RegisterCount == 7) ||
            !trace.Any(x => x.Purpose == PreflightReadPurpose.ConfigStore && x.StartAddress == 0x01C0 && x.RegisterCount == 5))
            throw new InvalidDataException("Preflight trace does not contain the fixed configuration and persistence read plan.");
    }
}

public static class PreflightEnvironment
{
    public static PreflightEnvironmentEvidence Capture(
        Assembly assembly, string workflowId, string clientCommit, string toolSha256,
        DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc) => new(
        1, workflowId, startedAtUtc, completedAtUtc, clientCommit,
        assembly.GetName().Version?.ToString() ?? "unknown", toolSha256,
        RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
        RuntimeInformation.ProcessArchitecture.ToString(), Environment.MachineName);
}
