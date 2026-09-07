namespace A33.Instrument.Core;

public sealed record FinalStabilityPolicy(TimeSpan Duration, TimeSpan Interval)
{
    public static FinalStabilityPolicy Fixed600Seconds { get; } = new(TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(1));
}

public sealed record FinalPersistenceExpectation(ushort ActiveSlot,uint ActiveSequence,uint CurrentRevision,uint SavedRevision,ushort MailboxResponseToken,ushort MailboxResultCode,ushort MailboxCommandState,ushort MailboxLastCommandId)
{
    public static FinalPersistenceExpectation FromCycleB(PersistenceJournal journal)
    {
        var reboot=journal.CycleBEvidence?.RebootEvidence??throw new InvalidDataException("Cycle B second-reboot evidence is missing.");
        if(journal.Phase!=PersistencePhase.Complete||journal.FinalConfigurationMatches64Of64!=true)throw new InvalidDataException("Journal is not complete.");
        return new(reboot.ConfigStore.ActiveSlot,reboot.ConfigStore.ActiveSequence,reboot.ConfigStore.CurrentRevision,reboot.ConfigStore.SavedRevision,
            reboot.Mailbox.ResponseToken,reboot.Mailbox.ResultCode,reboot.Mailbox.CommandState,reboot.Mailbox.LastCommandId);
    }
}
public sealed record FinalStabilityFailure(DateTimeOffset CapturedAtUtc,string Field,string Expected,string Actual);
public sealed record FinalStabilitySample(DateTimeOffset CapturedAtUtc,ushort[] ActiveRegisters,string ActiveSha256,ushort? Brightness,ConfigStoreSnapshot? ConfigStore,MailboxSnapshot? Mailbox)
{
    public int ActiveRegisterCount => ActiveRegisters?.Length ?? 0;
}
public sealed record FinalStabilityReport(DateTimeOffset StartedAtUtc,DateTimeOffset CompletedAtUtc,double DurationSeconds,bool Passed,FinalPersistenceExpectation Expectation,FinalStabilityFailure[] Failures,FinalStabilitySample[] Samples);
public static class FinalStabilityReportValidator
{
    public static void ValidatePass(FinalStabilityReport report,TrustedPersistenceBaseline baseline)
    {
        if(!report.Passed||report.Expectation is null||report.Expectation.ActiveSlot is not(1 or 2)||report.Expectation.CurrentRevision!=report.Expectation.SavedRevision||
            report.Expectation.MailboxCommandState!=0||report.Failures is null||report.Failures.Length!=0||report.DurationSeconds<600||report.CompletedAtUtc<report.StartedAtUtc||report.Samples is null||report.Samples.Length<2||
            report.StartedAtUtc.Offset!=TimeSpan.Zero||report.CompletedAtUtc.Offset!=TimeSpan.Zero||
            Math.Abs((report.CompletedAtUtc-report.StartedAtUtc).TotalSeconds-report.DurationSeconds)>0.01)
            throw new InvalidDataException("Final stability report duration or result is invalid.");
        DateTimeOffset? previous=null;
        foreach(var sample in report.Samples)
        {
            if(sample is null||sample.ActiveRegisters is null||sample.ActiveRegisterCount!=64||!sample.ActiveRegisters.SequenceEqual(baseline.Manifest.ActiveRegisters)||
                sample.ActiveSha256!=baseline.Manifest.ActiveArraySha256||sample.Brightness!=ConfigurationPersistenceService.OriginalBrightness||
                sample.CapturedAtUtc.Offset!=TimeSpan.Zero||sample.CapturedAtUtc<report.StartedAtUtc||sample.CapturedAtUtc>report.CompletedAtUtc||
                (previous.HasValue&&(sample.CapturedAtUtc<previous.Value||sample.CapturedAtUtc-previous.Value>TimeSpan.FromSeconds(2)))||
                sample.Mailbox is null||sample.Mailbox.Busy||sample.Mailbox.Pending||sample.ConfigStore is null||!sample.ConfigStore.StatesKnown||
                !sample.ConfigStore.StatesConsistent||sample.ConfigStore.State!=ConfigStoreState.Idle||sample.ConfigStore.ConfigDirty||
                sample.ConfigStore.CurrentRevision!=report.Expectation.CurrentRevision||sample.ConfigStore.SavedRevision!=report.Expectation.SavedRevision||
                sample.ConfigStore.ActiveSlot!=report.Expectation.ActiveSlot||sample.ConfigStore.ActiveSequence!=report.Expectation.ActiveSequence||
                sample.Mailbox.ResponseToken!=report.Expectation.MailboxResponseToken||sample.Mailbox.ResultCode!=report.Expectation.MailboxResultCode||
                sample.Mailbox.CommandState!=report.Expectation.MailboxCommandState||sample.Mailbox.LastCommandId!=report.Expectation.MailboxLastCommandId)
                throw new InvalidDataException("Final stability report contains an invalid sample.");
            previous=sample.CapturedAtUtc;
        }
        if(report.Samples[0].CapturedAtUtc-report.StartedAtUtc>TimeSpan.FromSeconds(2)||report.Samples[^1].CapturedAtUtc-report.StartedAtUtc<TimeSpan.FromSeconds(600))
            throw new InvalidDataException("Final stability samples do not cover the full fixed-rate window.");
    }
}

public sealed class FinalPersistenceStabilityService(
    IConfigurationPersistenceDevice device, TrustedPersistenceBaseline baseline,
    FinalPersistenceExpectation expectation,IPersistenceClock? clock = null, FinalStabilityPolicy? policy = null)
{
    private readonly IPersistenceClock clock=clock??new SystemPersistenceClock();
    private readonly FinalStabilityPolicy policy=policy??FinalStabilityPolicy.Fixed600Seconds;

    public async Task<FinalStabilityReport> RunAsync(CancellationToken cancellationToken=default)
    {
        var started=clock.UtcNow;var deadline=started+policy.Duration;var samples=new List<FinalStabilitySample>();var failures=new List<FinalStabilityFailure>();
        while(true)
        {
            ushort[] active;ConfigStoreSnapshot store;MailboxSnapshot mailbox;
            try{active=await device.ReadActiveConfigurationAsync(cancellationToken);store=await device.ReadConfigStoreAsync(cancellationToken);mailbox=await device.ReadMailboxAsync(cancellationToken);}
            catch(Exception error){failures.Add(new(clock.UtcNow,"read_exception","none",$"{error.GetType().Name}: {error.Message}"));return new(started,clock.UtcNow,(clock.UtcNow-started).TotalSeconds,false,expectation,failures.ToArray(),samples.ToArray());}
            var hash=active.Length==64?PersistenceBaselineContract.ComputeActiveSha256(active):"";
            var brightness=active.Length>ConfigurationPersistenceService.BrightnessOffset?active[ConfigurationPersistenceService.BrightnessOffset]:(ushort?)null;
            var sample=new FinalStabilitySample(clock.UtcNow,active.ToArray(),hash,brightness,store,mailbox);
            samples.Add(sample);
            Check(failures,sample.CapturedAtUtc,"active_count","64",active.Length.ToString());
            Check(failures,sample.CapturedAtUtc,"active_configuration","True",active.SequenceEqual(baseline.Manifest.ActiveRegisters).ToString());
            Check(failures,sample.CapturedAtUtc,"active_sha256",baseline.Manifest.ActiveArraySha256,hash);
            Check(failures,sample.CapturedAtUtc,"brightness","3",brightness?.ToString()??"missing");
            Check(failures,sample.CapturedAtUtc,"mailbox_busy","False",mailbox.Busy.ToString());Check(failures,sample.CapturedAtUtc,"mailbox_pending","False",mailbox.Pending.ToString());
            Check(failures,sample.CapturedAtUtc,"mailbox_token",expectation.MailboxResponseToken.ToString(),mailbox.ResponseToken.ToString());
            Check(failures,sample.CapturedAtUtc,"mailbox_result",expectation.MailboxResultCode.ToString(),mailbox.ResultCode.ToString());
            Check(failures,sample.CapturedAtUtc,"mailbox_state",expectation.MailboxCommandState.ToString(),mailbox.CommandState.ToString());
            Check(failures,sample.CapturedAtUtc,"mailbox_last_command",expectation.MailboxLastCommandId.ToString(),mailbox.LastCommandId.ToString());
            Check(failures,sample.CapturedAtUtc,"config_store_state","Idle",store.State?.ToString()??"unknown_or_inconsistent");
            Check(failures,sample.CapturedAtUtc,"dirty","False",store.ConfigDirty.ToString());
            Check(failures,sample.CapturedAtUtc,"active_slot",expectation.ActiveSlot.ToString(),store.ActiveSlot.ToString());
            Check(failures,sample.CapturedAtUtc,"active_sequence",expectation.ActiveSequence.ToString(),store.ActiveSequence.ToString());
            Check(failures,sample.CapturedAtUtc,"current_revision",expectation.CurrentRevision.ToString(),store.CurrentRevision.ToString());
            Check(failures,sample.CapturedAtUtc,"saved_revision",expectation.SavedRevision.ToString(),store.SavedRevision.ToString());
            if(failures.Count>0)return new(started,clock.UtcNow,(clock.UtcNow-started).TotalSeconds,false,expectation,failures.ToArray(),samples.ToArray());
            if(clock.UtcNow>=deadline)break;
            await clock.DelayAsync(policy.Interval,cancellationToken);
        }
        return new(started,clock.UtcNow,(clock.UtcNow-started).TotalSeconds,true,expectation,[],samples.ToArray());
    }
    private static void Check(List<FinalStabilityFailure> failures,DateTimeOffset at,string field,string expected,string actual){if(!string.Equals(expected,actual,StringComparison.Ordinal))failures.Add(new(at,field,expected,actual));}
}
