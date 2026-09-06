using A33.Instrument.Protocol;

namespace A33.Instrument.Core;

public enum ConfigStoreState : ushort
{
    Idle = 0, Prepare = 1, ErasePage0 = 2, ErasePage1 = 3, ProgramBody = 4,
    VerifyBody = 5, ProgramCommit = 6, VerifyFinal = 7, Complete = 8, Error = 9
}

public sealed record MailboxSnapshot(ushort ResponseToken, ushort ResultCode, ushort CommandState, ushort LastCommandId, ushort[] Raw)
{
    public bool Busy => CommandState != 0;
    public bool Pending => CommandState != 0;
}

public sealed record ConfigStoreSnapshot(
    ushort StateMirror1Raw, ushort StateMirror2Raw,
    ConfigStoreState? StateMirror1, ConfigStoreState? StateMirror2,
    bool ConfigDirty, uint CurrentRevision, uint SavedRevision,
    ushort SchemaVersion, ushort ActiveSlot, uint ActiveSequence)
{
    public bool StatesKnown => StateMirror1.HasValue && StateMirror2.HasValue;
    public bool StatesConsistent => StatesKnown && StateMirror1 == StateMirror2;
    public ConfigStoreState? State => StatesConsistent ? StateMirror1 : null;
}

public interface IReadOnlyRegisterAccess
{
    WordOrder WordOrder { get; }
    Task<ushort[]> ReadHoldingAsync(ushort address, ushort count, CancellationToken cancellationToken = default);
}

public static class ConfigStoreContract
{
    public const ushort StateMirror1Address = 0x0030;
    public const ushort ConfigDirtyAddress = 0x0032;
    public const ushort CurrentRevisionAddress = 0x0033;
    public const ushort SavedRevisionAddress = 0x0035;
    public const ushort MailboxResponseAddress = 0x004C;
    public const ushort MailboxResponseLength = 12;
    public const ushort StorageSchemaAddress = 0x01C0;
    public const ushort ActiveSlotAddress = 0x01C1;
    public const ushort ActiveSequenceAddress = 0x01C2;
    public const ushort StateMirror2Address = 0x01C4;

    public static ConfigStoreState? ParseState(ushort raw) =>
        Enum.IsDefined(typeof(ConfigStoreState), raw) ? (ConfigStoreState)raw : null;

    public static ConfigStoreSnapshot Decode(ReadOnlySpan<ushort> diagnostics, ReadOnlySpan<ushort> storage, WordOrder wordOrder)
    {
        if (diagnostics.Length != 7) throw new ArgumentException("Diagnostics block must contain 0x0030-0x0036.", nameof(diagnostics));
        if (storage.Length != 5) throw new ArgumentException("Storage block must contain 0x01C0-0x01C4.", nameof(storage));
        return new ConfigStoreSnapshot(
            diagnostics[0], storage[4], ParseState(diagnostics[0]), ParseState(storage[4]), diagnostics[2] != 0,
            RegisterValueCodec.UInt32(diagnostics[3..5], wordOrder),
            RegisterValueCodec.UInt32(diagnostics[5..7], wordOrder),
            storage[0], storage[1], RegisterValueCodec.UInt32(storage[2..4], wordOrder));
    }

    public static async Task<ConfigStoreSnapshot> ReadAsync(IReadOnlyRegisterAccess registers, CancellationToken cancellationToken = default)
    {
        var diagnostics = await registers.ReadHoldingAsync(StateMirror1Address, 7, cancellationToken);
        var storage = await registers.ReadHoldingAsync(StorageSchemaAddress, 5, cancellationToken);
        return Decode(diagnostics, storage, registers.WordOrder);
    }

    public static async Task<MailboxSnapshot> ReadMailboxAsync(IReadOnlyRegisterAccess registers, CancellationToken cancellationToken = default)
    {
        var raw = await registers.ReadHoldingAsync(MailboxResponseAddress, MailboxResponseLength, cancellationToken);
        return new MailboxSnapshot(raw[0], raw[1], raw[2], raw[3], raw);
    }

    public static uint NextSequence(uint current) => current + 1 == uint.MaxValue ? 0 : current + 1;
    public static ushort NextSlot(ushort current) => current switch { 0 => 1, 1 => 2, 2 => 1, _ => 0 };
}

public enum ConfigStoreCompletionResult { InProgress, Confirmed, Failed, ResultUncertain }
public sealed record ConfigStoreCompletionEvaluation(ConfigStoreCompletionResult Result, string Reason);

public static class ConfigStoreCompletionEvaluator
{
    public static ConfigStoreCompletionEvaluation Evaluate(
        ConfigStoreSnapshot before, ConfigStoreSnapshot current,
        IReadOnlyList<ushort> expectedActive, IReadOnlyList<ushort> actualActive)
    {
        if (before.SchemaVersion != 2 || current.SchemaVersion != 2) return Uncertain("ConfigStore schema is not 2.");
        if (!current.StatesKnown) return Uncertain("Unknown ConfigStore state value.");
        if (!current.StatesConsistent) return Uncertain("ConfigStore state mirrors disagree.");
        if (current.State == ConfigStoreState.Error)
            return new(ConfigStoreCompletionResult.Failed, "ConfigStore reported ERROR; the internal operation result and last error are not exposed over Modbus.");
        if (current.State is >= ConfigStoreState.Prepare and <= ConfigStoreState.VerifyFinal)
            return new(ConfigStoreCompletionResult.InProgress, "ConfigStore operation is in progress.");
        if (current.State is not (ConfigStoreState.Idle or ConfigStoreState.Complete))
            return Uncertain("ConfigStore is not in an acceptable terminal state.");

        var expectedSlot = ConfigStoreContract.NextSlot(before.ActiveSlot);
        if (expectedSlot == 0) return Uncertain("Pre-SAVE active slot is invalid.");
        var complete = !current.ConfigDirty && current.CurrentRevision == current.SavedRevision &&
            current.CurrentRevision == before.CurrentRevision &&
            current.ActiveSequence == ConfigStoreContract.NextSequence(before.ActiveSequence) &&
            current.ActiveSlot == expectedSlot &&
            expectedActive.SequenceEqual(actualActive);
        return complete
            ? new(ConfigStoreCompletionResult.Confirmed, "All public ConfigStore completion invariants are satisfied.")
            : new(ConfigStoreCompletionResult.InProgress, "Terminal state observed but the public completion invariants are not yet all satisfied.");
    }

    private static ConfigStoreCompletionEvaluation Uncertain(string reason) => new(ConfigStoreCompletionResult.ResultUncertain, reason);
}

public sealed class MailboxTokenAllocator
{
    private readonly HashSet<ushort> used = [];
    private ushort last;

    public MailboxTokenAllocator(ushort deviceToken) => last = deviceToken;
    public ushort LastIssued => last;
    public IReadOnlyCollection<ushort> Used => used;

    public ushort Allocate()
    {
        for (var attempts = 0; attempts < ushort.MaxValue - 1; attempts++)
        {
            last++;
            if (last != 0 && used.Add(last)) return last;
        }
        throw new InvalidOperationException("No unused non-zero Mailbox token remains.");
    }
}
