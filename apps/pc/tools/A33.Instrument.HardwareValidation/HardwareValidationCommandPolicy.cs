namespace A33.Instrument.HardwareValidation;

public static class HardwareValidationCommandPolicy
{
    public static bool UsesLegacyAuthorizedCancelPath(string command) =>
        string.Equals(command, "cancel-test", StringComparison.OrdinalIgnoreCase);
}
