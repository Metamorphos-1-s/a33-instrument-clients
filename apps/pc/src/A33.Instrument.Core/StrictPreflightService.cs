namespace A33.Instrument.Core;

public sealed record PreflightErrorCounters(long Timeouts, long CrcErrors, long MbapErrors, long TidErrors, long UnitErrors, long ModbusExceptions, long BadFrames)
{
    public bool IsClean => Timeouts == 0 && CrcErrors == 0 && MbapErrors == 0 && TidErrors == 0 && UnitErrors == 0 && ModbusExceptions == 0 && BadFrames == 0;
}

public interface ITrackedReadOnlyRegisterAccess : IReadOnlyRegisterAccess
{
    byte UnitId { get; }
    long Fc03Requests { get; }
    long Fc06Requests { get; }
    long Fc16Requests { get; }
    long MailboxWriteRequests { get; }
    long SaveRequests { get; }
    PreflightErrorCounters Errors { get; }
}

public sealed record StrictPreflightReport(
    bool Passed, string? FailureReason, DeviceIdentity Identity,
    ushort[] RealtimeSnapshot, ushort[] ActiveSnapshot1, ushort[] ActiveSnapshot2,
    ushort[] StagingSnapshot, MailboxSnapshot Mailbox, ConfigStoreSnapshot ConfigStore,
    long Fc03Requests, long Fc06Requests, long Fc16Requests, long MailboxWriteRequests,
    long SaveRequests, PreflightErrorCounters Errors);

public sealed class StrictPreflightService(ITrackedReadOnlyRegisterAccess registers)
{
    public async Task<StrictPreflightReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var identityWords = await registers.ReadHoldingAsync(14, 2, cancellationToken);
        var realtime = await registers.ReadHoldingAsync(0, 34, cancellationToken);
        var active1 = await ReadConfigurationAsync(0x0100, cancellationToken);
        var staging = await ReadConfigurationAsync(0x0140, cancellationToken);
        var mailbox = await ConfigStoreContract.ReadMailboxAsync(registers, cancellationToken);
        var store = await ConfigStoreContract.ReadAsync(registers, cancellationToken);
        var active2 = await ReadConfigurationAsync(0x0100, cancellationToken);
        var identity = new DeviceIdentity(identityWords[1], store.SchemaVersion, identityWords[0], registers.UnitId);

        var failures = new List<string>();
        if (identity is not { FirmwareVersion: 0x050A, SchemaVersion: 2, MapVersion: 0x0104, UnitId: 1 }) failures.Add("Device identity mismatch.");
        if (realtime.Length != 34) failures.Add("Fresh realtime snapshot is incomplete.");
        if (active1.Length != 64 || active2.Length != 64 || staging.Length != 64) failures.Add("Configuration snapshot is incomplete.");
        if (!active1.SequenceEqual(active2)) failures.Add("Active configuration changed during preflight.");
        if (active1.Length == 64 && active1[ConfigurationPersistenceService.BrightnessOffset] != ConfigurationPersistenceService.OriginalBrightness) failures.Add("Active brightness is not 3.");
        if (!store.StatesKnown || !store.StatesConsistent) failures.Add("ConfigStore state mirrors are unknown or inconsistent.");
        if (store.State != ConfigStoreState.Idle) failures.Add("ConfigStore is not IDLE.");
        if (store.ActiveSlot is not (1 or 2)) failures.Add("ConfigStore active slot is invalid.");
        if (store.ConfigDirty || store.CurrentRevision != store.SavedRevision) failures.Add("ConfigStore has pending unsaved configuration.");
        if (mailbox.Busy || mailbox.Pending) failures.Add("Mailbox is BUSY or pending.");
        if (!registers.Errors.IsClean) failures.Add("Communication diagnostics are not clean.");
        if (registers.Fc06Requests != 0 || registers.Fc16Requests != 0 || registers.MailboxWriteRequests != 0 || registers.SaveRequests != 0) failures.Add("Read-only request policy was violated.");

        return new StrictPreflightReport(failures.Count == 0, failures.Count == 0 ? null : string.Join(" ", failures), identity,
            realtime, active1, active2, staging, mailbox, store, registers.Fc03Requests,
            registers.Fc06Requests, registers.Fc16Requests, registers.MailboxWriteRequests, registers.SaveRequests, registers.Errors);
    }

    private async Task<ushort[]> ReadConfigurationAsync(ushort start, CancellationToken cancellationToken)
    {
        var result = new ushort[64];
        for (var i = 0; i < 4; i++)
        {
            var chunk = await registers.ReadHoldingAsync((ushort)(start + i * 16), 16, cancellationToken);
            if (chunk.Length != 16) throw new InvalidDataException("Configuration chunk must contain 16 registers.");
            chunk.CopyTo(result, i * 16);
        }
        return result;
    }
}
