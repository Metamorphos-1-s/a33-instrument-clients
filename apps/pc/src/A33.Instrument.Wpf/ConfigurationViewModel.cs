using System.Collections.ObjectModel;
using System.Windows;
using A33.Instrument.Core;

namespace A33.Instrument.Wpf;

public sealed partial class MainViewModel
{
    private ConfigurationTransactionService? configuration;
    private ConfigurationTransactionService Configuration => configuration ??= new ConfigurationTransactionService(service);
    public string ConfigurationState => Configuration.State.ToString();
    public string ConfigurationDifferences => string.Join(Environment.NewLine, Configuration.Differences.Select(d => $"{d.Key}: {d.Current} -> {d.Edited} {d.Unit}"));
    public bool ConfigurationDirty => Configuration.Differences.Count > 0;
    public AsyncRelayCommand RefreshConfigurationCommand => new(RefreshConfigurationAsync, () => State == MonitoringConnectionState.Monitoring && !runtimeBusy);
    public AsyncRelayCommand ValidateConfigurationCommand => new(ValidateConfigurationAsync, () => ConfigurationDirty && State == MonitoringConnectionState.Monitoring && !runtimeBusy);
    public AsyncRelayCommand ApplyConfigurationCommand => new(() => ApplyConfigurationAsync(false), () => ConfigurationDirty && State == MonitoringConnectionState.Monitoring && !runtimeBusy);
    public AsyncRelayCommand SaveConfigurationCommand => new(() => ApplyConfigurationAsync(true), () => ConfigurationDirty && State == MonitoringConnectionState.Monitoring && !runtimeBusy);
    public AsyncRelayCommand CancelConfigurationCommand => new(CancelConfigurationAsync, () => State == MonitoringConnectionState.Monitoring && !runtimeBusy);
    public IReadOnlyList<ConfigurationFieldDefinition> ConfigurationFields => ConfigurationContract.EditableFields;
    private string brightnessText = "";
    public string BrightnessText { get => brightnessText; set { brightnessText = value; OnPropertyChanged(); } }
    public AsyncRelayCommand EditBrightnessCommand => new(() => { if (int.TryParse(BrightnessText, out var value)) Configuration.Edit("brightness", value); ConfigurationStateChanged(); Refresh(); return Task.CompletedTask; }, () => State == MonitoringConnectionState.Monitoring && !runtimeBusy && Configuration.Snapshot is not null);
    public AsyncRelayCommand BeginConfigurationCommand => new(async () => { try { await Configuration.BeginAsync(); } catch (Exception error) { service.Diagnostics.Error(error); } Refresh(); }, () => ConfigurationDirty && State == MonitoringConnectionState.Monitoring && !runtimeBusy);

    private async Task RefreshConfigurationAsync() { try { var snapshot = await Configuration.RefreshAsync(); var field = snapshot.Fields.FirstOrDefault(f => f.Key == "brightness"); if (field is not null) BrightnessText = field.Edited[0].ToString(); } catch (Exception error) { service.Diagnostics.Error(error); } Refresh(); }
    private async Task ValidateConfigurationAsync() { try { await Configuration.ValidateAsync(); } catch (Exception error) { service.Diagnostics.Error(error); } Refresh(); }
    private async Task ApplyConfigurationAsync(bool save)
    {
        var message = save ? "将写入设备 Flash。保存期间请勿断电，完成后将重新连接并回读。不会自动重复发送。" : "仅应用到当前运行状态，尚未持久化，设备重启后可能恢复原值。";
        if (MessageBox.Show(message, save ? "保存配置" : "应用配置", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        runtimeBusy = true; Refresh(); try { await Configuration.ApplyRamAsync(save); } catch (Exception error) { service.Diagnostics.Error(error); } finally { runtimeBusy = false; Refresh(); }
    }
    private async Task CancelConfigurationAsync() { try { await Configuration.CancelAsync(); } catch (Exception error) { service.Diagnostics.Error(error); } Refresh(); }
    private void ConfigurationStateChanged() { OnPropertyChanged(nameof(ConfigurationState)); OnPropertyChanged(nameof(ConfigurationDifferences)); OnPropertyChanged(nameof(ConfigurationDirty)); }
}
