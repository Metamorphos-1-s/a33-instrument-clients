using A33.Instrument.Protocol;

namespace A33.Instrument.Core;

public sealed class ConfigurationTransactionService
{
    private readonly InstrumentMonitoringService monitoring;
    private readonly SemaphoreSlim gate = new(1, 1);
    private MailboxTokenAllocator? tokens;
    private ConfigurationSnapshot? snapshot;
    private ushort? activeToken;
    private bool validated;
    private bool saveSent;

    public ConfigurationTransactionService(InstrumentMonitoringService monitoring, MailboxTokenAllocator? tokens = null)
    {
        this.monitoring = monitoring;
        this.tokens = tokens;
    }

    public ConfigurationTransactionState State { get; private set; } = ConfigurationTransactionState.Disconnected;
    public ConfigurationSnapshot? Snapshot => snapshot;
    public IReadOnlyCollection<ushort> UsedTokens => tokens?.Used ?? [];
    public IReadOnlyList<ConfigurationDifference> Differences => snapshot?.Fields.Where(f => f.IsDirty)
        .Select(f => new ConfigurationDifference(f.Key, Format(f.Current), Format(f.Edited), f.Definition.Unit, f.Definition.RequiresReconnect)).ToArray() ?? [];
    public event EventHandler? StateChanged;

    public async Task<ConfigurationSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (State == ConfigurationTransactionState.ResultUncertain)
            throw new InvalidOperationException("RESULT_UNCERTAIN locks this configuration session; disconnect and establish a fresh session before continuing.");
        await WaitFreshAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try { return await RefreshCoreAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public void Edit(string key, long value)
    {
        if (activeToken.HasValue) throw new InvalidOperationException("Cancel active transaction before editing.");
        if (snapshot is null) throw new InvalidOperationException("Refresh required.");
        var field = snapshot.Fields.First(x => x.Key == key);
        if (value < field.Definition.Minimum || value > field.Definition.Maximum) throw new ArgumentOutOfRangeException(nameof(value));
        var edited = (ushort[])field.Edited.Clone();
        edited[0] = (ushort)value;
        snapshot = snapshot with { Fields = snapshot.Fields.Select(x => x.Key == key ? x with { Edited = edited } : x).ToArray() };
        State = ConfigurationTransactionState.Dirty;
        Notify();
    }

    public Task BeginAsync(CancellationToken cancellationToken = default) => ValidateAsync(cancellationToken);

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (snapshot is null || !snapshot.Fields.Any(f => f.IsDirty)) throw new InvalidOperationException("No configuration changes.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (activeToken.HasValue) throw new InvalidOperationException("Transaction already active.");
            State = ConfigurationTransactionState.Validating;
            Notify();
            activeToken = await NextTokenAsync(cancellationToken);
            var response = await SubmitAsync(9, activeToken.Value, cancellationToken);
            if (response.ResultCode != 0) { activeToken=null;State=ConfigurationTransactionState.Error;Notify();throw new InvalidOperationException($"BEGIN result {response.ResultCode}"); }
            foreach (var field in snapshot.Fields.Where(f => f.IsDirty))
                try{await monitoring.WithStrictWriteExclusiveAsync(c => c.WriteMultipleAsync((ushort)(field.Address + 0x40), field.Edited, cancellationToken), cancellationToken);}
                catch(Exception error){throw new AmbiguousDeviceCommandException("Staging write may have reached the device but its result is unknown.",error);}
            activeToken = await NextTokenAsync(cancellationToken);
            response = await SubmitAsync(10, activeToken.Value, cancellationToken);
            if (response.ResultCode != 0) {State=ConfigurationTransactionState.AppliedRam;Notify();throw new InvalidOperationException($"VALIDATE result {response.ResultCode}");}
            validated = true;
            State = ConfigurationTransactionState.AppliedRam;
            Notify();
        }
        catch (AmbiguousDeviceCommandException)
        {
            State = ConfigurationTransactionState.ResultUncertain;
            Notify();
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<CommandExecutionState> ApplyRamAsync(bool save, CancellationToken cancellationToken = default)
    {
        if (save) throw new InvalidOperationException("SAVE is only available through ConfigurationPersistenceService.");
        if (!validated || !activeToken.HasValue) throw new InvalidOperationException("Validate required.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            State = ConfigurationTransactionState.Applying;
            Notify();
            activeToken = await NextTokenAsync(cancellationToken);
            var response = await SubmitAsync(11, activeToken.Value, cancellationToken);
            if (response.ResultCode != 0) {State=ConfigurationTransactionState.AppliedRam;Notify();throw new InvalidOperationException($"APPLY result {response.ResultCode}");}
            activeToken = null;
            validated = false;
            try{await RefreshCoreAsync(cancellationToken);}catch(Exception error){throw new AmbiguousDeviceCommandException("APPLY was accepted but Active readback is unknown.",error);}
            return CommandExecutionState.Succeeded;
        }
        catch (AmbiguousDeviceCommandException)
        {
            State = ConfigurationTransactionState.ResultUncertain;
            Notify();
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!activeToken.HasValue) throw new InvalidOperationException("No active transaction.");
            activeToken = await NextTokenAsync(cancellationToken);
            var response = await SubmitAsync(12, activeToken.Value, cancellationToken);
            if (response.ResultCode != 0) {State=ConfigurationTransactionState.AppliedRam;Notify();throw new InvalidOperationException($"CANCEL result {response.ResultCode}");}
            activeToken = null;
            validated = false;
            try{await RefreshCoreAsync(cancellationToken);}catch(Exception error){throw new AmbiguousDeviceCommandException("CANCEL was accepted but configuration readback is unknown.",error);}
        }
        catch (AmbiguousDeviceCommandException)
        {
            State = ConfigurationTransactionState.ResultUncertain;
            Notify();
            throw;
        }
        finally { gate.Release(); }
    }

    internal async Task<MailboxCommandReceipt> SendSaveOnceAsync(ushort token, CancellationToken cancellationToken = default)
    {
        if (saveSent) throw new InvalidOperationException("SAVE was already sent by this transaction service instance.");
        saveSent = true;
        State = ConfigurationTransactionState.Saving;
        Notify();
        return await SubmitAsync(13, token, cancellationToken);
    }

    private async Task<ConfigurationSnapshot> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var values = new ushort[64];
        await monitoring.WithCommandExclusiveAsync(async client =>
        {
            for (var i = 0; i < 4; i++)
            {
                var chunk = await client.ReadHoldingAsync((ushort)(0x0100 + i * 16), 16, cancellationToken);
                chunk.CopyTo(values, i * 16);
            }
            return 0;
        }, cancellationToken);
        var fields = ConfigurationContract.EditableFields.Select(definition =>
            new ConfigurationField(definition.Key, definition.Address,
                Slice(values, definition.Address - 0x0100, definition.Width),
                Slice(values, definition.Address - 0x0100, definition.Width), definition)).ToArray();
        snapshot = new ConfigurationSnapshot(monitoring.MapVersion, 0, values, values.ToArray(), fields, DateTimeOffset.Now);
        State = ConfigurationTransactionState.Ready;
        Notify();
        return snapshot;
    }

    private async Task<MailboxCommandReceipt> SubmitAsync(ushort commandId, ushort token, CancellationToken cancellationToken)
    {
        var words = new ushort[12];
        words[0] = token;
        words[1] = commandId;
        words[11] = 0xA55A;
        try
        {
            return await monitoring.WithStrictWriteExclusiveAsync(async client =>
            {
                await client.WriteMultipleAsync(0x0040, words, cancellationToken);
                var response = await client.ReadHoldingAsync(0x004C, 12, cancellationToken);
                if (response[0] != token || response[3] != commandId)
                    throw new AmbiguousDeviceCommandException("Mailbox response token or command mismatch after a write.");
                return new MailboxCommandReceipt(response[0], response[3], response[1]);
            }, cancellationToken);
        }
        catch (AmbiguousDeviceCommandException) { throw; }
        catch (Exception error)
        {
            throw new AmbiguousDeviceCommandException($"Command {commandId} may have been sent but its result is unknown.", error);
        }
    }

    private async Task<ushort> NextTokenAsync(CancellationToken cancellationToken)
    {
        if (tokens is null)
        {
            var mailbox = await monitoring.ReadHoldingAsync(ConfigStoreContract.MailboxResponseAddress, ConfigStoreContract.MailboxResponseLength, cancellationToken);
            tokens = new MailboxTokenAllocator(mailbox[0]);
        }
        return tokens.Allocate();
    }

    private async Task WaitFreshAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (monitoring.Snapshot is null || monitoring.IsStale) await Task.Delay(25, timeout.Token);
    }

    private void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);
    private static ushort[] Slice(ushort[] values, int offset, int count) => values.Skip(offset).Take(count).ToArray();
    private static string Format(ushort[] values) => values.Length == 1 ? values[0].ToString() : string.Join(" ", values.Select(x => $"0x{x:X4}"));
}
