using A33.Instrument.Protocol;

namespace A33.Instrument.Core;

public sealed class ConfigurationTransactionService(InstrumentMonitoringService monitoring)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ConfigurationSnapshot? snapshot;
    private ushort token = 1;
    private ushort? activeToken;
    private bool validated;
    public ConfigurationTransactionState State { get; private set; } = ConfigurationTransactionState.Disconnected;
    public ConfigurationSnapshot? Snapshot => snapshot;
    public IReadOnlyList<ConfigurationDifference> Differences => snapshot?.Fields.Where(f => f.IsDirty).Select(f => new ConfigurationDifference(f.Key, Format(f.Current), Format(f.Edited), f.Definition.Unit, f.Definition.RequiresReconnect)).ToArray() ?? [];
    public event EventHandler? StateChanged;

    public async Task<ConfigurationSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken); State = ConfigurationTransactionState.Reading; Notify();
        try
        {
            return await RefreshCoreAsync(cancellationToken);
        }
        catch { State = ConfigurationTransactionState.Error; Notify(); throw; }
        finally { gate.Release(); }
    }

    private async Task<ConfigurationSnapshot> RefreshCoreAsync(CancellationToken cancellationToken)
    {
            var values = await monitoring.WithCommandExclusiveAsync(async c => await c.ReadHoldingAsync(0x0100, 64, cancellationToken), cancellationToken);
            if (monitoring.MapVersion != RegisterMap.Version) throw new InvalidOperationException("Configuration requires compatible Map 0x0104.");
            var fields = ConfigurationContract.EditableFields.Select(d => new ConfigurationField(d.Key, d.Address, Slice(values, d.Address - 0x0100, d.Width), Slice(values, d.Address - 0x0100, d.Width), d)).ToArray();
            snapshot = new ConfigurationSnapshot(monitoring.MapVersion, 0, values, values.ToArray(), fields, DateTimeOffset.Now); State = ConfigurationTransactionState.Ready; Notify(); return snapshot;
    }

    public void Edit(string key, long value)
    {
        if (activeToken.HasValue) throw new InvalidOperationException("Cancel the active device transaction before editing.");
        if (snapshot is null) throw new InvalidOperationException("Configuration has not been refreshed.");
        var field = snapshot.Fields.FirstOrDefault(f => f.Key == key) ?? throw new KeyNotFoundException(key);
        if (!field.Definition.Editable || value < field.Definition.Minimum || value > field.Definition.Maximum) throw new ArgumentOutOfRangeException(nameof(value));
        var edited = (ushort[])field.Edited.Clone(); edited[0] = checked((ushort)value);
        snapshot = snapshot with { Fields = snapshot.Fields.Select(f => f.Key == key ? f with { Edited = edited } : f).ToArray() }; State = ConfigurationTransactionState.Dirty; Notify();
    }

    public async Task<CommandExecutionState> ApplyRamAsync(bool save, CancellationToken cancellationToken = default)
    {
        if (snapshot is null || !snapshot.Fields.Any(f => f.IsDirty)) throw new InvalidOperationException("No configuration changes are pending.");
        await gate.WaitAsync(cancellationToken); var wrote = false;
        try
        {
            if (!validated || !activeToken.HasValue) throw new InvalidOperationException("Device validation is required before Apply RAM.");
            State = ConfigurationTransactionState.Applying; Notify();
            var result = await SubmitAsync(11, cancellationToken, activeToken.Value); if (result != 0) return Reject(result);
            await RefreshCoreAsync(cancellationToken); State = ConfigurationTransactionState.AppliedRam; Notify();
            if (save) { State = ConfigurationTransactionState.Saving; Notify(); result = await SubmitAsync(13, cancellationToken); if (result != 0) return Reject(result); await RefreshCoreAsync(cancellationToken); }
            return CommandExecutionState.Succeeded;
        }
        catch { State = wrote ? ConfigurationTransactionState.ResultUncertain : ConfigurationTransactionState.Error; Notify(); throw; }
        finally { gate.Release(); }
    }
    public async Task BeginAsync(CancellationToken cancellationToken = default) => await ValidateDeviceAsync(cancellationToken);
    public async Task ValidateAsync(CancellationToken cancellationToken = default) => await ValidateDeviceAsync(cancellationToken);
    private async Task ValidateDeviceAsync(CancellationToken cancellationToken)
    {
        if (snapshot is null || !snapshot.Fields.Any(f => f.IsDirty)) throw new InvalidOperationException("No configuration changes are pending.");
        await gate.WaitAsync(cancellationToken); try { if (activeToken.HasValue) throw new InvalidOperationException("A configuration transaction is already active."); activeToken=NextToken(); State=ConfigurationTransactionState.Validating; Notify(); var r=await SubmitAsync(9,cancellationToken,activeToken.Value); if(r!=0) throw new InvalidOperationException($"Device rejected BEGIN with Result Code {r}."); foreach(var field in snapshot.Fields.Where(f=>f.IsDirty)){await monitoring.WithCommandExclusiveAsync(c=>c.WriteMultipleAsync((ushort)(field.Address+0x40),field.Edited,cancellationToken),cancellationToken);} r=await SubmitAsync(10,cancellationToken,activeToken.Value); if(r!=0) throw new InvalidOperationException($"Device rejected VALIDATE with Result Code {r}."); validated=true; State=ConfigurationTransactionState.AppliedRam; Notify(); } catch { State=ConfigurationTransactionState.ResultUncertain; Notify(); throw; } finally { gate.Release(); }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken); try { if(!activeToken.HasValue) throw new InvalidOperationException("No active configuration transaction."); await SubmitAsync(12, cancellationToken, activeToken.Value); activeToken=null; validated=false; await RefreshCoreAsync(cancellationToken); } finally { gate.Release(); }
    }

    private async Task<ushort> SubmitAsync(ushort commandId, CancellationToken tokenCancellation, ushort? transactionToken=null)
    {
        var t = transactionToken ?? NextToken(); var words = new ushort[12]; words[0] = t; words[1] = commandId; words[11] = 0xA55A;
        var result = await monitoring.WithCommandExclusiveAsync(async c => { await c.WriteMultipleAsync(0x0040, words, tokenCancellation); var response = await c.ReadHoldingAsync(0x004C, 12, tokenCancellation); if (response[0] != t || response[3] != commandId) throw new InvalidOperationException("Configuration response token/command mismatch."); return response[1]; }, tokenCancellation); return result;
    }
    private CommandExecutionState Reject(ushort result) { State = ConfigurationTransactionState.Error; Notify(); throw new InvalidOperationException($"Device rejected configuration transaction with Result Code {result}."); }
    private ushort NextToken() { var current = token++; return current == 0 ? token++ : current; }
    private void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);
    private static ushort[] Slice(ushort[] values, int offset, int width) => values.Skip(offset).Take(width).ToArray();
    private static string Format(ushort[] values) => values.Length == 1 ? values[0].ToString() : string.Join(" ", values.Select(v => $"0x{v:X4}"));
}
