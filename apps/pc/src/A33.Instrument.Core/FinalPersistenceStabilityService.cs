namespace A33.Instrument.Core;

public sealed record FinalStabilityPolicy(TimeSpan Duration, TimeSpan Interval)
{
    public static FinalStabilityPolicy Fixed600Seconds { get; } = new(TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(1));
}

public sealed record FinalStabilitySample(DateTimeOffset CapturedAtUtc,string ActiveSha256,ushort Brightness,ConfigStoreSnapshot ConfigStore,MailboxSnapshot Mailbox);
public sealed record FinalStabilityReport(DateTimeOffset StartedAtUtc,DateTimeOffset CompletedAtUtc,double DurationSeconds,bool Passed,string? FailureReason,FinalStabilitySample[] Samples);
public static class FinalStabilityReportValidator
{
    public static void ValidatePass(FinalStabilityReport report,TrustedPersistenceBaseline baseline)
    {
        if(!report.Passed||report.DurationSeconds<600||report.CompletedAtUtc<report.StartedAtUtc||report.Samples is null||report.Samples.Length<2||
            Math.Abs((report.CompletedAtUtc-report.StartedAtUtc).TotalSeconds-report.DurationSeconds)>0.01)
            throw new InvalidDataException("Final stability report duration or result is invalid.");
        foreach(var sample in report.Samples)
        {
            if(sample is null||sample.ActiveSha256!=baseline.Manifest.ActiveArraySha256||sample.Brightness!=ConfigurationPersistenceService.OriginalBrightness||
                sample.Mailbox is null||sample.Mailbox.Busy||sample.Mailbox.Pending||sample.ConfigStore is null||!sample.ConfigStore.StatesKnown||
                !sample.ConfigStore.StatesConsistent||sample.ConfigStore.State!=ConfigStoreState.Idle||sample.ConfigStore.ConfigDirty||
                sample.ConfigStore.CurrentRevision!=sample.ConfigStore.SavedRevision||sample.ConfigStore.ActiveSlot is not(1 or 2))
                throw new InvalidDataException("Final stability report contains an invalid sample.");
        }
    }
}

public sealed class FinalPersistenceStabilityService(
    IConfigurationPersistenceDevice device, TrustedPersistenceBaseline baseline,
    IPersistenceClock? clock = null, FinalStabilityPolicy? policy = null)
{
    private readonly IPersistenceClock clock=clock??new SystemPersistenceClock();
    private readonly FinalStabilityPolicy policy=policy??FinalStabilityPolicy.Fixed600Seconds;

    public async Task<FinalStabilityReport> RunAsync(CancellationToken cancellationToken=default)
    {
        var started=clock.UtcNow;var deadline=started+policy.Duration;var samples=new List<FinalStabilitySample>();
        while(true)
        {
            var active=await device.ReadActiveConfigurationAsync(cancellationToken);
            var store=await device.ReadConfigStoreAsync(cancellationToken);
            var mailbox=await device.ReadMailboxAsync(cancellationToken);
            var hash=PersistenceBaselineContract.ComputeActiveSha256(active);
            var sample=new FinalStabilitySample(clock.UtcNow,hash,active[ConfigurationPersistenceService.BrightnessOffset],store,mailbox);
            samples.Add(sample);
            var valid=active.SequenceEqual(baseline.Manifest.ActiveRegisters)&&hash==baseline.Manifest.ActiveArraySha256&&
                sample.Brightness==ConfigurationPersistenceService.OriginalBrightness&&!mailbox.Busy&&!mailbox.Pending&&
                store.StatesKnown&&store.StatesConsistent&&store.State==ConfigStoreState.Idle&&!store.ConfigDirty&&
                store.CurrentRevision==store.SavedRevision&&store.ActiveSlot is 1 or 2;
            if(!valid)return new(started,clock.UtcNow,(clock.UtcNow-started).TotalSeconds,false,"Final stability invariant failed.",samples.ToArray());
            if(clock.UtcNow>=deadline)break;
            await clock.DelayAsync(policy.Interval,cancellationToken);
        }
        return new(started,clock.UtcNow,(clock.UtcNow-started).TotalSeconds,true,null,samples.ToArray());
    }
}
