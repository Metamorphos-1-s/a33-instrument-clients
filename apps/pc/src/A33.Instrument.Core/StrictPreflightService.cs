using A33.Instrument.Protocol;

namespace A33.Instrument.Core;

public enum PreflightReadPurpose { Identity, Freshness, Realtime, Active, Staging, Mailbox, ConfigStore }

public sealed record PreflightRequestTrace(
    int Sequence, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, double DurationMs,
    byte FunctionCode, ushort StartAddress, ushort RegisterCount, string RequestHex, string ResponseHex,
    int ActualRegisterCount, bool Succeeded, string? ExceptionCategory, byte? ModbusExceptionCode,
    PreflightReadPurpose Purpose, ushort? MailboxCommandId);

public sealed record PreflightErrorCounters(
    long Timeouts, long CrcErrors, long MbapErrors, long TidErrors, long UnitErrors,
    long ModbusExceptions, long BadFrames, long TransportErrors)
{
    public bool IsClean => Timeouts == 0 && CrcErrors == 0 && MbapErrors == 0 && TidErrors == 0 &&
        UnitErrors == 0 && ModbusExceptions == 0 && BadFrames == 0 && TransportErrors == 0;
}

public sealed record PreflightConnectionStatistics(int ConnectionAttempts, int ConnectionSucceeded, int ConnectionFailed, int Disconnects, int AutomaticRetries);

public sealed record PreflightRequestStatistics(
    int Fc03Attempted, int Fc03Succeeded, int Fc03Failed, int Fc06, int Fc16,
    int MailboxWrites, int Begin, int Validate, int Apply, int Cancel, int Save, int Reboots)
{
    public static PreflightRequestStatistics FromTrace(IReadOnlyList<PreflightRequestTrace> trace) => new(
        trace.Count(x => x.FunctionCode == 3), trace.Count(x => x.FunctionCode == 3 && x.Succeeded),
        trace.Count(x => x.FunctionCode == 3 && !x.Succeeded), trace.Count(x => x.FunctionCode == 6),
        trace.Count(x => x.FunctionCode == 16),
        trace.Count(x => (x.FunctionCode is 6 or 16) && x.StartAddress == 0x0040),
        trace.Count(x => x.MailboxCommandId == 9), trace.Count(x => x.MailboxCommandId == 10),
        trace.Count(x => x.MailboxCommandId == 11), trace.Count(x => x.MailboxCommandId == 12),
        trace.Count(x => x.MailboxCommandId == 13), 0);
}

public sealed record PreflightTimingEvidence(
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, double DurationMs,
    DateTimeOffset FreshnessStartedAtUtc, DateTimeOffset FreshnessCompletedAtUtc,
    double FreshnessWaitMs, uint InitialSampleSequence, uint FinalSampleSequence);

public sealed record PreflightEnvironmentEvidence(
    int SchemaVersion, string WorkflowId, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    string ClientCommit, string ToolAssemblyVersion, string ToolSha256,
    string OsDescription, string FrameworkDescription, string ProcessArchitecture, string MachineName);
public sealed record ConfigurationRegisterDifference(ushort Address, ushort ActiveValue, ushort StagingValue);
public sealed record ConfigStorePreflightEvidence(WordOrder WordOrder, ushort[] DiagnosticsRaw, ushort[] StorageRaw, ConfigStoreSnapshot Parsed);

public interface ITrackedReadOnlyRegisterAccess : IReadOnlyRegisterAccess
{
    new WordOrder WordOrder { get; set; }
    byte UnitId { get; }
    IReadOnlyList<PreflightRequestTrace> Trace { get; }
    PreflightErrorCounters Errors { get; }
    Task<ushort[]> ReadForPreflightAsync(ushort address, ushort count, PreflightReadPurpose purpose, CancellationToken cancellationToken = default);
}

public sealed record StrictPreflightReport(
    bool Passed, string[] FailureReasons, DeviceIdentity Identity, PreflightTimingEvidence Timing,
    ushort[] RealtimeSnapshot, ushort[] ActiveSnapshot1, ushort[] ActiveSnapshot2, ushort[] StagingSnapshot,
    string ActiveSnapshot1Sha256, string ActiveSnapshot2Sha256, ConfigurationRegisterDifference[] StagingDifferences,
    MailboxSnapshot Mailbox, ConfigStorePreflightEvidence ConfigStoreEvidence, string BaselineId, string BaselineSha256,
    string BaselineManifestSha256, PreflightRequestStatistics Requests, PreflightErrorCounters Errors,
    IReadOnlyDictionary<string, bool> Gates);

public sealed record FreshSnapshotPolicy(TimeSpan PollInterval, TimeSpan Timeout)
{
    public static FreshSnapshotPolicy Default { get; } = new(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(3));
}

public sealed class StrictPreflightService(
    ITrackedReadOnlyRegisterAccess registers, TrustedPersistenceBaseline baseline,
    IPersistenceClock? clock = null, FreshSnapshotPolicy? freshnessPolicy = null)
{
    private readonly IPersistenceClock clock = clock ?? new SystemPersistenceClock();
    private readonly FreshSnapshotPolicy freshness = freshnessPolicy ?? FreshSnapshotPolicy.Default;

    public async Task<StrictPreflightReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;
        var orderValue = (await ReadAsync(0x0103, 1, PreflightReadPurpose.Identity, cancellationToken))[0];
        registers.WordOrder = orderValue switch { 0 => WordOrder.HighWordFirst, 1 => WordOrder.LowWordFirst, _ => throw new InvalidDataException("Unknown Modbus word order.") };
        var identityWords = await ReadAsync(14, 2, PreflightReadPurpose.Identity, cancellationToken);
        var (realtime, timing) = await ReadFreshRealtimeAsync(started, cancellationToken);
        var active1 = await ReadConfigurationAsync(0x0100, PreflightReadPurpose.Active, cancellationToken);
        var staging = await ReadConfigurationAsync(0x0140, PreflightReadPurpose.Staging, cancellationToken);
        var mailboxRaw = await ReadAsync(ConfigStoreContract.MailboxResponseAddress, ConfigStoreContract.MailboxResponseLength, PreflightReadPurpose.Mailbox, cancellationToken);
        var mailbox = new MailboxSnapshot(mailboxRaw[0], mailboxRaw[1], mailboxRaw[2], mailboxRaw[3], mailboxRaw);
        var diagnostics = await ReadAsync(ConfigStoreContract.StateMirror1Address, 7, PreflightReadPurpose.ConfigStore, cancellationToken);
        var storage = await ReadAsync(ConfigStoreContract.StorageSchemaAddress, 5, PreflightReadPurpose.ConfigStore, cancellationToken);
        var store = ConfigStoreContract.Decode(diagnostics, storage, registers.WordOrder);
        var storeEvidence = new ConfigStorePreflightEvidence(registers.WordOrder, diagnostics, storage, store);
        var active2 = await ReadConfigurationAsync(0x0100, PreflightReadPurpose.Active, cancellationToken);
        var identity = new DeviceIdentity(identityWords[1], store.SchemaVersion, identityWords[0], registers.UnitId);
        var activeHash1 = PersistenceBaselineContract.ComputeActiveSha256(active1);
        var activeHash2 = PersistenceBaselineContract.ComputeActiveSha256(active2);
        var differences = active1.Select((value, index) => new { value, index }).Where(x => staging[x.index] != x.value)
            .Select(x => new ConfigurationRegisterDifference((ushort)(0x0100 + x.index), x.value, staging[x.index])).ToArray();
        var requests = PreflightRequestStatistics.FromTrace(registers.Trace);
        var gates = new Dictionary<string, bool>
        {
            ["identity"] = Stage2BDeviceContract.Matches(identity),
            ["fresh_sample_sequence"] = timing.FinalSampleSequence != timing.InitialSampleSequence,
            ["active_complete"] = active1.Length == 64 && active2.Length == 64,
            ["active_stable"] = active1.SequenceEqual(active2),
            ["active_matches_baseline"] = active1.SequenceEqual(baseline.Manifest.ActiveRegisters) && active2.SequenceEqual(baseline.Manifest.ActiveRegisters),
            ["active_hash_matches_baseline"] = activeHash1 == baseline.Manifest.ActiveArraySha256 && activeHash2 == baseline.Manifest.ActiveArraySha256,
            ["brightness"] = active1[ConfigurationPersistenceService.BrightnessOffset] == ConfigurationPersistenceService.OriginalBrightness,
            ["staging_complete"] = staging.Length == 64,
            ["mailbox_idle"] = !mailbox.Busy && !mailbox.Pending,
            ["config_store_known_consistent_idle"] = store.StatesKnown && store.StatesConsistent && store.State == ConfigStoreState.Idle,
            ["config_store_clean"] = !store.ConfigDirty && store.CurrentRevision == store.SavedRevision &&
                store.CurrentRevision == baseline.Manifest.CurrentRevision &&
                store.ActiveSlot == baseline.Manifest.ActiveSlot && store.ActiveSequence == baseline.Manifest.ActiveSequence,
            ["request_trace_consistent"] = requests.Fc03Attempted == registers.Trace.Count(x => x.FunctionCode == 3) &&
                requests.Fc03Succeeded == registers.Trace.Count(x => x.FunctionCode == 3 && x.Succeeded),
            ["errors_clean"] = registers.Errors.IsClean,
            ["read_only"] = requests is { Fc06: 0, Fc16: 0, MailboxWrites: 0, Begin: 0, Validate: 0, Apply: 0, Cancel: 0, Save: 0, Reboots: 0 }
        };
        var failures = gates.Where(x => !x.Value).Select(x => $"Gate failed: {x.Key}.").ToArray();
        var completed = clock.UtcNow;
        return new StrictPreflightReport(failures.Length == 0, failures, identity,
            timing with { CompletedAtUtc = completed, DurationMs = (completed - started).TotalMilliseconds },
            realtime, active1, active2, staging, activeHash1, activeHash2, differences, mailbox, storeEvidence,
            baseline.Manifest.BaselineId, baseline.Manifest.ActiveArraySha256, baseline.ManifestSha256,
            requests, registers.Errors, gates);
    }

    private async Task<(ushort[] Snapshot, PreflightTimingEvidence Timing)> ReadFreshRealtimeAsync(DateTimeOffset overallStarted, CancellationToken cancellationToken)
    {
        var freshnessStarted = clock.UtcNow;
        var initial = RegisterValueCodec.UInt32(await ReadAsync(0x0020, 2, PreflightReadPurpose.Freshness, cancellationToken), registers.WordOrder);
        var deadline = freshnessStarted + freshness.Timeout;
        do
        {
            var sequenceBefore = RegisterValueCodec.UInt32(await ReadAsync(0x0020, 2, PreflightReadPurpose.Freshness, cancellationToken), registers.WordOrder);
            var snapshot = new ushort[34];
            (await ReadAsync(0x0000, 16, PreflightReadPurpose.Realtime, cancellationToken)).CopyTo(snapshot, 0);
            (await ReadAsync(0x0010, 16, PreflightReadPurpose.Realtime, cancellationToken)).CopyTo(snapshot, 16);
            var finalWords = await ReadAsync(0x0020, 2, PreflightReadPurpose.Realtime, cancellationToken);
            finalWords.CopyTo(snapshot, 32);
            var final = RegisterValueCodec.UInt32(finalWords, registers.WordOrder);
            if (final != initial && final == sequenceBefore)
            {
                var completed = clock.UtcNow;
                return (snapshot, new PreflightTimingEvidence(overallStarted, completed, (completed - overallStarted).TotalMilliseconds,
                    freshnessStarted, completed, (completed - freshnessStarted).TotalMilliseconds, initial, final));
            }
            if (clock.UtcNow >= deadline) break;
            await clock.DelayAsync(freshness.PollInterval, cancellationToken);
        } while (clock.UtcNow <= deadline);
        throw new TimeoutException("Fresh realtime sample sequence did not advance to a stable planned snapshot.");
    }

    private async Task<ushort[]> ReadConfigurationAsync(ushort start, PreflightReadPurpose purpose, CancellationToken cancellationToken)
    {
        var result = new ushort[64];
        for (var i = 0; i < 4; i++) (await ReadAsync((ushort)(start + i * 16), 16, purpose, cancellationToken)).CopyTo(result, i * 16);
        return result;
    }

    private Task<ushort[]> ReadAsync(ushort address, ushort count, PreflightReadPurpose purpose, CancellationToken cancellationToken)
    {
        if (count is 0 or > 16) throw new InvalidOperationException("Strict preflight FC03 reads are limited to 1-16 registers.");
        return registers.ReadForPreflightAsync(address, count, purpose, cancellationToken);
    }
}
