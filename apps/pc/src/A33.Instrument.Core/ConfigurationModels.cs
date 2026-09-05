namespace A33.Instrument.Core;

public enum ConfigurationTransactionState { Disconnected, Reading, Ready, Dirty, Validating, Applying, AppliedRam, Saving, Reconnecting, ResultUncertain, Error }
public enum ConfigurationValueKind { UInt16, Enum, Boolean, UInt64 }
public sealed record ConfigurationFieldDefinition(string Key, ushort Address, ushort Width, ConfigurationValueKind Kind, long Minimum, long Maximum, bool Editable, bool RequiresSave, bool RequiresReconnect, string Unit);
public sealed record ConfigurationField(string Key, ushort Address, ushort[] Current, ushort[] Edited, ConfigurationFieldDefinition Definition)
{
    public bool IsDirty => !Current.SequenceEqual(Edited);
}
public sealed record ConfigurationSnapshot(ushort MapVersion, ushort FirmwareVersion, ushort[] Active, ushort[] Staging, IReadOnlyList<ConfigurationField> Fields, DateTimeOffset CapturedAt);
public sealed record ConfigurationDifference(string Key, string Current, string Edited, string Unit, bool RequiresReconnect);

public static class ConfigurationContract
{
    public static readonly IReadOnlyList<ConfigurationFieldDefinition> EditableFields =
    [
        new("brightness", 0x0116, 1, ConfigurationValueKind.UInt16, 0, 7, true, true, false, "level"),
        new("startup_auto_zero", 0x013C, 1, ConfigurationValueKind.Boolean, 0, 1, true, true, false, "bool"),
        new("profile0_filter_strength", 0x0123, 1, ConfigurationValueKind.UInt16, 0, 255, true, true, false, "level"),
        new("profile0_stability_window", 0x0124, 1, ConfigurationValueKind.UInt16, 1, 255, true, true, false, "samples"),
        new("profile0_stability_hold_ms", 0x0125, 1, ConfigurationValueKind.UInt16, 0, 60000, true, true, false, "ms"),
        new("profile1_filter_strength", 0x0131, 1, ConfigurationValueKind.UInt16, 0, 255, true, true, false, "level"),
        new("profile1_stability_window", 0x0132, 1, ConfigurationValueKind.UInt16, 1, 255, true, true, false, "samples"),
        new("profile1_stability_hold_ms", 0x0133, 1, ConfigurationValueKind.UInt16, 0, 60000, true, true, false, "ms")
    ];
}
