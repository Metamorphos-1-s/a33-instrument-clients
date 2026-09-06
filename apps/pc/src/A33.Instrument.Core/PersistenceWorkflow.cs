using System.Text.Json;

namespace A33.Instrument.Core;

public enum PersistencePhase
{
    Initial, PreflightComplete, TestValueAppliedRam, TestValueSaveRequested, SavingTestValue,
    TestValueSaveConfirmed, WaitingForFirstReboot, TestValuePersistenceVerified,
    OriginalValueAppliedRam, OriginalValueSaveRequested, SavingOriginalValue,
    OriginalValueSaveConfirmed, WaitingForSecondReboot, FinalRestorationVerified,
    Complete, ResultUncertain, Failed
}

public enum PersistenceCycle { A, B, Complete }
public enum PersistenceEventKind { Information, AppliedRam, SaveReserved, SaveSent, SaveConfirmed, RebootRequired, RebootObserved, Verified, ResultUncertain, Failed }
public sealed record PersistenceEvent(DateTimeOffset At, PersistenceEventKind Kind, string Message);

public sealed record PersistenceJournal
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string WorkflowId { get; init; }
    public required string ClientCommit { get; init; }
    public required string Stm32Commit { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public PersistencePhase Phase { get; init; }
    public PersistenceCycle Cycle { get; init; }
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
}

public static class PersistenceJournalStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task WriteAtomicAsync(string path, PersistenceJournal journal, CancellationToken cancellationToken = default)
    {
        Validate(journal);
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
            Validate(journal);
            return journal;
        }
        catch (JsonException error) { throw new InvalidDataException("Persistence journal is damaged.", error); }
    }

    public static void Validate(PersistenceJournal journal)
    {
        if (journal.SchemaVersion != PersistenceJournal.CurrentSchemaVersion) throw new InvalidDataException("Unsupported persistence journal schema.");
        if (journal.SaveBudget != ConfigurationPersistenceService.FixedSaveBudget || journal.ReservedSaveCount is < 0 or > ConfigurationPersistenceService.FixedSaveBudget)
            throw new InvalidDataException("Persistence journal SAVE budget is invalid.");
        if (journal.OriginalActiveConfiguration.Length != 64 || journal.ExpectedActiveConfiguration.Length != 64)
            throw new InvalidDataException("Persistence journal configuration snapshots must contain 64 registers.");
        if (journal.SaveTokens.Any(x => x == 0) || journal.SaveTokens.Distinct().Count() != journal.SaveTokens.Length || journal.SaveTokens.Length != journal.ReservedSaveCount)
            throw new InvalidDataException("Persistence journal SAVE token history is inconsistent.");
    }
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
