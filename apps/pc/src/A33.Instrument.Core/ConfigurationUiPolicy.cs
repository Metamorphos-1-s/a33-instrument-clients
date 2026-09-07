namespace A33.Instrument.Core;

public static class ConfigurationUiPolicy
{
    public static bool CanRefresh(MonitoringConnectionState monitoring, bool stale, bool busy, ConfigurationTransactionState state) =>
        monitoring == MonitoringConnectionState.Monitoring && !stale && !busy && state != ConfigurationTransactionState.ResultUncertain;

    public static bool CanEdit(MonitoringConnectionState monitoring, bool stale, bool busy, bool runtimeUncertain,
        ConfigurationTransactionState state, bool hasSnapshot) =>
        CanWriteBase(monitoring, stale, busy, runtimeUncertain) && hasSnapshot &&
        state is ConfigurationTransactionState.Ready or ConfigurationTransactionState.Dirty;

    public static bool CanValidate(MonitoringConnectionState monitoring, bool stale, bool busy, bool runtimeUncertain,
        ConfigurationTransactionState state, bool dirty) =>
        CanWriteBase(monitoring, stale, busy, runtimeUncertain) && dirty && state == ConfigurationTransactionState.Dirty;

    public static bool CanApplyRam(MonitoringConnectionState monitoring, bool stale, bool busy, bool runtimeUncertain,
        ConfigurationTransactionState state, bool dirty) =>
        CanWriteBase(monitoring, stale, busy, runtimeUncertain) && dirty && state == ConfigurationTransactionState.AppliedRam;

    public static bool CanCancel(MonitoringConnectionState monitoring, bool stale, bool busy, bool runtimeUncertain,
        ConfigurationTransactionState state) =>
        CanWriteBase(monitoring, stale, busy, runtimeUncertain) && state == ConfigurationTransactionState.AppliedRam;

    public static bool ConfigurationTransactionActive(ConfigurationTransactionState state) => state is
        ConfigurationTransactionState.Validating or ConfigurationTransactionState.Applying or
        ConfigurationTransactionState.AppliedRam or ConfigurationTransactionState.Saving or
        ConfigurationTransactionState.ResultUncertain;

    private static bool CanWriteBase(MonitoringConnectionState monitoring, bool stale, bool busy, bool runtimeUncertain) =>
        monitoring == MonitoringConnectionState.Monitoring && !stale && !busy && !runtimeUncertain;
}
