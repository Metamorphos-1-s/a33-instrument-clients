using A33.Instrument.Protocol;

namespace A33.Instrument.Core;

public sealed class ConfigurationTransactionService(InstrumentMonitoringService monitoring)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ConfigurationSnapshot? snapshot;
    private ushort token = 1;
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
            State = ConfigurationTransactionState.Validating; Notify();
            var result = await SubmitAsync(9, cancellationToken); if (result != 0) return Reject(result);
            foreach (var field in snapshot.Fields.Where(f => f.IsDirty)) { await SetFieldAsync(field, cancellationToken); wrote = true; }
            result = await SubmitAsync(10, cancellationToken); if (result != 0) return Reject(result);
            State = ConfigurationTransactionState.Applying; Notify(); result = await SubmitAsync(11, cancellationToken); if (result != 0) return Reject(result);
            await RefreshCoreAsync(cancellationToken); State = ConfigurationTransactionState.AppliedRam; Notify();
            if (save) { State = ConfigurationTransactionState.Saving; Notify(); result = await SubmitAsync(13, cancellationToken); if (result != 0) return Reject(result); await RefreshCoreAsync(cancellationToken); }
            return CommandExecutionState.Succeeded;
        }
        catch { State = wrote ? ConfigurationTransactionState.ResultUncertain : ConfigurationTransactionState.Error; Notify(); throw; }
        finally { gate.Release(); }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken); try { await SubmitAsync(12, cancellationToken); await RefreshCoreAsync(cancellationToken); } finally { gate.Release(); }
    }

    private async Task<ushort> SubmitAsync(ushort commandId, CancellationToken tokenCancellation)
    {
        var t = NextToken(); var words = new ushort[12]; words[0] = t; words[1] = commandId; words[11] = 0xA55A;
        var result = await monitoring.WithCommandExclusiveAsync(async c => { await c.WriteMultipleAsync(0x0040, words, tokenCancellation); var response = await c.ReadHoldingAsync(0x004C, 12, tokenCancellation); if (response[0] != t || response[3] != commandId) throw new InvalidOperationException("Configuration response token/command mismatch."); return response[1]; }, tokenCancellation); return result;
    }
    private Task SetFieldAsync(ConfigurationField field, CancellationToken cancellationToken) => field.Key switch
    {
        "brightness" => SubmitFieldAsync(7, 13, 0, field.Edited[0], cancellationToken),
        "startup_auto_zero" => SubmitFieldAsync(7, 20, 0, field.Edited[0], cancellationToken),
        "profile0_filter_strength" => SubmitFieldAsync(29, 0, 3, field.Edited[0], cancellationToken),
        "profile0_stability_window" => SubmitFieldAsync(29, 0, 4, field.Edited[0], cancellationToken),
        "profile0_stability_hold_ms" => SubmitFieldAsync(29, 0, 7, field.Edited[0], cancellationToken),
        "profile1_filter_strength" => SubmitFieldAsync(29, 1, 3, field.Edited[0], cancellationToken),
        "profile1_stability_window" => SubmitFieldAsync(29, 1, 4, field.Edited[0], cancellationToken),
        "profile1_stability_hold_ms" => SubmitFieldAsync(29, 1, 7, field.Edited[0], cancellationToken),
        _ => throw new InvalidOperationException($"No safe command mapping for {field.Key}.")
    };
    private async Task SubmitFieldAsync(ushort commandId, int value0, int value1, long value64, CancellationToken cancellationToken)
    {
        var t=NextToken();var words=new ushort[12];words[0]=t;words[1]=commandId;words[2]=(ushort)(value0>>16);words[3]=(ushort)value0;words[4]=(ushort)(value1>>16);words[5]=(ushort)value1;var raw=unchecked((ulong)value64);for(var i=0;i<4;i++)words[6+i]=(ushort)(raw>>(48-16*i));words[11]=0xA55A;var result=await monitoring.WithCommandExclusiveAsync(async c=>{await c.WriteMultipleAsync(0x0040,words,cancellationToken);var response=await c.ReadHoldingAsync(0x004C,12,cancellationToken);if(response[0]!=t||response[3]!=commandId)throw new InvalidOperationException("Configuration response token/command mismatch.");return response[1];},cancellationToken);if(result!=0)throw new InvalidOperationException($"Device rejected configuration field with Result Code {result}.");
    }
    private CommandExecutionState Reject(ushort result) { State = ConfigurationTransactionState.Error; Notify(); throw new InvalidOperationException($"Device rejected configuration transaction with Result Code {result}."); }
    private ushort NextToken() { var current = token++; return current == 0 ? token++ : current; }
    private void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);
    private static ushort[] Slice(ushort[] values, int offset, int width) => values.Skip(offset).Take(width).ToArray();
    private static string Format(ushort[] values) => values.Length == 1 ? values[0].ToString() : string.Join(" ", values.Select(v => $"0x{v:X4}"));
}
