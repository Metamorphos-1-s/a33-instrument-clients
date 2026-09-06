using System.Text.Json;
using A33.Instrument.Core;
using A33.Instrument.HardwareValidation;
using A33.Instrument.Protocol;
using Xunit;

namespace A33.Instrument.Protocol.Tests;

public sealed class PersistenceTests
{
    private const string ClientCommit = "1111111111111111111111111111111111111111";
    [Fact] public void ConfigStoreAddressesMatchFirmware()
    {
        Assert.Equal(0x0030, ConfigStoreContract.StateMirror1Address);
        Assert.Equal(0x0032, ConfigStoreContract.ConfigDirtyAddress);
        Assert.Equal(0x0033, ConfigStoreContract.CurrentRevisionAddress);
        Assert.Equal(0x0035, ConfigStoreContract.SavedRevisionAddress);
        Assert.Equal(0x004C, ConfigStoreContract.MailboxResponseAddress);
        Assert.Equal(0x01C1, ConfigStoreContract.ActiveSlotAddress);
        Assert.Equal(0x01C2, ConfigStoreContract.ActiveSequenceAddress);
        Assert.Equal(0x01C4, ConfigStoreContract.StateMirror2Address);
    }

    [Theory]
    [InlineData(0, ConfigStoreState.Idle)] [InlineData(1, ConfigStoreState.Prepare)]
    [InlineData(2, ConfigStoreState.ErasePage0)] [InlineData(3, ConfigStoreState.ErasePage1)]
    [InlineData(4, ConfigStoreState.ProgramBody)] [InlineData(5, ConfigStoreState.VerifyBody)]
    [InlineData(6, ConfigStoreState.ProgramCommit)] [InlineData(7, ConfigStoreState.VerifyFinal)]
    [InlineData(8, ConfigStoreState.Complete)] [InlineData(9, ConfigStoreState.Error)]
    public void ConfigStoreStatesAreStronglyTyped(ushort raw, ConfigStoreState expected) => Assert.Equal(expected, ConfigStoreContract.ParseState(raw));

    [Fact] public void UnknownConfigStoreStateIsNotAccepted() => Assert.Null(ConfigStoreContract.ParseState(10));

    [Fact] public void ConfigStoreDecodeUsesHighWordFirst()
    {
        var value = ConfigStoreContract.Decode([0, 1, 1, 0x1234, 0x5678, 0x9ABC, 0xDEF0], [2, 2, 0x0102, 0x0304, 0], WordOrder.HighWordFirst);
        Assert.Equal(0x12345678U, value.CurrentRevision); Assert.Equal(0x9ABCDEF0U, value.SavedRevision); Assert.Equal(0x01020304U, value.ActiveSequence);
    }

    [Fact] public void ConfigStoreDecodeUsesConfiguredLowWordFirst()
    {
        var value = ConfigStoreContract.Decode([0, 1, 0, 0x5678, 0x1234, 0xDEF0, 0x9ABC], [2, 1, 0x0304, 0x0102, 0], WordOrder.LowWordFirst);
        Assert.Equal(0x12345678U, value.CurrentRevision); Assert.Equal(0x9ABCDEF0U, value.SavedRevision); Assert.Equal(0x01020304U, value.ActiveSequence);
    }

    [Fact] public void StateMirrorsMustAgree()
    {
        Assert.True(Store().StatesConsistent);
        Assert.False(Store(state2: ConfigStoreState.Complete).StatesConsistent);
    }

    [Fact] public void SequenceAndSlotFollowFirmwareRules()
    {
        Assert.Equal(8U, ConfigStoreContract.NextSequence(7));
        Assert.Equal(0U, ConfigStoreContract.NextSequence(0xFFFFFFFE));
        Assert.Equal((ushort)2, ConfigStoreContract.NextSlot(1));
        Assert.Equal((ushort)1, ConfigStoreContract.NextSlot(2));
    }

    [Theory]
    [InlineData("dirty")] [InlineData("revision")] [InlineData("sequence")]
    [InlineData("slot")] [InlineData("active")]
    public void CompletionRequiresEveryPublicInvariant(string broken)
    {
        var before = Store(dirty: true, current: 11, saved: 10, slot: 1, sequence: 7);
        var expected = Active(4); var actual = expected.ToArray();
        var after = Store(current: 11, saved: 11, slot: 2, sequence: 8);
        after = broken switch
        {
            "dirty" => after with { ConfigDirty = true },
            "revision" => after with { SavedRevision = 10 },
            "sequence" => after with { ActiveSequence = 7 },
            "slot" => after with { ActiveSlot = 1 },
            _ => after
        };
        if (broken == "active") actual[22] = 3;
        Assert.NotEqual(ConfigStoreCompletionResult.Confirmed, ConfigStoreCompletionEvaluator.Evaluate(before, after, expected, actual).Result);
    }

    [Theory] [InlineData(0)] [InlineData(8)]
    public void CompletionAllowsIdleOrTransientComplete(ushort terminalState)
    {
        var before = Store(dirty: true, current: 11, saved: 10, slot: 1, sequence: 7);
        var after = Store((ConfigStoreState)terminalState, (ConfigStoreState)terminalState, false, 11, 11, 2, 8);
        Assert.Equal(ConfigStoreCompletionResult.Confirmed, ConfigStoreCompletionEvaluator.Evaluate(before, after, Active(4), Active(4)).Result);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void BusyConfigStoreStatesRemainInProgress(ushort state)
    {
        var before = Store(dirty: true, current: 11, saved: 10);
        var busy = Store((ConfigStoreState)state, (ConfigStoreState)state, true, 11, 10);
        Assert.Equal(ConfigStoreCompletionResult.InProgress, ConfigStoreCompletionEvaluator.Evaluate(before, busy, Active(4), Active(4)).Result);
    }

    [Fact] public void MailboxAcceptedAloneCannotConfirmSave()
    {
        var before = Store(dirty: true, current: 11, saved: 10, slot: 1, sequence: 7);
        Assert.Equal(ConfigStoreCompletionResult.InProgress, ConfigStoreCompletionEvaluator.Evaluate(before, before, Active(4), Active(4)).Result);
    }

    [Fact] public void ConfigStoreErrorIsExplicitFailure()
    {
        var result = ConfigStoreCompletionEvaluator.Evaluate(Store(), Store(ConfigStoreState.Error, ConfigStoreState.Error), Active(4), Active(4));
        Assert.Equal(ConfigStoreCompletionResult.Failed, result.Result); Assert.Contains("not exposed", result.Reason);
    }

    [Fact] public void UnknownOrDisagreeingStateIsUncertain()
    {
        var unknown = Store() with { StateMirror1Raw = 20, StateMirror1 = null };
        Assert.Equal(ConfigStoreCompletionResult.ResultUncertain, ConfigStoreCompletionEvaluator.Evaluate(Store(), unknown, Active(4), Active(4)).Result);
        Assert.Equal(ConfigStoreCompletionResult.ResultUncertain, ConfigStoreCompletionEvaluator.Evaluate(Store(), Store(state2: ConfigStoreState.Complete), Active(4), Active(4)).Result);
    }

    [Fact] public void TokenAllocatorStartsAfterDeviceTokenAndSkipsZero()
    {
        var allocator = new MailboxTokenAllocator(41); Assert.Equal((ushort)42, allocator.Allocate());
        allocator = new MailboxTokenAllocator(ushort.MaxValue); Assert.Equal((ushort)1, allocator.Allocate());
    }

    [Fact] public void TokenAllocatorNeverRepeatsWithinRun()
    {
        var allocator = new MailboxTokenAllocator(0); var values = Enumerable.Range(0, 1000).Select(_ => allocator.Allocate()).ToArray();
        Assert.DoesNotContain((ushort)0, values); Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Fact] public async Task TwoIndependentProcessesCompleteFixedTwoSaveWorkflow()
    {
        using var files = new TempFiles(); var path = files.Path("journal.json"); var clock = new FakeClock();
        var firstDevice = new FakePersistenceDevice(Active(3), Store(slot: 1, sequence: 7), 0);
        var first = await Service(firstDevice, clock).StartAsync(path, Guid.NewGuid().ToString(), ClientCommit);
        Assert.Equal(PersistencePhase.WaitingForFirstReboot, first.Phase); Assert.Equal(1, firstDevice.SaveCalls); Assert.Equal(4, firstDevice.Active[22]);
        Assert.Contains(first.Events, item => item.Kind == PersistenceEventKind.SaveConfirmed);

        var secondDevice = FakePersistenceDevice.AfterReboot(firstDevice);
        var second = await Service(secondDevice, clock).ResumeAfterManualRebootAsync(path);
        Assert.Equal(PersistencePhase.WaitingForSecondReboot, second.Phase); Assert.Equal(1, secondDevice.SaveCalls); Assert.Equal(3, secondDevice.Active[22]);

        var thirdDevice = FakePersistenceDevice.AfterReboot(secondDevice);
        var complete = await Service(thirdDevice, clock).ResumeAfterManualRebootAsync(path);
        Assert.Equal(PersistencePhase.Complete, complete.Phase); Assert.True(complete.FinalConfigurationMatches64Of64);
        Assert.Equal(2, complete.ReservedSaveCount); Assert.Equal(2, complete.SaveTokens.Distinct().Count()); Assert.DoesNotContain((ushort)0, complete.SaveTokens);
        Assert.Equal(new ushort[] { 4, 5 }, complete.SaveTokens);
        Assert.Equal((ushort)0, complete.MailboxTokenAfterFirstReboot); Assert.Equal((ushort)0, complete.MailboxTokenAfterSecondReboot);
        Assert.NotNull(complete.CycleAEvidence.SaveConfirmed); Assert.NotNull(complete.CycleAEvidence.RebootEvidence);
        Assert.NotNull(complete.CycleBEvidence?.SaveConfirmed); Assert.NotNull(complete.CycleBEvidence?.RebootEvidence);
        Assert.NotSame(complete.CycleAEvidence.PollSnapshots, complete.CycleBEvidence!.PollSnapshots);
        var fourthDevice = FakePersistenceDevice.AfterReboot(thirdDevice);
        var stillComplete = await Service(fourthDevice, clock).ResumeAfterManualRebootAsync(path);
        Assert.Equal(PersistencePhase.Complete, stillComplete.Phase); Assert.Equal(0, fourthDevice.SaveCalls);
    }

    [Fact] public async Task WaitingForFirstRebootStopsBeforeCycleB()
    {
        using var files = new TempFiles(); var device = new FakePersistenceDevice(Active(3), Store(), 0);
        var journal = await Service(device).StartAsync(files.Path("j.json"), Guid.NewGuid().ToString(), ClientCommit);
        Assert.True(journal.WaitingForFirstReboot); Assert.Equal(1, device.SaveCalls);
    }

    [Fact] public async Task ProcessRestartWithoutDeviceRebootCannotPassManualGate()
    {
        using var files = new TempFiles(); var path = files.Path("j.json"); var clock = new FakeClock(); var firstDevice = new FakePersistenceDevice(Active(3), Store(), 0);
        await Service(firstDevice, clock).StartAsync(path, Guid.NewGuid().ToString(), ClientCommit);
        var noReboot = new FakePersistenceDevice(firstDevice.Active, firstDevice.Store, firstDevice.MailboxToken);
        var blocked = await Service(noReboot, clock).ResumeAfterManualRebootAsync(path);
        Assert.Equal(PersistencePhase.ResultUncertain, blocked.Phase); Assert.Equal(0, noReboot.SaveCalls);
    }

    [Fact] public async Task SaveResponseLossConsumesBudgetAndBecomesUncertainWithoutRetry()
    {
        using var files = new TempFiles(); var device = new FakePersistenceDevice(Active(3), Store(), 5) { SaveError = new IOException("lost") };
        var journal = await Service(device).StartAsync(files.Path("j.json"), Guid.NewGuid().ToString(), ClientCommit);
        Assert.Equal(PersistencePhase.ResultUncertain, journal.Phase); Assert.Equal(1, journal.ReservedSaveCount); Assert.Equal(1, device.SaveCalls);
    }

    [Fact] public async Task SaveBudgetIsDurableBeforeBottomLayerSend()
    {
        using var files = new TempFiles(); var path = files.Path("j.json"); PersistenceJournal? observed = null;
        var device = new FakePersistenceDevice(Active(3), Store(), 0) { BeforeSave = () => observed = PersistenceJournalStore.ReadAsync(path).GetAwaiter().GetResult() };
        await Service(device).StartAsync(path, Guid.NewGuid().ToString(), ClientCommit);
        Assert.NotNull(observed); Assert.Equal(1, observed.ReservedSaveCount); Assert.Single(observed.SaveTokens);
    }

    [Fact] public async Task SaveSendExceptionIsNeverRetried()
    {
        using var files = new TempFiles(); var device = new FakePersistenceDevice(Active(3), Store(), 0) { SaveError = new AmbiguousDeviceCommandException("send") };
        await Service(device).StartAsync(files.Path("j.json"), Guid.NewGuid().ToString(), ClientCommit); Assert.Equal(1, device.SaveCalls);
    }

    [Fact] public async Task ExplicitMailboxSaveRejectionFailsWithoutRetry()
    {
        using var files = new TempFiles(); var device = new FakePersistenceDevice(Active(3), Store(), 0) { SaveResultCode = 11 };
        var journal = await Service(device).StartAsync(files.Path("j.json"), Guid.NewGuid().ToString(), ClientCommit);
        Assert.Equal(PersistencePhase.Failed, journal.Phase); Assert.Equal(1, journal.ReservedSaveCount); Assert.Equal(1, device.SaveCalls);
    }

    [Fact] public async Task ApplyUncertainBlocksSave()
    {
        using var files = new TempFiles(); var device = new FakePersistenceDevice(Active(3), Store(), 0) { ApplyError = new AmbiguousDeviceCommandException("apply") };
        var journal = await Service(device).StartAsync(files.Path("j.json"), Guid.NewGuid().ToString(), ClientCommit);
        Assert.Equal(PersistencePhase.ResultUncertain, journal.Phase); Assert.Equal(0, device.SaveCalls);
    }

    [Theory]
    [InlineData("dirty")] [InlineData("revision")] [InlineData("slot")] [InlineData("metadata")]
    [InlineData("slot-binding")] [InlineData("revision-binding")] [InlineData("mailbox")] [InlineData("active")]
    public async Task OnSiteGateBlocksBeforeApplyAndSave(string failure)
    {
        using var files = new TempFiles(); var active = Active(3); var store = Store();
        if (failure == "dirty") store = store with { ConfigDirty = true };
        if (failure == "revision") store = store with { SavedRevision = store.CurrentRevision - 1 };
        if (failure == "slot") store = store with { ActiveSlot = 0 };
        if (failure == "metadata") store = store with { ActiveSequence = store.ActiveSequence + 1 };
        if (failure == "slot-binding") store = store with { ActiveSlot = 2 };
        if (failure == "revision-binding") store = store with { CurrentRevision = 11, SavedRevision = 11 };
        if (failure == "active") active[0]++;
        var device = new FakePersistenceDevice(active, store, 0) { MailboxState = failure == "mailbox" ? (ushort)1 : (ushort)0 };
        await Assert.ThrowsAnyAsync<Exception>(() => Service(device).StartAsync(files.Path("j.json"), Guid.NewGuid().ToString(), ClientCommit));
        Assert.Equal(0, device.ApplyCalls); Assert.Equal(0, device.SaveCalls); Assert.False(File.Exists(files.Path("j.json")));
    }

    [Theory]
    [InlineData("slot")] [InlineData("sequence")] [InlineData("revision")] [InlineData("active")] [InlineData("mailbox")]
    public async Task FirstRebootMetadataMismatchBlocksCycleBSave(string failure)
    {
        using var files = new TempFiles(); var path = files.Path("j.json"); var clock = new FakeClock();
        var firstDevice = new FakePersistenceDevice(Active(3), Store(), 0);
        await Service(firstDevice, clock).StartAsync(path, Guid.NewGuid().ToString(), ClientCommit);
        var active = firstDevice.Active.ToArray(); var store = firstDevice.Store; var token = (ushort)0;
        if (failure == "slot") store = store with { ActiveSlot = ConfigStoreContract.NextSlot(store.ActiveSlot) };
        if (failure == "sequence") store = store with { ActiveSequence = store.ActiveSequence + 1 };
        if (failure == "revision") store = store with { CurrentRevision = store.CurrentRevision + 1, SavedRevision = store.SavedRevision + 1 };
        if (failure == "active") active[0]++;
        if (failure == "mailbox") token = 9;
        var rebooted = new FakePersistenceDevice(active, store, token);
        var result = await Service(rebooted, clock).ResumeAfterManualRebootAsync(path);
        Assert.Equal(PersistencePhase.ResultUncertain, result.Phase); Assert.Equal(0, rebooted.SaveCalls);
    }

    [Theory]
    [InlineData("slot")] [InlineData("sequence")] [InlineData("revision")] [InlineData("active")] [InlineData("mailbox")]
    public async Task SecondRebootMetadataMismatchCannotComplete(string failure)
    {
        using var files = new TempFiles(); var path = files.Path("j.json"); var clock = new FakeClock();
        var a = new FakePersistenceDevice(Active(3), Store(), 0); await Service(a, clock).StartAsync(path, Guid.NewGuid().ToString(), ClientCommit);
        var b = FakePersistenceDevice.AfterReboot(a); await Service(b, clock).ResumeAfterManualRebootAsync(path);
        var badStore = b.Store; var active = b.Active.ToArray(); var token = (ushort)0;
        if (failure == "slot") badStore = badStore with { ActiveSlot = ConfigStoreContract.NextSlot(badStore.ActiveSlot) };
        if (failure == "sequence") badStore = badStore with { ActiveSequence = badStore.ActiveSequence + 1 };
        if (failure == "revision") badStore = badStore with { CurrentRevision = badStore.CurrentRevision + 1, SavedRevision = badStore.SavedRevision + 1 };
        if (failure == "active") active[0]++;
        if (failure == "mailbox") token = 8;
        var c = new FakePersistenceDevice(active, badStore, token); var result = await Service(c, clock).ResumeAfterManualRebootAsync(path);
        Assert.Equal(PersistencePhase.ResultUncertain, result.Phase); Assert.Equal(0, c.SaveCalls);
    }

    [Fact] public async Task PollDisconnectTimesOutUsingVirtualClock()
    {
        using var files = new TempFiles(); var clock = new FakeClock();
        var device = new FakePersistenceDevice(Active(3), Store(), 0) { PollError = new IOException("offline") };
        var journal = await Service(device, clock).StartAsync(files.Path("j.json"), Guid.NewGuid().ToString(), ClientCommit);
        Assert.Equal(PersistencePhase.ResultUncertain, journal.Phase); Assert.Contains("timeout", journal.ResultUncertainReason, StringComparison.OrdinalIgnoreCase); Assert.True(clock.DelayCount > 0);
    }

    [Fact] public async Task CancellationAfterSaveIsPersistedAsUncertain()
    {
        using var files = new TempFiles(); using var cancellation = new CancellationTokenSource();
        var clock = new FakeClock(); clock.BeforeDelay = () => { if (clock.DelayCount >= 2) cancellation.Cancel(); };
        var device = new FakePersistenceDevice(Active(3), Store(), 0) { NeverComplete = true };
        var journal = await Service(device, clock).StartAsync(files.Path("j.json"), Guid.NewGuid().ToString(), ClientCommit, cancellation.Token);
        Assert.Equal(PersistencePhase.ResultUncertain, journal.Phase); Assert.Equal(1, journal.ReservedSaveCount); Assert.Equal(1, device.SaveCalls);
    }

    [Fact] public async Task UncertainJournalCannotResumeOrReleaseBudget()
    {
        using var files = new TempFiles(); var path = files.Path("j.json"); var device = new FakePersistenceDevice(Active(3), Store(), 0) { SaveError = new IOException("lost") };
        var first = await Service(device).StartAsync(path, Guid.NewGuid().ToString(), ClientCommit);
        var other = FakePersistenceDevice.AfterReboot(device); var resumed = await Service(other).ResumeAfterManualRebootAsync(path);
        Assert.Equal(PersistencePhase.ResultUncertain, resumed.Phase); Assert.Equal(1, resumed.ReservedSaveCount); Assert.Equal(0, other.SaveCalls);
    }

    [Fact] public async Task ReservedSaveRecoveryCanConfirmButNeverRetransmits()
    {
        using var files = new TempFiles(); var path = files.Path("j.json");
        var before = Store(dirty: true, current: 11, saved: 10, slot: 1, sequence: 7);
        var initial = Journal();
        var journal = initial with { Phase = PersistencePhase.TestValueSaveRequested, AuthorizationStage = PersistenceAuthorizationStage.CycleAWrites, ReservedSaveCount = 1, SaveTokens = [7], LastMailboxToken = 7, SaveBefore = before,
            CycleAEvidence = initial.CycleAEvidence with { SaveBefore = before, SaveToken = 7, SaveReservedAtUtc = initial.UpdatedAt, SaveRequestMayHaveBeenSent = true, MailboxTokens = [7] } };
        await PersistenceJournalStore.WriteAtomicAsync(path, journal);
        var device = new FakePersistenceDevice(Active(4), Store(current: 11, saved: 11, slot: 2, sequence: 8), 7);
        var recovered = await Service(device).ResumeAfterManualRebootAsync(path);
        Assert.Equal(PersistencePhase.WaitingForFirstReboot, recovered.Phase); Assert.Equal(0, device.SaveCalls); Assert.Equal(1, recovered.ReservedSaveCount);
    }

    [Fact] public async Task ReservedSaveConflictBecomesUncertainWithoutRetransmit()
    {
        using var files = new TempFiles(); var path = files.Path("j.json");
        var initial = Journal(); var before = Store(dirty: true, current: 11, saved: 10);
        var journal = initial with { Phase = PersistencePhase.TestValueSaveRequested, AuthorizationStage = PersistenceAuthorizationStage.CycleAWrites, ReservedSaveCount = 1, SaveTokens = [7], SaveBefore = before,
            CycleAEvidence = initial.CycleAEvidence with { SaveBefore = before, SaveToken = 7, SaveReservedAtUtc = initial.UpdatedAt, SaveRequestMayHaveBeenSent = true, MailboxTokens = [7] } };
        await PersistenceJournalStore.WriteAtomicAsync(path, journal);
        var device = new FakePersistenceDevice(Active(4), Store(dirty: true, current: 11, saved: 10), 7);
        var recovered = await Service(device).ResumeAfterManualRebootAsync(path);
        Assert.Equal(PersistencePhase.ResultUncertain, recovered.Phase); Assert.Equal(0, device.SaveCalls);
    }

    [Fact] public async Task TimeoutDoesNotTreatDirtyOrRevisionAloneAsSuccess()
    {
        using var files = new TempFiles(); var device = new FakePersistenceDevice(Active(3), Store(), 0) { NeverComplete = true };
        var journal = await Service(device, new FakeClock()).StartAsync(files.Path("j.json"), Guid.NewGuid().ToString(), ClientCommit);
        Assert.Equal(PersistencePhase.ResultUncertain, journal.Phase); Assert.Equal(1, device.SaveCalls);
    }

    [Fact] public async Task JournalRoundTripPreservesHistoryAndSchema()
    {
        using var files = new TempFiles(); var path = files.Path("j.json"); var journal = Journal();
        await PersistenceJournalStore.WriteAtomicAsync(path, journal); var loaded = await PersistenceJournalStore.ReadAsync(path);
        Assert.Equal(PersistenceJournal.CurrentSchemaVersion, loaded.SchemaVersion); Assert.Equal(journal.Events, loaded.Events); Assert.Equal(64, loaded.OriginalActiveConfiguration.Length);
    }

    [Fact] public async Task AtomicJournalFailureKeepsOldJournal()
    {
        using var files = new TempFiles(); var path = files.Path("j.json"); var original = Journal();
        await PersistenceJournalStore.WriteAtomicAsync(path, original);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PersistenceJournalStore.WriteAtomicAsync(path, original with { UpdatedAt = original.UpdatedAt.AddSeconds(1) }, cancellation.Token));
        Assert.Equal(original.UpdatedAt, (await PersistenceJournalStore.ReadAsync(path)).UpdatedAt);
    }

    [Fact] public async Task CorruptOrIncompatibleJournalIsBlocked()
    {
        using var files = new TempFiles(); var corrupt = files.Path("bad.json"); await File.WriteAllTextAsync(corrupt, "{");
        await Assert.ThrowsAsync<InvalidDataException>(() => PersistenceJournalStore.ReadAsync(corrupt));
        var incompatible = files.Path("old.json"); await File.WriteAllTextAsync(incompatible, JsonSerializer.Serialize(Journal() with { SchemaVersion = 1 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => PersistenceJournalStore.ReadAsync(incompatible));
    }

    [Fact] public void JournalRejectsRepeatedSaveTokensAndBudgetConflict()
    {
        Assert.Throws<InvalidDataException>(() => PersistenceJournalStore.Validate(Journal() with { ReservedSaveCount = 2, SaveTokens = [7, 7] }));
        Assert.Throws<InvalidDataException>(() => PersistenceJournalStore.Validate(Journal() with { SaveBudget = 3 }));
    }

    [Fact] public async Task SyntacticallyValidButContradictoryJournalIsRejected()
    {
        using var files = new TempFiles(); var path = files.Path("contradictory.json");
        var contradictory = Journal() with { Phase = PersistencePhase.WaitingForFirstReboot, WaitingForFirstReboot = false };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(contradictory));
        await Assert.ThrowsAsync<InvalidDataException>(() => PersistenceJournalStore.ReadAsync(path));
    }

    [Fact] public async Task NullJournalArraysProduceSafeValidationError()
    {
        using var files = new TempFiles(); var path = files.Path("null.json");
        var json = JsonSerializer.Serialize(Journal()).Replace("\"SaveTokens\":[]", "\"SaveTokens\":null");
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => PersistenceJournalStore.ReadAsync(path));
    }

    [Fact] public void CycleBCannotExistWithoutFirstRebootEvidence()
    {
        var journal = Journal() with { Cycle = PersistenceCycle.B, CycleBEvidence = new PersistenceCycleEvidence { Cycle = PersistenceCycle.B, ExpectedActiveConfiguration = Active(3) } };
        Assert.Throws<InvalidDataException>(() => PersistenceJournalStore.Validate(journal));
    }

    [Fact] public async Task ProductionJournalDirectoryMustMatchWorkflowId()
    {
        using var files = new TempFiles(); var directory = System.IO.Path.Combine(files.Root, "20260907T000000000Z_" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => PersistenceJournalStore.WriteAtomicAsync(System.IO.Path.Combine(directory, "persistence-journal.json"), Journal()));
    }

    [Fact] public void EvidenceDirectoriesAreUniqueAndNeverOverwrite()
    {
        using var files = new TempFiles(); var id1 = Guid.NewGuid().ToString(); var id2 = Guid.NewGuid().ToString(); var now = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        var first = PersistenceEvidenceDirectory.CreateUnique(files.Root, id1, now); var second = PersistenceEvidenceDirectory.CreateUnique(files.Root, id2, now);
        Assert.NotEqual(first, second); Assert.True(Directory.Exists(first)); Assert.True(Directory.Exists(second));
        Assert.Throws<IOException>(() => PersistenceEvidenceDirectory.CreateUnique(files.Root, id1, now));
    }

    [Fact] public async Task UnauthorizedHardwareEntryNeverInvokesConnectionFactory()
    {
        var calls = 0; var exit = await PersistenceHardwareAuthorizationGate.ExecuteAfterAuthorizationAsync(["persistence-brightness-cycle"], _ => { calls++; return Task.FromResult(0); });
        Assert.Equal(PersistenceHardwareAuthorizationGate.DeniedExitCode, exit); Assert.Equal(0, calls);
    }

    [Fact] public async Task ActualPersistenceRunnerDenialDoesNotCreateMonitoringService()
    {
        var calls = 0;
        var exit = await PersistenceHardwareRunner.RunAsync(["persistence-brightness-cycle"], () => { calls++; return new InstrumentMonitoringService(); });
        Assert.Equal(PersistenceHardwareAuthorizationGate.DeniedExitCode, exit); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("--address")] [InlineData("--brightness")] [InlineData("--save-budget")]
    [InlineData("--force")] [InlineData("--skip-restore")]
    public void PersistenceAuthorizationRejectsUnsafeOptions(string option)
    {
        var args = AuthorizedArgs().Append(option).Append("1").ToArray(); Assert.False(PersistenceHardwareAuthorizationGate.Validate(args).Authorized);
    }

    [Fact] public async Task CompleteAuthorizationInvokesFactoryOnlyAfterValidation()
    {
        var calls = 0; var exit = await PersistenceHardwareAuthorizationGate.ExecuteAfterAuthorizationAsync(AuthorizedArgs(), _ => { calls++; return Task.FromResult(27); });
        Assert.Equal(27, exit); Assert.Equal(1, calls);
    }

    [Fact] public async Task StrictPreflightReportsActualFc03Trace()
    {
        var access = new FakePreflightAccess(); var report = await new StrictPreflightService(access, Baseline(), new FakeClock()).RunAsync();
        Assert.True(report.Passed, string.Join(" ", report.FailureReasons));
        Assert.Equal(access.Trace.Count, report.Requests.Fc03Attempted); Assert.Equal(22, report.Requests.Fc03Attempted); Assert.Equal(22, report.Requests.Fc03Succeeded);
        Assert.All(access.Trace, item => Assert.True(item.Succeeded));
    }

    [Fact] public async Task StrictPreflightRunnerSeparatesSuccessfulConnectionStatistics()
    {
        using var files = new TempFiles(); var access = new FakePreflightAccess();
        var exit = await StrictPreflightRunner.RunAsync(["preflight", "--output-root", files.Root], _ => access);
        Assert.Equal(0, exit); var directory = Assert.Single(Directory.GetDirectories(files.Root));
        var summary = AtomicJsonFile.Read<PreflightSummary>(System.IO.Path.Combine(directory, "preflight-summary.json"));
        Assert.Equal(new PreflightConnectionStatistics(1, 1, 0, 1, 0), summary.Connections);
        Assert.Equal(summary.Requests.Fc03Attempted, AtomicJsonFile.Read<PreflightRequestTrace[]>(System.IO.Path.Combine(directory, "request-trace.json")).Length);
    }

    [Fact] public async Task StrictPreflightRunnerConnectionFailureCannotReportSuccessOrRetry()
    {
        using var files = new TempFiles(); var access = new FakePreflightAccess { OpenError = new IOException("offline") };
        var exit = await StrictPreflightRunner.RunAsync(["preflight", "--output-root", files.Root], _ => access);
        Assert.Equal(6, exit); var directory = Assert.Single(Directory.GetDirectories(files.Root));
        var summary = AtomicJsonFile.Read<PreflightSummary>(System.IO.Path.Combine(directory, "preflight-summary.json"));
        Assert.Equal(new PreflightConnectionStatistics(1, 0, 1, 0, 0), summary.Connections);
        Assert.Equal("FAIL", summary.FinalStatus); Assert.Equal(1, summary.Errors.TransportErrors);
    }

    [Theory]
    [InlineData("identity")] [InlineData("mailbox")] [InlineData("mirrors")] [InlineData("errors")] [InlineData("writes")] [InlineData("baseline")]
    public async Task StrictPreflightRejectsUnsafeState(string failure)
    {
        var access = new FakePreflightAccess { Failure = failure }; var report = await new StrictPreflightService(access, Baseline(), new FakeClock()).RunAsync(); Assert.False(report.Passed);
    }

    [Fact] public async Task FailedFc03IsPreservedInTrace()
    {
        var access = new FakePreflightAccess { Failure = "read" };
        await Assert.ThrowsAsync<IOException>(() => new StrictPreflightService(access, Baseline(), new FakeClock()).RunAsync());
        Assert.Contains(access.Trace, item => item.FunctionCode == 3 && !item.Succeeded && item.ExceptionCategory == "IOException");
    }

    [Fact] public async Task FreshSampleSequenceMustAdvanceUsingVirtualTime()
    {
        var access = new FakePreflightAccess { Failure = "freshness" }; var clock = new FakeClock();
        await Assert.ThrowsAsync<TimeoutException>(() => new StrictPreflightService(access, Baseline(), clock,
            new FreshSnapshotPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))).RunAsync());
        Assert.True(clock.DelayCount > 0); Assert.All(access.Trace, item => Assert.Equal((byte)3, item.FunctionCode));
    }

    [Fact] public async Task FreshSampleWaitTimeIsRecordedFromVirtualClock()
    {
        var access = new FakePreflightAccess { Failure = "freshness-delay" }; var clock = new FakeClock();
        var report = await new StrictPreflightService(access, Baseline(), clock,
            new FreshSnapshotPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3))).RunAsync();
        Assert.True(report.Passed); Assert.Equal(1000, report.Timing.FreshnessWaitMs); Assert.Equal(1U, report.Timing.InitialSampleSequence); Assert.Equal(2U, report.Timing.FinalSampleSequence);
    }

    [Fact] public async Task TransientConfigStoreMirrorMismatchUsesReadOnlyResampling()
    {
        using var files = new TempFiles(); var device = new FakePersistenceDevice(Active(3), Store(), 0);
        var completed = Store(current: 11, saved: 11, slot: 2, sequence: 8);
        device.PostSaveSnapshots.Enqueue(Store(state2: ConfigStoreState.Complete, dirty: true, current: 11, saved: 10));
        device.PostSaveSnapshots.Enqueue(completed); device.PostSaveSnapshots.Enqueue(completed);
        var journal = await Service(device).StartAsync(files.Path("j.json"), Guid.NewGuid().ToString(), ClientCommit);
        Assert.Equal(PersistencePhase.WaitingForFirstReboot, journal.Phase); Assert.Equal(1, device.SaveCalls);
        Assert.Contains(journal.CycleAEvidence.PollSnapshots, item => !item.Snapshot.StatesConsistent);
    }

    [Fact] public async Task PersistentConfigStoreMirrorMismatchBecomesUncertainWithoutWriteRetry()
    {
        using var files = new TempFiles(); var device = new FakePersistenceDevice(Active(3), Store(), 0);
        var mismatch = Store(state2: ConfigStoreState.Complete, dirty: true, current: 11, saved: 10);
        device.PostSaveSnapshots.Enqueue(mismatch); device.PostSaveSnapshots.Enqueue(mismatch); device.PostSaveSnapshots.Enqueue(mismatch);
        var journal = await Service(device).StartAsync(files.Path("j.json"), Guid.NewGuid().ToString(), ClientCommit);
        Assert.Equal(PersistencePhase.ResultUncertain, journal.Phase); Assert.Equal(1, device.SaveCalls);
    }

    private static ConfigurationPersistenceService Service(FakePersistenceDevice device, FakeClock? clock = null) =>
        new(device, Safety(Store()), clock ?? new FakeClock(), new PersistencePollingPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
            new ConfigStoreSamplingPolicy(3, 2, TimeSpan.FromMilliseconds(1)));

    private static ushort[] Active(ushort brightness)
    {
        var values = Baseline().Manifest.ActiveRegisters.ToArray(); values[22] = brightness; return values;
    }

    private static ConfigStoreSnapshot Store(ConfigStoreState state1 = ConfigStoreState.Idle, ConfigStoreState state2 = ConfigStoreState.Idle,
        bool dirty = false, uint current = 10, uint saved = 10, ushort slot = 1, uint sequence = 7) =>
        new((ushort)state1, (ushort)state2, state1, state2, dirty, current, saved, 2, slot, sequence);

    private static PersistenceJournal Journal()
    {
        var now = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        return new PersistenceJournal { WorkflowId = Guid.NewGuid().ToString(), ClientCommit = ClientCommit, Stm32Commit = ConfigurationPersistenceService.FixedStm32Commit,
            BaselineId = Baseline().Manifest.BaselineId, BaselineSha256 = Baseline().Manifest.ActiveArraySha256,
            BaselineManifestSha256 = Baseline().ManifestSha256, BoundPreflightWorkflowId = PreflightWorkflowId,
            PreflightSummarySha256 = Hash64,
            CreatedAt = now, UpdatedAt = now, Phase = PersistencePhase.PreflightComplete, Cycle = PersistenceCycle.A,
            AuthorizationStage = PersistenceAuthorizationStage.BoundPreflight,
            OriginalActiveConfiguration = Active(3), ExpectedActiveConfiguration = Active(4), SaveTokens = [], PollSnapshots = [],
            Events = [new(now, PersistenceEventKind.Information, "created")],
            CycleAEvidence = new PersistenceCycleEvidence { Cycle = PersistenceCycle.A, ExpectedActiveConfiguration = Active(4), ActiveBeforeApply = Active(3) } };
    }

    private static string[] AuthorizedArgs() => ["persistence-brightness-cycle", "--authorize-stage2b-persistence", "--confirmation",
        "A33_STAGE2B_BRIGHTNESS_3_TO_4_TO_3_TWO_SAVES", "--acknowledge-manual-reboots", "--acknowledge-result-uncertain-lockout",
        "--preflight-workflow-id", PreflightWorkflowId];

    private const string PreflightWorkflowId = "11111111-1111-1111-1111-111111111111";
    private const string Hash64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static TrustedPersistenceBaseline? baseline;
    private static TrustedPersistenceBaseline Baseline()
    {
        if (baseline is not null) return baseline;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, PersistenceBaselineContract.RelativeManifestPath))) directory = directory.Parent;
        return baseline = PersistenceBaselineContract.LoadFromRepository(directory?.FullName ?? throw new DirectoryNotFoundException());
    }

    private static PersistenceSafetyContext Safety(ConfigStoreSnapshot store)
    {
        var now = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        var requests = new PreflightRequestStatistics(1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var errors = new PreflightErrorCounters(0, 0, 0, 0, 0, 0, 0, 0);
        var summary = new PreflightSummary(1, PreflightWorkflowId, ClientCommit, "1.0.0", Hash64,
            ConfigurationPersistenceService.FixedStm32Commit, Baseline().Manifest.BaselineId,
            Baseline().Manifest.ActiveArraySha256, Baseline().ManifestSha256, now, now, 0, 0, 1, 2,
            "192.168.1.100:502", 1, new DeviceIdentity(0x050A, 2, 0x0104, 1), new(1, 1, 0, 1, 0), requests, errors,
            new Dictionary<string, bool> { ["all"] = true }, "PASS", [], new Dictionary<string, string>());
        var binding = new ValidatedPreflightBinding(PreflightWorkflowId, "summary", Hash64, summary, store,
            new MailboxSnapshot(0, 0, 0, 0, new ushort[12]), Active(3));
        return new PersistenceSafetyContext(Baseline(), binding, ClientCommit);
    }

    private sealed class FakeClock : IPersistenceClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        public int DelayCount { get; private set; }
        public Action? BeforeDelay { get; set; }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) { BeforeDelay?.Invoke(); cancellationToken.ThrowIfCancellationRequested(); UtcNow += delay; DelayCount++; return Task.CompletedTask; }
    }

    private sealed class FakePersistenceDevice(ushort[] active, ConfigStoreSnapshot store, ushort mailboxToken) : IConfigurationPersistenceDevice
    {
        private bool saveSent;
        private bool saveCompleted;
        public ushort[] Active { get; } = active.ToArray();
        public ConfigStoreSnapshot Store { get; private set; } = store;
        public ushort MailboxToken { get; private set; } = mailboxToken;
        public int SaveCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public Exception? ApplyError { get; init; }
        public Exception? SaveError { get; init; }
        public Exception? PollError { get; init; }
        public ushort SaveResultCode { get; init; } = 1;
        public Action? BeforeSave { get; init; }
        public bool NeverComplete { get; init; }
        public ushort MailboxState { get; init; }
        public Queue<ConfigStoreSnapshot> PostSaveSnapshots { get; } = [];
        public WordOrder WordOrder => WordOrder.HighWordFirst;

        public static FakePersistenceDevice AfterReboot(FakePersistenceDevice source) => new(source.Active, source.Store with
        { StateMirror1Raw = 0, StateMirror2Raw = 0, StateMirror1 = ConfigStoreState.Idle, StateMirror2 = ConfigStoreState.Idle }, 0);
        public Task<DeviceIdentity> ReadIdentityAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DeviceIdentity(0x050A, 2, 0x0104, 1));
        public Task<MailboxSnapshot> ReadMailboxAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MailboxSnapshot(MailboxToken, 0, MailboxState, 0, new ushort[12]));
        public Task<ushort[]> ReadActiveConfigurationAsync(CancellationToken cancellationToken = default) => Task.FromResult(Active.ToArray());
        public Task<ConfigStoreSnapshot> ReadConfigStoreAsync(CancellationToken cancellationToken = default)
        {
            if (saveSent && PollError is not null) throw PollError;
            if (saveSent && PostSaveSnapshots.Count > 0) { Store = PostSaveSnapshots.Dequeue(); return Task.FromResult(Store); }
            if (saveSent && !NeverComplete && !saveCompleted)
            {
                Store = Store with { ConfigDirty = false, SavedRevision = Store.CurrentRevision, ActiveSlot = ConfigStoreContract.NextSlot(Store.ActiveSlot), ActiveSequence = ConfigStoreContract.NextSequence(Store.ActiveSequence) };
                saveCompleted = true;
            }
            return Task.FromResult(Store);
        }
        public Task ApplyBrightnessRamAsync(ushort brightness, MailboxTokenAllocator tokens, CancellationToken cancellationToken = default)
        {
            if (ApplyError is not null) throw ApplyError;
            ApplyCalls++;
            _ = tokens.Allocate(); _ = tokens.Allocate(); _ = tokens.Allocate();
            Active[22] = brightness; Store = Store with { ConfigDirty = true, CurrentRevision = Store.CurrentRevision + 1 }; return Task.CompletedTask;
        }
        public Task<MailboxCommandReceipt> SendSaveOnceAsync(ushort token, CancellationToken cancellationToken = default)
        {
            BeforeSave?.Invoke(); SaveCalls++; saveSent = true; MailboxToken = token; if (SaveError is not null) throw SaveError; return Task.FromResult(new MailboxCommandReceipt(token, 13, SaveResultCode));
        }
        public Task<ushort[]> ReadHoldingAsync(ushort address, ushort count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakePreflightAccess : IStrictPreflightSession
    {
        private readonly List<PreflightRequestTrace> trace = [];
        private int sequenceReads;
        private bool writeInjected;
        private int connectionErrors;
        public string? Failure { get; init; }
        public Exception? OpenError { get; init; }
        public bool IsOpen { get; private set; }
        public WordOrder WordOrder { get; set; } = WordOrder.HighWordFirst;
        public byte UnitId => 1;
        public IReadOnlyList<PreflightRequestTrace> Trace
        {
            get
            {
                if (Failure == "writes" && !writeInjected)
                {
                    writeInjected = true; var at = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
                    trace.Add(new(trace.Count + 1, at, at, 0, 16, 0x0040, 12, "AA", "BB", 12, true, null, null, PreflightReadPurpose.Mailbox, 13));
                }
                return trace;
            }
        }
        public PreflightErrorCounters Errors => Failure == "errors" ? new(1, 0, 0, 0, 0, 0, 0, connectionErrors) : new(0, 0, 0, 0, 0, 0, 0, connectionErrors);
        public Task OpenAsync(CancellationToken cancellationToken) { if (OpenError is not null) throw OpenError; IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }
        public void RecordConnectionError(Exception error) { connectionErrors++; }
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
        public Task<ushort[]> ReadHoldingAsync(ushort address, ushort count, CancellationToken cancellationToken = default) =>
            ReadForPreflightAsync(address, count, PreflightReadPurpose.ConfigStore, cancellationToken);
        public Task<ushort[]> ReadForPreflightAsync(ushort address, ushort count, PreflightReadPurpose purpose, CancellationToken cancellationToken = default)
        {
            var at = DateTimeOffset.Parse("2026-09-07T00:00:00Z").AddMilliseconds(trace.Count);
            if (Failure == "read" && address == 0x0100)
            {
                trace.Add(new(trace.Count + 1, at, at, 0, 3, address, count, "0301000010", "", 0, false, "IOException", null, purpose, null));
                throw new IOException("planned read failure");
            }
            var values = new ushort[count];
            if (address == 0x0103) values[0] = 0;
            else if (address == 14) { values[0] = Failure == "identity" ? (ushort)0x0103 : (ushort)0x0104; values[1] = 0x050A; }
            else if (address == 0x0020)
            {
                sequenceReads++; values[0] = 0;
                values[1] = (ushort)(Failure == "freshness" || (Failure == "freshness-delay" && sequenceReads <= 3) || (Failure != "freshness-delay" && sequenceReads == 1) ? 1 : 2);
            }
            else if (address is 0x0100 or 0x0110 or 0x0120 or 0x0130 or 0x0140 or 0x0150 or 0x0160 or 0x0170)
            {
                var full = Active(Failure == "baseline" && address < 0x0140 ? (ushort)4 : (ushort)3);
                Array.Copy(full, address % 0x40, values, 0, count);
            }
            else if (address == 0x004C && Failure == "mailbox") values[2] = 1;
            else if (address == 0x0030) { values[0] = 0; values[2] = 0; values[3] = 0; values[4] = 10; values[5] = 0; values[6] = 10; }
            else if (address == 0x01C0) { values[0] = 2; values[1] = 1; values[2] = 0; values[3] = 7; values[4] = Failure == "mirrors" ? (ushort)8 : (ushort)0; }
            trace.Add(new(trace.Count + 1, at, at, 0, 3, address, count, "03", "03", values.Length, true, null, null, purpose, null));
            return Task.FromResult(values);
        }
    }

    private sealed class TempFiles : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "a33-tests-" + Guid.NewGuid().ToString("N"));
        public TempFiles() => Directory.CreateDirectory(Root);
        public string Path(string name) => System.IO.Path.Combine(Root, name);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
