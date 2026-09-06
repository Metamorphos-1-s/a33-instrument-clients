namespace A33.Instrument.Core;

public sealed record DeviceIdentity(ushort FirmwareVersion, ushort SchemaVersion, ushort MapVersion, byte UnitId);
public sealed record MailboxCommandReceipt(ushort Token, ushort CommandId, ushort ResultCode);

public sealed class AmbiguousDeviceCommandException(string message, Exception? innerException = null) : IOException(message, innerException);

public interface IConfigurationPersistenceDevice : IReadOnlyRegisterAccess
{
    Task<DeviceIdentity> ReadIdentityAsync(CancellationToken cancellationToken = default);
    Task<MailboxSnapshot> ReadMailboxAsync(CancellationToken cancellationToken = default);
    Task<ConfigStoreSnapshot> ReadConfigStoreAsync(CancellationToken cancellationToken = default);
    Task<ushort[]> ReadActiveConfigurationAsync(CancellationToken cancellationToken = default);
    Task ApplyBrightnessRamAsync(ushort brightness, MailboxTokenAllocator tokens, CancellationToken cancellationToken = default);
    Task<MailboxCommandReceipt> SendSaveOnceAsync(ushort token, CancellationToken cancellationToken = default);
}

public interface IPersistenceClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemPersistenceClock : IPersistenceClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed record PersistencePollingPolicy(TimeSpan Interval, TimeSpan Timeout)
{
    public static PersistencePollingPolicy Default { get; } = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30));
}

public sealed record ConfigStoreSamplingPolicy(int MaximumAttempts, int RequiredConsecutiveTerminalSamples, TimeSpan Interval)
{
    public static ConfigStoreSamplingPolicy Default { get; } = new(4, 2, TimeSpan.FromMilliseconds(50));
}

public sealed record PersistenceSafetyContext(TrustedPersistenceBaseline Baseline, ValidatedPreflightBinding Preflight, string ClientCommit);

public interface IConfigurationPersistenceService
{
    Task<PersistenceJournal> StartAsync(string journalPath, string workflowId, string clientCommit, CancellationToken cancellationToken = default);
    Task<PersistenceJournal> ResumeAfterManualRebootAsync(string journalPath, CancellationToken cancellationToken = default);
}

public sealed class ConfigurationPersistenceService(
    IConfigurationPersistenceDevice device,
    PersistenceSafetyContext safetyContext,
    IPersistenceClock? clock = null,
    PersistencePollingPolicy? pollingPolicy = null,
    ConfigStoreSamplingPolicy? samplingPolicy = null) : IConfigurationPersistenceService
{
    public const int FixedSaveBudget = 2;
    public const ushort OriginalBrightness = 3;
    public const ushort TestBrightness = 4;
    public const int BrightnessOffset = 0x0116 - 0x0100;
    public const string FixedStm32Commit = "71a61249645bff6249286ac801d7f468786cfe85";

    private readonly IPersistenceClock clock = clock ?? new SystemPersistenceClock();
    private readonly PersistencePollingPolicy polling = pollingPolicy ?? PersistencePollingPolicy.Default;
    private readonly ConfigStoreSamplingPolicy sampling = samplingPolicy ?? ConfigStoreSamplingPolicy.Default;

    public async Task<PersistenceJournal> StartAsync(
        string journalPath, string workflowId, string clientCommit,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(workflowId, out _)) throw new ArgumentException("Workflow ID must be a GUID.", nameof(workflowId));
        if (clientCommit != safetyContext.ClientCommit || clientCommit != safetyContext.Preflight.Summary.ClientCommit)
            throw new InvalidOperationException("Current client commit does not match the bound preflight evidence.");
        var identity = await device.ReadIdentityAsync(cancellationToken);
        var mailbox = await device.ReadMailboxAsync(cancellationToken);
        var store = await ReadStableConfigStoreAsync(cancellationToken);
        var original = await ReadActive64Async(cancellationToken);
        ValidateOnSiteGate(identity, mailbox, store, original);
        var expected = WithBrightness(original, TestBrightness);
        var now = clock.UtcNow;
        var journal = new PersistenceJournal
        {
            WorkflowId = workflowId, ClientCommit = clientCommit, Stm32Commit = FixedStm32Commit,
            BaselineId = safetyContext.Baseline.Manifest.BaselineId,
            BaselineSha256 = safetyContext.Baseline.Manifest.ActiveArraySha256,
            BaselineManifestSha256 = safetyContext.Baseline.ManifestSha256,
            BoundPreflightWorkflowId = safetyContext.Preflight.WorkflowId,
            PreflightSummarySha256 = safetyContext.Preflight.SummarySha256,
            CreatedAt = now, UpdatedAt = now, Phase = PersistencePhase.PreflightComplete, Cycle = PersistenceCycle.A,
            AuthorizationStage = PersistenceAuthorizationStage.BoundPreflight,
            OriginalActiveConfiguration = original, ExpectedActiveConfiguration = expected,
            SaveTokens = [], PollSnapshots = [], LastMailboxToken = mailbox.ResponseToken,
            Events = [new(now, PersistenceEventKind.Information, "Bound preflight evidence and the read-only on-site safety gate were validated.")],
            CycleAEvidence = new PersistenceCycleEvidence { Cycle = PersistenceCycle.A, ExpectedActiveConfiguration = expected, ActiveBeforeApply = original }
        };
        await SaveJournalAsync(journalPath, journal, cancellationToken);
        return await ExecuteCycleAsync(journalPath, journal, TestBrightness, mailbox.ResponseToken, cancellationToken);
    }

    public async Task<PersistenceJournal> ResumeAfterManualRebootAsync(string journalPath, CancellationToken cancellationToken = default)
    {
        PersistenceJournal journal;
        try { journal = await PersistenceJournalStore.ReadAsync(journalPath, cancellationToken); }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            throw new InvalidDataException("Persistence recovery is safely blocked because the journal cannot be trusted.", error);
        }
        ValidateJournalBinding(journal);
        if (journal.Phase is PersistencePhase.ResultUncertain or PersistencePhase.Failed or PersistencePhase.Complete)
            return journal;
        if (journal.Phase is PersistencePhase.TestValueSaveRequested or PersistencePhase.SavingTestValue or
            PersistencePhase.OriginalValueSaveRequested or PersistencePhase.SavingOriginalValue)
            return await RecoverReservedSaveAsync(journalPath, journal, cancellationToken);
        if (journal.Phase is PersistencePhase.TestValueAppliedRam or PersistencePhase.OriginalValueAppliedRam)
            return await MarkUncertainAsync(journalPath, journal, "Process stopped after APPLY and before a durable SAVE reservation; automatic write recovery is blocked.", cancellationToken);
        if (journal.Phase is not (PersistencePhase.TestValueSaveConfirmed or PersistencePhase.OriginalValueSaveConfirmed or
            PersistencePhase.WaitingForFirstReboot or PersistencePhase.WaitingForSecondReboot or PersistencePhase.TestValuePersistenceVerified))
            throw new InvalidOperationException("Journal is not waiting for a manual reboot.");

        var identity = await device.ReadIdentityAsync(cancellationToken);
        var mailbox = await device.ReadMailboxAsync(cancellationToken);
        var store = await ReadStableConfigStoreAsync(cancellationToken);
        var active = await ReadActive64Async(cancellationToken);
        var cycleEvidence = journal.Phase == PersistencePhase.TestValuePersistenceVerified
            ? journal.CycleAEvidence
            : CurrentCycleEvidence(journal);
        var expectedConfirmed = cycleEvidence.SaveConfirmed;
        var expectedActive = cycleEvidence.ExpectedActiveConfiguration;
        var rebootValid = identity is { FirmwareVersion: 0x050A, SchemaVersion: 2, MapVersion: 0x0104, UnitId: 1 } &&
            mailbox.ResponseToken == 0 && !mailbox.Busy && !mailbox.Pending &&
            store.StatesKnown && store.StatesConsistent && store.State == ConfigStoreState.Idle &&
            !store.ConfigDirty && store.CurrentRevision == store.SavedRevision && expectedConfirmed is not null &&
            store.ActiveSlot == expectedConfirmed.ActiveSlot && store.ActiveSequence == expectedConfirmed.ActiveSequence &&
            store.CurrentRevision == expectedConfirmed.CurrentRevision && expectedActive.SequenceEqual(active);
        if (!rebootValid)
            return await MarkUncertainAsync(journalPath, journal, "Post-reboot identity, Mailbox, ConfigStore metadata, or Active configuration continuity failed.", cancellationToken);
        var now = clock.UtcNow;
        var rebootEvidence = new PersistenceRebootEvidence(now, identity, mailbox, store, active,
            PersistenceBaselineContract.ComputeActiveSha256(active));
        cycleEvidence = cycleEvidence with { RebootEvidence = rebootEvidence };
        journal = SetCycleEvidence(journal, cycleEvidence);
        journal = AddEvent(journal with
        {
            UpdatedAt = now,
            LastMailboxToken = mailbox.ResponseToken,
            MailboxTokenAfterFirstReboot = journal.Cycle == PersistenceCycle.A ? mailbox.ResponseToken : journal.MailboxTokenAfterFirstReboot,
            MailboxTokenAfterSecondReboot = journal.Cycle == PersistenceCycle.B ? mailbox.ResponseToken : journal.MailboxTokenAfterSecondReboot
        }, PersistenceEventKind.RebootObserved, "Manual reboot observed; identity, Mailbox and ConfigStore were read before any write.");

        if (journal.Cycle == PersistenceCycle.A)
        {
            var cycleB = new PersistenceCycleEvidence
            {
                Cycle = PersistenceCycle.B,
                ExpectedActiveConfiguration = journal.OriginalActiveConfiguration.ToArray(),
                ActiveBeforeApply = active.ToArray()
            };
            journal = AddEvent(journal with
            {
                Phase = PersistencePhase.TestValuePersistenceVerified, Cycle = PersistenceCycle.B,
                AuthorizationStage = PersistenceAuthorizationStage.CycleBWrites,
                VerificationResult = "Brightness 4 and cycle A metadata persisted after the first manual reboot.",
                CycleBEvidence = cycleB,
                WaitingForFirstReboot = false
            }, PersistenceEventKind.Verified, "Cycle A persistence verified; cycle B may begin.");
            await SaveJournalAsync(journalPath, journal, cancellationToken);
            return await ExecuteCycleAsync(journalPath, journal, OriginalBrightness, mailbox.ResponseToken, cancellationToken);
        }

        if (journal.Phase == PersistencePhase.TestValuePersistenceVerified)
        {
            return await ExecuteCycleAsync(journalPath, journal, OriginalBrightness, mailbox.ResponseToken, cancellationToken);
        }

        var matches = journal.OriginalActiveConfiguration.SequenceEqual(active) &&
            PersistenceBaselineContract.ComputeActiveSha256(active) == safetyContext.Baseline.Manifest.ActiveArraySha256 &&
            active[BrightnessOffset] == OriginalBrightness;
        journal = AddEvent(journal with
        {
            UpdatedAt = clock.UtcNow,
            Phase = matches ? PersistencePhase.Complete : PersistencePhase.ResultUncertain,
            Cycle = matches ? PersistenceCycle.Complete : PersistenceCycle.B,
            AuthorizationStage = matches ? PersistenceAuthorizationStage.Complete : PersistenceAuthorizationStage.Locked,
            WaitingForSecondReboot = false,
            VerificationResult = matches ? "Brightness 3 and the original active configuration were restored 64/64." : null,
            ResultUncertainReason = matches ? null : "Final active configuration does not match the original 64-register snapshot.",
            FinalConfigurationMatches64Of64 = matches
        }, matches ? PersistenceEventKind.Verified : PersistenceEventKind.ResultUncertain,
            matches ? "Cycle B persistence and final 64/64 restoration verified." : "Final restoration verification is uncertain.");
        await SaveJournalAsync(journalPath, journal, cancellationToken);
        return journal;
    }

    private async Task<PersistenceJournal> ExecuteCycleAsync(
        string journalPath, PersistenceJournal journal, ushort brightness,
        ushort observedDeviceToken, CancellationToken cancellationToken)
    {
        if (journal.ReservedSaveCount >= FixedSaveBudget)
            return await MarkUncertainAsync(journalPath, journal, "The fixed two-SAVE budget is exhausted.", cancellationToken);
        var tokens = new MailboxTokenAllocator(observedDeviceToken);
        journal = journal with
        {
            UpdatedAt = clock.UtcNow,
            Phase = journal.Cycle == PersistenceCycle.A ? PersistencePhase.TestValueWriteAuthorized : PersistencePhase.OriginalValueWriteAuthorized,
            AuthorizationStage = journal.Cycle == PersistenceCycle.A ? PersistenceAuthorizationStage.CycleAWrites : PersistenceAuthorizationStage.CycleBWrites
        };
        await SaveJournalAsync(journalPath, journal, cancellationToken);
        ushort[] activeBeforeApply;
        try { activeBeforeApply = await ReadActive64Async(cancellationToken); }
        catch (Exception error) { return await MarkUncertainAsync(journalPath, journal, $"Pre-APPLY readback failed: {error.Message}", cancellationToken); }
        var expectedBeforeApply = journal.Cycle == PersistenceCycle.A
            ? safetyContext.Baseline.Manifest.ActiveRegisters
            : WithBrightness(journal.OriginalActiveConfiguration, TestBrightness);
        if (!expectedBeforeApply.SequenceEqual(activeBeforeApply))
            return await MarkUncertainAsync(journalPath, journal, "Pre-APPLY Active configuration does not match the required cycle input.", cancellationToken);
        try
        {
            await device.ApplyBrightnessRamAsync(brightness, tokens, cancellationToken);
        }
        catch (Exception error)
        {
            return await MarkUncertainAsync(journalPath, journal, $"APPLY result is uncertain: {error.Message}", CancellationToken.None);
        }

        var expected = WithBrightness(journal.OriginalActiveConfiguration, brightness);
        ushort[] applied;
        ConfigStoreSnapshot before;
        try
        {
            applied = await ReadActive64Async(cancellationToken);
            before = await ReadStableConfigStoreAsync(cancellationToken);
        }
        catch (Exception error)
        {
            return await MarkUncertainAsync(journalPath, journal, $"APPLY readback is uncertain: {error.Message}", CancellationToken.None);
        }
        if (!expected.SequenceEqual(applied))
            return await MarkUncertainAsync(journalPath, journal, "APPLY readback does not match the expected 64-register configuration.", cancellationToken);
        if (before.SchemaVersion != 2 || !before.StatesKnown || !before.StatesConsistent || before.State != ConfigStoreState.Idle ||
            !before.ConfigDirty || before.CurrentRevision == before.SavedRevision || ConfigStoreContract.NextSlot(before.ActiveSlot) == 0)
            return await MarkUncertainAsync(journalPath, journal, "ConfigStore pre-SAVE snapshot is not trustworthy.", cancellationToken);

        var cycle = CurrentCycleEvidence(journal) with
        {
            ActiveBeforeApply = activeBeforeApply,
            ActiveAfterApply = applied,
            SaveBefore = before,
            MailboxTokens = tokens.Used.ToArray()
        };
        journal = SetCurrentCycleEvidence(journal, cycle);
        journal = AddEvent(journal with
        {
            UpdatedAt = clock.UtcNow,
            Phase = journal.Cycle == PersistenceCycle.A ? PersistencePhase.TestValueAppliedRam : PersistencePhase.OriginalValueAppliedRam,
            ExpectedActiveConfiguration = expected, ApplyResult = "APPLIED_RAM", LastMailboxToken = tokens.LastIssued
        }, PersistenceEventKind.AppliedRam, $"Brightness {brightness} applied in RAM and verified 64/64.");
        await SaveJournalAsync(journalPath, journal, cancellationToken);

        var saveToken = tokens.Allocate();
        while (journal.SaveTokens.Contains(saveToken)) saveToken = tokens.Allocate();
        var reservedTokens = journal.SaveTokens.Append(saveToken).ToArray();
        cycle = CurrentCycleEvidence(journal) with
        {
            SaveBefore = before,
            MailboxTokens = tokens.Used.ToArray(),
            SaveToken = saveToken,
            SaveReservedAtUtc = clock.UtcNow,
            SaveRequestMayHaveBeenSent = true
        };
        journal = SetCurrentCycleEvidence(journal, cycle);
        journal = AddEvent(journal with
        {
            UpdatedAt = clock.UtcNow,
            Phase = journal.Cycle == PersistenceCycle.A ? PersistencePhase.TestValueSaveRequested : PersistencePhase.OriginalValueSaveRequested,
            ReservedSaveCount = journal.ReservedSaveCount + 1, SaveTokens = reservedTokens,
            LastMailboxToken = saveToken, SaveBefore = before,
            MailboxTokenBeforeFirstReboot = journal.Cycle == PersistenceCycle.A ? saveToken : journal.MailboxTokenBeforeFirstReboot,
            MailboxTokenBeforeSecondReboot = journal.Cycle == PersistenceCycle.B ? saveToken : journal.MailboxTokenBeforeSecondReboot
        }, PersistenceEventKind.SaveReserved, $"Reserved SAVE budget before sending command 13 with token {saveToken}.");
        await SaveJournalAsync(journalPath, journal, cancellationToken);

        MailboxCommandReceipt receipt;
        try { receipt = await device.SendSaveOnceAsync(saveToken, cancellationToken); }
        catch (Exception error)
        {
            return await MarkUncertainAsync(journalPath, journal, $"SAVE may have been sent but its response is uncertain: {error.Message}", CancellationToken.None);
        }
        if (receipt.Token != saveToken || receipt.CommandId != 13)
            return await MarkUncertainAsync(journalPath, journal, "SAVE response token or command does not match the request.", cancellationToken);
        if (receipt.ResultCode != 1)
            return await MarkFailedAsync(journalPath, journal, $"SAVE request was rejected with result {receipt.ResultCode}.", cancellationToken);

        journal = AddEvent(journal with
        {
            UpdatedAt = clock.UtcNow,
            Phase = journal.Cycle == PersistenceCycle.A ? PersistencePhase.SavingTestValue : PersistencePhase.SavingOriginalValue,
            SaveResult = "MAILBOX_ACCEPTED_NOT_FLASH_CONFIRMED"
        }, PersistenceEventKind.SaveSent, "SAVE command 13 was sent exactly once; Mailbox ACCEPTED is not Flash confirmation.");
        await SaveJournalAsync(journalPath, journal, cancellationToken);

        var deadline = clock.UtcNow + polling.Timeout;
        string? lastReadError = null;
        var unstableSamples = 0;
        var consecutiveConfirmed = 0;
        ConfigStoreSnapshot? previousConfirmed = null;
        while (clock.UtcNow <= deadline)
        {
            try
            {
                var current = await device.ReadConfigStoreAsync(cancellationToken);
                var active = await ReadActive64Async(cancellationToken);
                var observation = new ConfigStoreObservation(clock.UtcNow, journal.Cycle, current);
                cycle = CurrentCycleEvidence(journal) with { PollSnapshots = CurrentCycleEvidence(journal).PollSnapshots.Append(observation).ToArray() };
                journal = SetCurrentCycleEvidence(journal, cycle) with
                {
                    UpdatedAt = clock.UtcNow,
                    PollSnapshots = journal.PollSnapshots.Append(current).ToArray()
                };
                if (!current.StatesKnown || !current.StatesConsistent)
                {
                    unstableSamples++;
                    consecutiveConfirmed = 0;
                    previousConfirmed = null;
                    await SaveJournalAsync(journalPath, journal, cancellationToken);
                    if (unstableSamples >= sampling.MaximumAttempts)
                        return await MarkUncertainAsync(journalPath, journal, "ConfigStore state mirrors remained unknown or inconsistent after bounded read-only resampling.", cancellationToken);
                    await clock.DelayAsync(sampling.Interval, cancellationToken);
                    continue;
                }
                unstableSamples = 0;
                var evaluation = ConfigStoreCompletionEvaluator.Evaluate(before, current, expected, active);
                if (evaluation.Result == ConfigStoreCompletionResult.Confirmed)
                {
                    consecutiveConfirmed = current == previousConfirmed ? consecutiveConfirmed + 1 : 1;
                    previousConfirmed = current;
                    if (consecutiveConfirmed < sampling.RequiredConsecutiveTerminalSamples)
                    {
                        await SaveJournalAsync(journalPath, journal, cancellationToken);
                        await clock.DelayAsync(sampling.Interval, cancellationToken);
                        continue;
                    }
                    var cycleA = journal.Cycle == PersistenceCycle.A;
                    cycle = CurrentCycleEvidence(journal) with { SaveConfirmed = current };
                    journal = SetCurrentCycleEvidence(journal, cycle);
                    journal = AddEvent(journal with
                    {
                        Phase = cycleA ? PersistencePhase.TestValueSaveConfirmed : PersistencePhase.OriginalValueSaveConfirmed,
                        SaveConfirmed = current, SaveResult = "SAVE_CONFIRMED_BY_PUBLIC_REGISTERS",
                    }, PersistenceEventKind.SaveConfirmed, "SAVE confirmed by all public ConfigStore invariants.");
                    await SaveJournalAsync(journalPath, journal, cancellationToken);
                    journal = AddEvent(journal with
                    {
                        UpdatedAt = clock.UtcNow,
                        Phase = cycleA ? PersistencePhase.WaitingForFirstReboot : PersistencePhase.WaitingForSecondReboot,
                        AuthorizationStage = cycleA ? PersistenceAuthorizationStage.WaitingForFirstReboot : PersistenceAuthorizationStage.WaitingForSecondReboot,
                        WaitingForFirstReboot = cycleA,
                        WaitingForSecondReboot = !cycleA
                    }, PersistenceEventKind.RebootRequired, cycleA
                        ? "SAVE confirmed. Stop all writes and perform the first manual reboot."
                        : "SAVE confirmed. Stop all writes and perform the second manual reboot.");
                    await SaveJournalAsync(journalPath, journal, cancellationToken);
                    return journal;
                }
                if (evaluation.Result == ConfigStoreCompletionResult.Failed)
                    return await MarkFailedAsync(journalPath, journal, evaluation.Reason, cancellationToken);
                if (evaluation.Result == ConfigStoreCompletionResult.ResultUncertain)
                    return await MarkUncertainAsync(journalPath, journal, evaluation.Reason, cancellationToken);
                consecutiveConfirmed = 0;
                previousConfirmed = null;
                lastReadError = null;
                await SaveJournalAsync(journalPath, journal, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await MarkUncertainAsync(journalPath, journal, "Polling was cancelled after SAVE was accepted.", CancellationToken.None);
            }
            catch (Exception error) { lastReadError = error.Message; }
            try { await clock.DelayAsync(polling.Interval, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await MarkUncertainAsync(journalPath, journal, "Polling was cancelled after SAVE was accepted.", CancellationToken.None);
            }
        }
        var reason = "SAVE completion timeout; public registers do not prove success or explicit failure.";
        if (lastReadError is not null) reason += $" Last read error: {lastReadError}";
        return await MarkUncertainAsync(journalPath, journal, reason, cancellationToken);
    }

    private async Task<PersistenceJournal> RecoverReservedSaveAsync(string path, PersistenceJournal journal, CancellationToken cancellationToken)
    {
        var cycle = CurrentCycleEvidence(journal);
        if (cycle.SaveBefore is null)
            return await MarkUncertainAsync(path, journal, "Reserved SAVE has no trustworthy pre-SAVE ConfigStore snapshot.", cancellationToken);
        try
        {
            var identity = await device.ReadIdentityAsync(cancellationToken);
            ValidateIdentity(identity);
            var current = await ReadStableConfigStoreAsync(cancellationToken);
            var active = await ReadActive64Async(cancellationToken);
            var evaluation = ConfigStoreCompletionEvaluator.Evaluate(cycle.SaveBefore, current, cycle.ExpectedActiveConfiguration, active);
            if (evaluation.Result == ConfigStoreCompletionResult.Confirmed)
            {
                var cycleA = journal.Cycle == PersistenceCycle.A;
                cycle = cycle with { SaveConfirmed = current };
                journal = SetCurrentCycleEvidence(journal, cycle);
                var recovered = AddEvent(journal with
                {
                    UpdatedAt = clock.UtcNow,
                    Phase = cycleA ? PersistencePhase.WaitingForFirstReboot : PersistencePhase.WaitingForSecondReboot,
                    AuthorizationStage = cycleA ? PersistenceAuthorizationStage.WaitingForFirstReboot : PersistenceAuthorizationStage.WaitingForSecondReboot,
                    SaveConfirmed = current,
                    SaveResult = "SAVE_CONFIRMED_BY_READ_ONLY_RECOVERY",
                    WaitingForFirstReboot = cycleA,
                    WaitingForSecondReboot = !cycleA
                }, PersistenceEventKind.SaveConfirmed, "A reserved SAVE was confirmed by read-only recovery; it was not retransmitted.");
                await SaveJournalAsync(path, recovered, cancellationToken);
                return recovered;
            }
            return await MarkUncertainAsync(path, journal, "A SAVE budget was reserved, but read-only recovery cannot prove whether the request reached the device.", cancellationToken);
        }
        catch (Exception error)
        {
            return await MarkUncertainAsync(path, journal, $"Reserved SAVE recovery failed: {error.Message}", CancellationToken.None);
        }
    }

    private async Task<ushort[]> ReadActive64Async(CancellationToken cancellationToken)
    {
        var active = await device.ReadActiveConfigurationAsync(cancellationToken);
        if (active.Length != 64) throw new InvalidDataException("Active configuration must contain exactly 64 registers.");
        return active;
    }

    private static ushort[] WithBrightness(IReadOnlyList<ushort> source, ushort brightness)
    {
        var result = source.ToArray();
        result[BrightnessOffset] = brightness;
        return result;
    }

    private static void ValidateIdentity(DeviceIdentity identity)
    {
        if (identity is not { FirmwareVersion: 0x050A, SchemaVersion: 2, MapVersion: 0x0104, UnitId: 1 })
            throw new InvalidOperationException("Device identity does not match the fixed Stage 2B persistence contract.");
    }

    private void ValidateOnSiteGate(DeviceIdentity identity, MailboxSnapshot mailbox, ConfigStoreSnapshot store, ushort[] active)
    {
        PersistenceBaselineContract.Validate(safetyContext.Baseline.Manifest);
        var boundStore = safetyContext.Preflight.ConfigStore;
        var valid = identity is { FirmwareVersion: 0x050A, SchemaVersion: 2, MapVersion: 0x0104, UnitId: 1 } &&
            !mailbox.Busy && !mailbox.Pending && store.SchemaVersion == 2 && store.StatesKnown && store.StatesConsistent &&
            store.State == ConfigStoreState.Idle && !store.ConfigDirty && store.CurrentRevision == store.SavedRevision &&
            store.ActiveSlot is 1 or 2 && store.ActiveSlot == boundStore.ActiveSlot &&
            store.ActiveSequence == boundStore.ActiveSequence && store.CurrentRevision == boundStore.CurrentRevision &&
            active.SequenceEqual(safetyContext.Baseline.Manifest.ActiveRegisters) &&
            PersistenceBaselineContract.ComputeActiveSha256(active) == safetyContext.Baseline.Manifest.ActiveArraySha256 &&
            active[BrightnessOffset] == OriginalBrightness;
        if (!valid) throw new InvalidOperationException("SAVE pre-write on-site safety gate failed before any write cycle was created.");
    }

    private void ValidateJournalBinding(PersistenceJournal journal)
    {
        if (journal.ClientCommit != safetyContext.ClientCommit || journal.BaselineId != safetyContext.Baseline.Manifest.BaselineId ||
            journal.BaselineSha256 != safetyContext.Baseline.Manifest.ActiveArraySha256 ||
            journal.BaselineManifestSha256 != safetyContext.Baseline.ManifestSha256 ||
            journal.BoundPreflightWorkflowId != safetyContext.Preflight.WorkflowId ||
            journal.PreflightSummarySha256 != safetyContext.Preflight.SummarySha256)
            throw new InvalidDataException("Persistence journal does not match the trusted baseline and preflight binding.");
    }

    private async Task<ConfigStoreSnapshot> ReadStableConfigStoreAsync(CancellationToken cancellationToken)
    {
        ConfigStoreSnapshot? previous = null;
        var consecutive = 0;
        for (var attempt = 0; attempt < sampling.MaximumAttempts; attempt++)
        {
            var current = await device.ReadConfigStoreAsync(cancellationToken);
            if (current.StatesKnown && current.StatesConsistent && current == previous) consecutive++;
            else consecutive = current.StatesKnown && current.StatesConsistent ? 1 : 0;
            if (consecutive >= sampling.RequiredConsecutiveTerminalSamples) return current;
            previous = current;
            if (attempt + 1 < sampling.MaximumAttempts) await clock.DelayAsync(sampling.Interval, cancellationToken);
        }
        throw new InvalidOperationException("ConfigStore did not produce the required consecutive stable snapshots.");
    }

    private static PersistenceCycleEvidence CurrentCycleEvidence(PersistenceJournal journal) => journal.Cycle switch
    {
        PersistenceCycle.A => journal.CycleAEvidence,
        PersistenceCycle.B => journal.CycleBEvidence ?? throw new InvalidDataException("Cycle B evidence is missing."),
        _ => throw new InvalidDataException("Completed workflow has no writable cycle.")
    };

    private static PersistenceJournal SetCurrentCycleEvidence(PersistenceJournal journal, PersistenceCycleEvidence evidence) =>
        SetCycleEvidence(journal, evidence);

    private static PersistenceJournal SetCycleEvidence(PersistenceJournal journal, PersistenceCycleEvidence evidence) => evidence.Cycle switch
    {
        PersistenceCycle.A => journal with { CycleAEvidence = evidence },
        PersistenceCycle.B => journal with { CycleBEvidence = evidence },
        _ => throw new InvalidDataException("Invalid persistence evidence cycle.")
    };

    private async Task<PersistenceJournal> MarkUncertainAsync(string path, PersistenceJournal journal, string reason, CancellationToken cancellationToken)
    {
        var updated = AddEvent(journal with
        {
            UpdatedAt = clock.UtcNow, Phase = PersistencePhase.ResultUncertain,
            AuthorizationStage = PersistenceAuthorizationStage.Locked,
            WaitingForFirstReboot = false, WaitingForSecondReboot = false,
            ResultUncertainReason = reason
        }, PersistenceEventKind.ResultUncertain, reason);
        await SaveJournalAsync(path, updated, cancellationToken);
        return updated;
    }

    private async Task<PersistenceJournal> MarkFailedAsync(string path, PersistenceJournal journal, string reason, CancellationToken cancellationToken)
    {
        var updated = AddEvent(journal with
        {
            UpdatedAt = clock.UtcNow, Phase = PersistencePhase.Failed,
            AuthorizationStage = PersistenceAuthorizationStage.Locked,
            WaitingForFirstReboot = false, WaitingForSecondReboot = false,
            SaveResult = reason
        }, PersistenceEventKind.Failed, reason);
        await SaveJournalAsync(path, updated, cancellationToken);
        return updated;
    }

    private static PersistenceJournal AddEvent(PersistenceJournal journal, PersistenceEventKind kind, string message) =>
        journal with { Events = journal.Events.Append(new(journal.UpdatedAt, kind, message)).ToArray() };

    private static Task SaveJournalAsync(string path, PersistenceJournal journal, CancellationToken cancellationToken) =>
        PersistenceJournalStore.WriteAtomicAsync(path, journal, cancellationToken);
}
