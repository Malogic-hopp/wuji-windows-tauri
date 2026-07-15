using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class PrivacyPageViewModel : ObservableObject
{
    private readonly SettingsViewModel _settings;
    private string _newExcludedProcess = string.Empty;
    private string _selectedRetentionDays = "30";
    private bool _isFullPathVisible;
    private bool _isExportConfirming;
    private string _exportStatusText = "尚未导出数据。";
    private bool _isSynchronizing;

    public PrivacyPageViewModel(SettingsViewModel settingsViewModel)
    {
        _settings = settingsViewModel;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        AddExclusionCommand = new RelayCommand(AddExclusion, () => !string.IsNullOrWhiteSpace(NewExcludedProcess));
        RemoveExclusionCommand = new RelayCommand<PrivacyExclusionRule?>(RemoveExclusion);
        SavePrivacyCommand = new AsyncRelayCommand(SavePrivacyAsync);
        BeginExportCommand = new RelayCommand(() => IsExportConfirming = true);
        CancelExportCommand = new RelayCommand(() => IsExportConfirming = false);
        ConfirmExportCommand = new AsyncRelayCommand(ExportAsync);
        SyncFromSettings();
    }

    public IReadOnlyList<RetentionChoice> RetentionOptions { get; } =
    [
        new("7", "7 天"),
        new("30", "30 天"),
        new("90", "90 天"),
        new("Custom", "自定义")
    ];

    public ObservableCollection<PrivacyExclusionRule> ExclusionRules { get; } = new();

    public IRelayCommand AddExclusionCommand { get; }

    public IRelayCommand<PrivacyExclusionRule?> RemoveExclusionCommand { get; }

    public IAsyncRelayCommand SavePrivacyCommand { get; }

    public IRelayCommand BeginExportCommand { get; }

    public IRelayCommand CancelExportCommand { get; }

    public IAsyncRelayCommand ConfirmExportCommand { get; }

    public string NewExcludedProcess
    {
        get => _newExcludedProcess;
        set
        {
            if (SetProperty(ref _newExcludedProcess, value))
            {
                AddExclusionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SelectedRetentionDays
    {
        get => _selectedRetentionDays;
        set
        {
            if (SetProperty(ref _selectedRetentionDays, value) && !_isSynchronizing && value != "Custom")
            {
                _settings.RetentionDaysText = value;
            }
        }
    }

    public string CustomRetentionDays
    {
        get => _settings.RetentionDaysText;
        set
        {
            _settings.RetentionDaysText = value;
            OnPropertyChanged();
        }
    }

    public bool IsCustomRetention => SelectedRetentionDays == "Custom";

    public bool MaskWindowTitles
    {
        get => _settings.MaskWindowTitles;
        set => _settings.MaskWindowTitles = value;
    }

    public bool IsFullPathVisible
    {
        get => _isFullPathVisible;
        set
        {
            if (SetProperty(ref _isFullPathVisible, value))
            {
                OnPropertyChanged(nameof(DataPathText));
            }
        }
    }

    public string DataPathText => IsFullPathVisible ? _settings.DataRootText : MaskPath(_settings.DataRootText);

    public bool IsExportConfirming
    {
        get => _isExportConfirming;
        set => SetProperty(ref _isExportConfirming, value);
    }

    public string ExportStatusText
    {
        get => _exportStatusText;
        private set => SetProperty(ref _exportStatusText, value);
    }

    public bool IsClearHistoryConfirming => _settings.IsClearHistoryConfirming;

    public string ClearHistoryConfirmationInput
    {
        get => _settings.ClearHistoryConfirmationInput;
        set => _settings.ClearHistoryConfirmationInput = value;
    }

    public string ClearHistoryStatusText => _settings.ClearHistoryStatusText;

    public string PruneDataStatusText => _settings.PruneDataStatusText;

    public string LastMaintenanceStatusText => _settings.LastMaintenanceStatusText;

    public IAsyncRelayCommand ClearHistoryCommand => _settings.ClearHistoryCommand;

    public IAsyncRelayCommand ConfirmClearHistoryCommand => _settings.ConfirmClearHistoryCommand;

    public IAsyncRelayCommand PruneDataCommand => _settings.PruneDataCommand;

    public ICommand OpenDataFolderCommand => _settings.OpenDataFolderCommand;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.IsDirty)
        {
            await _settings.LoadAsync(cancellationToken);
        }
        SyncFromSettings();
    }

    private void AddExclusion()
    {
        var value = NewExcludedProcess.Trim();
        if (string.IsNullOrWhiteSpace(value)
            || ExclusionRules.Any(rule => string.Equals(rule.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ExclusionRules.Add(new PrivacyExclusionRule(value, true));
        NewExcludedProcess = string.Empty;
        SyncRulesToSettings();
    }

    private void RemoveExclusion(PrivacyExclusionRule? rule)
    {
        if (rule is null || !ExclusionRules.Remove(rule))
        {
            return;
        }
        SyncRulesToSettings();
    }

    private async Task SavePrivacyAsync()
    {
        SyncRulesToSettings();
        await _settings.SaveAgentOptionsAsync();
    }

    private async Task ExportAsync()
    {
        IsExportConfirming = false;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出 WUJI 本地数据",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = $"WUJI-本地数据-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog() != true)
        {
            ExportStatusText = "已取消导出。";
            return;
        }

        try
        {
            await Task.Run(() => CreateExportArchive(dialog.FileName));
            ExportStatusText = "本地数据已导出。压缩包可能包含敏感活动记录，请妥善保管。";
        }
        catch
        {
            ExportStatusText = "导出失败。请确认目标位置可写，然后重试。";
        }
    }

    private void CreateExportArchive(string destinationPath)
    {
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        AddFile(archive, _settings.DatabasePathText, "data/wuji.db");
        AddFile(archive, _settings.AppSettingsPathText, "config/app-settings.json");
        AddFile(archive, _settings.AgentOptionsPathText, "config/agent-options.json");
    }

    private static void AddFile(ZipArchive archive, string path, string entryName)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = entry.Open();
        source.CopyTo(target);
    }

    private void SyncFromSettings()
    {
        _isSynchronizing = true;
        try
        {
            ExclusionRules.Clear();
            foreach (var value in ParseLines(_settings.ExcludedProcessesText))
            {
                ExclusionRules.Add(new PrivacyExclusionRule(value, true));
            }

            var retention = _settings.RetentionDaysText;
            SelectedRetentionDays = retention is "7" or "30" or "90" ? retention : "Custom";
            OnPropertyChanged(nameof(CustomRetentionDays));
            OnPropertyChanged(nameof(IsCustomRetention));
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void SyncRulesToSettings() =>
        _settings.ExcludedProcessesText = string.Join(Environment.NewLine, ExclusionRules.Where(rule => rule.IsEnabled).Select(rule => rule.Value));

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.IsClearHistoryConfirming)
            or nameof(SettingsViewModel.ClearHistoryConfirmationInput)
            or nameof(SettingsViewModel.ClearHistoryStatusText)
            or nameof(SettingsViewModel.PruneDataStatusText)
            or nameof(SettingsViewModel.LastMaintenanceStatusText))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    private static IEnumerable<string> ParseLines(string? text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.Equals(value, "(none)", StringComparison.OrdinalIgnoreCase));

    private static string MaskPath(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return $"{root}…{Path.DirectorySeparatorChar}{leaf}";
    }
}

public sealed partial class PrivacyExclusionRule : ObservableObject
{
    public PrivacyExclusionRule(string value, bool isEnabled)
    {
        Value = value;
        _isEnabled = isEnabled;
    }

    public string Value { get; }

    [ObservableProperty]
    private bool _isEnabled;
}

public sealed record RetentionChoice(string Value, string DisplayName);
