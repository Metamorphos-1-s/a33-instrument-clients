using System.Windows;
using A33.Instrument.Core;

namespace A33.Instrument.Wpf;

public sealed partial class MainViewModel
{
    private ConfigurationTransactionService? configuration;
    private ConfigurationTransactionService Configuration
    {
        get
        {
            if (configuration is not null) return configuration;
            configuration = new ConfigurationTransactionService(service);
            configuration.StateChanged += ConfigurationServiceStateChanged;
            return configuration;
        }
    }
    private ConfigurationTransactionState ConfigurationTransactionState => configuration?.State ?? ConfigurationTransactionState.Disconnected;

    public string ConfigurationState => ConfigurationTransactionState.ToString();
    public string ConfigurationDifferences => configuration is null
        ? ""
        : string.Join(Environment.NewLine, configuration.Differences.Select(d => $"{d.Key}: {d.Current} -> {d.Edited} {d.Unit}"));
    public bool ConfigurationDirty => configuration?.Differences.Count > 0;
    public bool ConfigurationTransactionActive => ConfigurationUiPolicy.ConfigurationTransactionActive(ConfigurationTransactionState);
    public string ConfigurationScope => "Stage 2B仅开放亮度(0-7)的RAM事务；Flash保存尚未作为产品功能开放。";

    public AsyncRelayCommand RefreshConfigurationCommand { get; private set; } = null!;
    public AsyncRelayCommand ValidateConfigurationCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplyConfigurationCommand { get; private set; } = null!;
    public AsyncRelayCommand CancelConfigurationCommand { get; private set; } = null!;
    public AsyncRelayCommand EditBrightnessCommand { get; private set; } = null!;

    private string brightnessText = "";
    public string BrightnessText { get => brightnessText; set { brightnessText = value; OnPropertyChanged(); } }

    private void InitializeConfigurationCommands()
    {
        RefreshConfigurationCommand = Command(RefreshConfigurationAsync,
            () => ConfigurationUiPolicy.CanRefresh(State, service.IsStale, runtimeBusy || runtime.State != CommandExecutionState.Idle, ConfigurationTransactionState));
        EditBrightnessCommand = Command(EditBrightnessAsync,
            () => ConfigurationUiPolicy.CanEdit(State, service.IsStale, runtimeBusy || runtime.State != CommandExecutionState.Idle,
                HasUncertainResult, ConfigurationTransactionState, configuration?.Snapshot is not null));
        ValidateConfigurationCommand = Command(ValidateConfigurationAsync,
            () => ConfigurationUiPolicy.CanValidate(State, service.IsStale, runtimeBusy || runtime.State != CommandExecutionState.Idle,
                HasUncertainResult, ConfigurationTransactionState, ConfigurationDirty));
        ApplyConfigurationCommand = Command(ApplyConfigurationAsync,
            () => ConfigurationUiPolicy.CanApplyRam(State, service.IsStale, runtimeBusy || runtime.State != CommandExecutionState.Idle,
                HasUncertainResult, ConfigurationTransactionState, ConfigurationDirty));
        CancelConfigurationCommand = Command(CancelConfigurationAsync,
            () => ConfigurationUiPolicy.CanCancel(State, service.IsStale, runtimeBusy || runtime.State != CommandExecutionState.Idle,
                HasUncertainResult, ConfigurationTransactionState));
    }

    private Task EditBrightnessAsync()
    {
        if (!int.TryParse(BrightnessText, out var value)) throw new InvalidOperationException("亮度必须是0到7之间的整数。");
        Configuration.Edit("brightness", value);
        Refresh();
        return Task.CompletedTask;
    }

    private async Task RefreshConfigurationAsync()
    {
        runtimeBusy = true; Refresh();
        try
        {
            var snapshot = await Configuration.RefreshAsync();
            BrightnessText = snapshot.Fields.Single(f => f.Key == "brightness").Edited[0].ToString();
        }
        finally { runtimeBusy = false; Refresh(); }
    }

    private async Task ValidateConfigurationAsync()
    {
        runtimeBusy = true; Refresh();
        try { await Configuration.ValidateAsync(); }
        finally { runtimeBusy = false; Refresh(); }
    }

    private async Task ApplyConfigurationAsync()
    {
        const string message = "仅应用亮度到当前RAM运行状态，尚未持久化；设备重启后恢复Flash中的值。";
        if (MessageBox.Show(message, "应用亮度到RAM", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        runtimeBusy = true;
        Refresh();
        try { await Configuration.ApplyRamAsync(false); }
        finally { runtimeBusy = false; Refresh(); }
    }

    private async Task CancelConfigurationAsync()
    {
        runtimeBusy = true; Refresh();
        try { await Configuration.CancelAsync(); }
        finally { runtimeBusy = false; Refresh(); }
    }

    private void ResetConfigurationSession()
    {
        if (configuration is null) return;
        configuration.StateChanged -= ConfigurationServiceStateChanged;
        configuration = null;
        BrightnessText = "";
        ConfigurationStateChanged();
    }

    private void ConfigurationServiceStateChanged(object? sender, EventArgs e) => Refresh();

    private void ConfigurationStateChanged()
    {
        OnPropertyChanged(nameof(ConfigurationState));
        OnPropertyChanged(nameof(ConfigurationDifferences));
        OnPropertyChanged(nameof(ConfigurationDirty));
        OnPropertyChanged(nameof(ConfigurationTransactionActive));
        RaiseConfigurationCanExecuteChanged();
    }

    private void RaiseConfigurationCanExecuteChanged()
    {
        RefreshConfigurationCommand?.RaiseCanExecuteChanged();
        EditBrightnessCommand?.RaiseCanExecuteChanged();
        ValidateConfigurationCommand?.RaiseCanExecuteChanged();
        ApplyConfigurationCommand?.RaiseCanExecuteChanged();
        CancelConfigurationCommand?.RaiseCanExecuteChanged();
    }
}
