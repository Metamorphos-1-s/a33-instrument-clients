namespace A33.Instrument.Core;

public sealed class MonitoringConfigurationPersistenceDevice(InstrumentMonitoringService monitoring,TimeSpan? saveResponseQuietPeriod = null) : IConfigurationPersistenceDevice
{
    private ConfigurationTransactionService? transaction;
    public A33.Instrument.Protocol.WordOrder WordOrder => monitoring.WordOrder;

    public Task<ushort[]> ReadHoldingAsync(ushort address, ushort count, CancellationToken cancellationToken = default) =>
        monitoring.ReadHoldingAsync(address, count, cancellationToken);

    public async Task<DeviceIdentity> ReadIdentityAsync(CancellationToken cancellationToken = default)
    {
        var identity = await ReadHoldingAsync(14, 2, cancellationToken);
        var schema = (await ReadHoldingAsync(ConfigStoreContract.StorageSchemaAddress, 1, cancellationToken))[0];
        return new DeviceIdentity(identity[1], schema, identity[0], monitoring.CurrentOptions?.UnitId ?? 0);
    }

    public Task<MailboxSnapshot> ReadMailboxAsync(CancellationToken cancellationToken = default) =>
        ConfigStoreContract.ReadMailboxAsync(this, cancellationToken);

    public Task<ConfigStoreSnapshot> ReadConfigStoreAsync(CancellationToken cancellationToken = default) =>
        ConfigStoreContract.ReadAsync(this, cancellationToken);

    public async Task<ushort[]> ReadActiveConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var values = new ushort[64];
        for (var i = 0; i < 4; i++)
        {
            var chunk = await ReadHoldingAsync((ushort)(0x0100 + i * 16), 16, cancellationToken);
            chunk.CopyTo(values, i * 16);
        }
        return values;
    }

    public async Task ApplyBrightnessRamAsync(ushort brightness, MailboxTokenAllocator tokens, CancellationToken cancellationToken = default)
    {
        transaction = new ConfigurationTransactionService(monitoring, tokens,saveResponseQuietPeriod);
        await transaction.RefreshAsync(cancellationToken);
        transaction.Edit("brightness", brightness);
        await transaction.ValidateAsync(cancellationToken);
        await transaction.ApplyRamAsync(false, cancellationToken);
    }

    public Task<MailboxCommandReceipt> SendSaveOnceAsync(ushort token, CancellationToken cancellationToken = default) =>
        (transaction ?? throw new InvalidOperationException("APPLY RAM must complete before SAVE.")).SendSaveOnceAsync(token, cancellationToken);
}
