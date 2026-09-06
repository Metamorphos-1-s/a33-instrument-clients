using System.Text.Json;

namespace A33.Instrument.Core;

public enum PersistencePhase
{
    Initial, PreflightComplete, TestValueWriteAuthorized, TestValueAppliedRam, TestValueSaveRequested, SavingTestValue,
    TestValueSaveConfirmed, WaitingForFirstReboot, TestValuePersistenceVerified,
    OriginalValueWriteAuthorized, OriginalValueAppliedRam, OriginalValueSaveRequested, SavingOriginalValue,
    OriginalValueSaveConfirmed, WaitingForSecondReboot, FinalRestorationVerified,
    Complete, ResultUncertain, Failed
}

public enum PersistenceCycle { A, B, Complete }
public enum PersistenceAuthorizationStage { BoundPreflight, CycleAWrites, WaitingForFirstReboot, CycleBWrites, WaitingForSecondReboot, Complete, Locked }
public enum PersistenceEventKind { Information, AppliedRam, SaveReserved, SaveSent, SaveConfirmed, RebootRequired, RebootObserved, Verified, ResultUncertain, Failed }
public sealed record PersistenceEvent(DateTimeOffset At, PersistenceEventKind Kind, string Message);
public sealed record ConfigStoreObservation(DateTimeOffset CapturedAtUtc, PersistenceCycle Cycle, ConfigStoreSnapshot Snapshot);
public sealed record PersistenceRebootEvidence(DateTimeOffset CapturedAtUtc, DeviceIdentity Identity, MailboxSnapshot Mailbox, ConfigStoreSnapshot ConfigStore, ushort[] ActiveConfiguration, string ActiveSha256);
public sealed record PersistenceCycleEvidence
{
    public required PersistenceCycle Cycle { get; init; }
    public required ushort[] ExpectedActiveConfiguration { get; init; }
    public ushort[]? ActiveBeforeApply { get; init; }
    public ushort[]? ActiveAfterApply { get; init; }
    public ConfigStoreSnapshot? SaveBefore { get; init; }
    public ConfigStoreSnapshot? SaveConfirmed { get; init; }
    public ConfigStoreObservation[] PollSnapshots { get; init; } = [];
    public ushort[] MailboxTokens { get; init; } = [];
    public ushort? SaveToken { get; init; }
    public DateTimeOffset? SaveReservedAtUtc { get; init; }
    public bool SaveRequestMayHaveBeenSent { get; init; }
    public PersistenceRebootEvidence? RebootEvidence { get; init; }
}

public sealed record PersistenceJournal
{
    public const int CurrentSchemaVersion = 3;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string WorkflowId { get; init; }
    public required string ClientCommit { get; init; }
    public required string Stm32Commit { get; init; }
    public required string BaselineId { get; init; }
    public required string BaselineSha256 { get; init; }
    public required string BaselineManifestSha256 { get; init; }
    public required string BoundPreflightWorkflowId { get; init; }
    public required string PreflightSummarySha256 { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public PersistencePhase Phase { get; init; }
    public PersistenceCycle Cycle { get; init; }
    public PersistenceAuthorizationStage AuthorizationStage { get; init; }
    public required ushort[] OriginalActiveConfiguration { get; init; }
    public required ushort[] ExpectedActiveConfiguration { get; init; }
    public int OriginalBrightness { get; init; } = ConfigurationPersistenceService.OriginalBrightness;
    public int TestBrightness { get; init; } = ConfigurationPersistenceService.TestBrightness;
    public int SaveBudget { get; init; } = ConfigurationPersistenceService.FixedSaveBudget;
    public int ReservedSaveCount { get; init; }
    public required ushort[] SaveTokens { get; init; }
    public ushort? LastMailboxToken { get; init; }
    public ushort? MailboxTokenBeforeFirstReboot { get; init; }
    public ushort? MailboxTokenAfterFirstReboot { get; init; }
    public ushort? MailboxTokenBeforeSecondReboot { get; init; }
    public ushort? MailboxTokenAfterSecondReboot { get; init; }
    public ConfigStoreSnapshot? SaveBefore { get; init; }
    public required ConfigStoreSnapshot[] PollSnapshots { get; init; }
    public ConfigStoreSnapshot? SaveConfirmed { get; init; }
    public bool WaitingForFirstReboot { get; init; }
    public bool WaitingForSecondReboot { get; init; }
    public string? ApplyResult { get; init; }
    public string? SaveResult { get; init; }
    public string? VerificationResult { get; init; }
    public string? ResultUncertainReason { get; init; }
    public bool? FinalConfigurationMatches64Of64 { get; init; }
    public required PersistenceEvent[] Events { get; init; }
    public required PersistenceCycleEvidence CycleAEvidence { get; init; }
    public PersistenceCycleEvidence? CycleBEvidence { get; init; }
    public string ValidationSummary { get; init; } = "schema-3 logical invariants validated";
}

public static class PersistenceJournalStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task WriteAtomicAsync(string path, PersistenceJournal journal, CancellationToken cancellationToken = default)
    {
        Validate(journal, path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("Journal path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, journal, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task<PersistenceJournal> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var journal = await JsonSerializer.DeserializeAsync<PersistenceJournal>(stream, Options, cancellationToken)
                ?? throw new InvalidDataException("Persistence journal is empty.");
            Validate(journal, path);
            return journal;
        }
        catch (JsonException error) { throw new InvalidDataException("Persistence journal is damaged.", error); }
    }

    public static void Validate(PersistenceJournal journal, string? path = null)
    {
        if (journal.SchemaVersion != PersistenceJournal.CurrentSchemaVersion || !Enum.IsDefined(journal.Phase) ||
            !Enum.IsDefined(journal.Cycle) || !Enum.IsDefined(journal.AuthorizationStage)) throw new InvalidDataException("Unsupported persistence journal schema or enum value.");
        if (!Guid.TryParse(journal.WorkflowId, out _) || !Guid.TryParse(journal.BoundPreflightWorkflowId, out _))
            throw new InvalidDataException("Persistence or preflight workflow ID is invalid.");
        if (journal.Stm32Commit != ConfigurationPersistenceService.FixedStm32Commit || journal.OriginalBrightness != 3 || journal.TestBrightness != 4 ||
            string.IsNullOrWhiteSpace(journal.BaselineId) || !IsSha256(journal.BaselineSha256) || !IsSha256(journal.BaselineManifestSha256) ||
            !IsSha256(journal.PreflightSummarySha256) || journal.ClientCommit.Length != 40 || journal.ClientCommit.Any(x => !Uri.IsHexDigit(x)))
            throw new InvalidDataException("Persistence journal fixed contract or evidence binding is invalid.");
        if (journal.SaveBudget != ConfigurationPersistenceService.FixedSaveBudget || journal.ReservedSaveCount is < 0 or > ConfigurationPersistenceService.FixedSaveBudget)
            throw new InvalidDataException("Persistence journal SAVE budget is invalid.");
        if (journal.OriginalActiveConfiguration is null || journal.ExpectedActiveConfiguration is null || journal.OriginalActiveConfiguration.Length != 64 || journal.ExpectedActiveConfiguration.Length != 64 || journal.Events is null || journal.SaveTokens is null || journal.PollSnapshots is null)
            throw new InvalidDataException("Persistence journal configuration snapshots must contain 64 registers.");
        if (journal.SaveTokens.Any(x => x == 0) || journal.SaveTokens.Distinct().Count() != journal.SaveTokens.Length || journal.SaveTokens.Length != journal.ReservedSaveCount)
            throw new InvalidDataException("Persistence journal SAVE token history is inconsistent.");
        if (journal.CycleAEvidence is null || journal.CycleAEvidence.Cycle != PersistenceCycle.A || journal.CycleAEvidence.ExpectedActiveConfiguration?.Length != 64 ||
            journal.CycleAEvidence.PollSnapshots is null || journal.CycleAEvidence.MailboxTokens is null)
            throw new InvalidDataException("Cycle A evidence is missing or invalid.");
        if (journal.CycleBEvidence is not null && (journal.CycleBEvidence.Cycle != PersistenceCycle.B || journal.CycleBEvidence.ExpectedActiveConfiguration?.Length != 64 ||
            journal.CycleBEvidence.PollSnapshots is null || journal.CycleBEvidence.MailboxTokens is null || journal.CycleAEvidence.RebootEvidence is null))
            throw new InvalidDataException("Cycle B cannot exist before verified cycle A reboot evidence.");
        if (journal.Cycle == PersistenceCycle.A && journal.CycleBEvidence is not null)
            throw new InvalidDataException("Cycle B evidence conflicts with the current cycle.");
        if (journal.Cycle is PersistenceCycle.B or PersistenceCycle.Complete && !ValidReboot(journal.CycleAEvidence.RebootEvidence))
            throw new InvalidDataException("Cycle B requires the first reboot evidence.");
        if (journal.WaitingForFirstReboot != (journal.Phase == PersistencePhase.WaitingForFirstReboot) ||
            journal.WaitingForSecondReboot != (journal.Phase == PersistencePhase.WaitingForSecondReboot))
            throw new InvalidDataException("Journal reboot waiting flags contradict the phase.");
        var expectedReservations = journal.Phase switch
        {
            PersistencePhase.Initial or PersistencePhase.PreflightComplete or PersistencePhase.TestValueWriteAuthorized or PersistencePhase.TestValueAppliedRam => 0,
            PersistencePhase.TestValueSaveRequested or PersistencePhase.SavingTestValue or PersistencePhase.TestValueSaveConfirmed or
                PersistencePhase.WaitingForFirstReboot or PersistencePhase.TestValuePersistenceVerified or PersistencePhase.OriginalValueWriteAuthorized or PersistencePhase.OriginalValueAppliedRam => 1,
            PersistencePhase.OriginalValueSaveRequested or PersistencePhase.SavingOriginalValue or PersistencePhase.OriginalValueSaveConfirmed or
                PersistencePhase.WaitingForSecondReboot or PersistencePhase.FinalRestorationVerified or PersistencePhase.Complete => 2,
            _ => journal.ReservedSaveCount
        };
        if (journal.Phase is not (PersistencePhase.ResultUncertain or PersistencePhase.Failed) && journal.ReservedSaveCount != expectedReservations)
            throw new InvalidDataException("Journal phase contradicts the reserved SAVE count.");
        var expectedAuthorization = journal.Phase switch
        {
            PersistencePhase.Initial or PersistencePhase.PreflightComplete => PersistenceAuthorizationStage.BoundPreflight,
            PersistencePhase.TestValueWriteAuthorized or PersistencePhase.TestValueAppliedRam or PersistencePhase.TestValueSaveRequested or PersistencePhase.SavingTestValue or PersistencePhase.TestValueSaveConfirmed => PersistenceAuthorizationStage.CycleAWrites,
            PersistencePhase.WaitingForFirstReboot => PersistenceAuthorizationStage.WaitingForFirstReboot,
            PersistencePhase.TestValuePersistenceVerified or PersistencePhase.OriginalValueWriteAuthorized or PersistencePhase.OriginalValueAppliedRam or PersistencePhase.OriginalValueSaveRequested or PersistencePhase.SavingOriginalValue or PersistencePhase.OriginalValueSaveConfirmed => PersistenceAuthorizationStage.CycleBWrites,
            PersistencePhase.WaitingForSecondReboot => PersistenceAuthorizationStage.WaitingForSecondReboot,
            PersistencePhase.FinalRestorationVerified or PersistencePhase.Complete => PersistenceAuthorizationStage.Complete,
            _ => PersistenceAuthorizationStage.Locked
        };
        if (journal.AuthorizationStage != expectedAuthorization) throw new InvalidDataException("Journal authorization stage contradicts the phase.");
        if (journal.ReservedSaveCount >= 1 && (journal.CycleAEvidence.SaveToken is null or 0 || journal.CycleAEvidence.SaveBefore is null || journal.CycleAEvidence.SaveReservedAtUtc is null))
            throw new InvalidDataException("Cycle A SAVE reservation evidence is incomplete.");
        if (journal.ReservedSaveCount == 2 && (journal.CycleBEvidence?.SaveToken is null or 0 || journal.CycleBEvidence.SaveBefore is null || journal.CycleBEvidence.SaveReservedAtUtc is null || journal.CycleAEvidence.RebootEvidence is null))
            throw new InvalidDataException("Cycle B SAVE reservation requires complete cycle A reboot evidence.");
        var cycleTokens = new[] { journal.CycleAEvidence.SaveToken, journal.CycleBEvidence?.SaveToken }
            .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        if (!cycleTokens.SequenceEqual(journal.SaveTokens) ||
            (journal.ReservedSaveCount >= 1 && (!journal.CycleAEvidence.SaveRequestMayHaveBeenSent || !journal.CycleAEvidence.MailboxTokens.Contains(journal.CycleAEvidence.SaveToken!.Value))) ||
            (journal.ReservedSaveCount == 2 && (!journal.CycleBEvidence!.SaveRequestMayHaveBeenSent || !journal.CycleBEvidence.MailboxTokens.Contains(journal.CycleBEvidence.SaveToken!.Value))))
            throw new InvalidDataException("Journal SAVE Token and cycle evidence are contradictory.");
        if (journal.Phase == PersistencePhase.Complete && (journal.ReservedSaveCount != 2 || !ValidReboot(journal.CycleBEvidence?.RebootEvidence) || journal.FinalConfigurationMatches64Of64 != true))
            throw new InvalidDataException("Complete journal lacks two SAVEs, two reboots, or final 64/64 verification.");
        if (journal.Phase == PersistencePhase.ResultUncertain && string.IsNullOrWhiteSpace(journal.ResultUncertainReason))
            throw new InvalidDataException("RESULT_UNCERTAIN journal has no reason.");
        if (journal.Events.Length == 0 || journal.Events.Any(x => x is null || x.At == default) || journal.Events.Zip(journal.Events.Skip(1)).Any(x => x.First.At > x.Second.At))
            throw new InvalidDataException("Journal event history is invalid.");
        if (path is not null && Path.GetFileName(path).Equals("persistence-journal.json", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))!).EndsWith("_" + journal.WorkflowId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Journal workflow ID does not match its directory.");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidReboot(PersistenceRebootEvidence? evidence) => evidence is not null &&
        evidence.ActiveConfiguration is { Length: 64 } && IsSha256(evidence.ActiveSha256) && evidence.Mailbox is not null &&
        evidence.ConfigStore is not null && evidence.Identity is not null;
}

public static class PersistenceEvidenceDirectory
{
    public static string CreateUnique(string root, string workflowId, DateTimeOffset now)
    {
        if (!Guid.TryParse(workflowId, out _)) throw new ArgumentException("Workflow ID must be a GUID.", nameof(workflowId));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{now.UtcDateTime:yyyyMMddTHHmmssfffZ}_{workflowId}");
        if (Directory.Exists(path)) throw new IOException("Evidence directory already exists; existing evidence will not be overwritten.");
        Directory.CreateDirectory(path);
        return path;
    }
}
