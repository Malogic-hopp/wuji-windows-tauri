using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantifiedSelf.Windows.App.Models;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    public IReadOnlyList<string> LastSelectedPageOptions { get; } =
        MainWindowViewModel.NavigationPages;

    private readonly Func<CancellationToken, Task<AppSettings>> _readAppSettingsAsync;
    private readonly Func<AppSettings, CancellationToken, Task> _saveAppSettingsAsync;
    private readonly Func<CancellationToken, Task<WindowsAgentOptions>> _readAgentOptionsAsync;
    private readonly Func<WindowsAgentOptions, CancellationToken, Task> _saveAgentOptionsAsync;
    private readonly Func<CancellationToken, Task> _restoreAgentOptionsBackupAsync;
    private readonly Func<CancellationToken, Task<AgentStatusSnapshot>>? _getAgentStatusAsync;
    private readonly Func<CancellationToken, Task<AgentCommandResult>>? _requestReloadConfigAsync;
    private readonly Func<CancellationToken, Task<AgentCommandResult>>? _requestPruneDataAsync;
    private readonly Func<CancellationToken, Task<AgentCommandResult>>? _requestClearHistoryAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AgentEvent>>>? _getRecentAgentEventsAsync;
    private readonly AgentOptionsValidator _agentOptionsValidator;
    private readonly WindowsAgentPaths _paths;

    internal int ReloadConfigPollMaxAttempts { get; set; } = 30;
    internal TimeSpan ReloadConfigPollDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    private AppSettings _appSettings = new();
    private WindowsAgentOptions _agentOptions = new();
    private string _statusText = "No settings loaded.";
    private string _emptyStateText = "No settings loaded.";
    private string _saveStatusText = "Ready to save App Settings.";
    private string _agentOptionsValidationText = "Agent options not validated yet.";
    private string _agentOptionsValidationDetailsText = "Validate Agent Options to review ranges and normalized privacy rules.";
    private string _agentOptionsSaveStatusText = "Ready to save Agent Options.";
    private string _agentOptionsReloadStatusText = "Agent status will be checked when Settings loads.";
    private string _normalizedExcludedProcessesText = "(none)";
    private string _normalizedExcludedTitlePatternsText = "(none)";
    private bool _hasLoadError;
    private bool _hasSaveError;
    private bool _hasValidationError;
    private bool _hasAgentOptionsValidationError;
    private bool _hasAgentOptionsSaveError;
    private bool _hasAgentOptionsReloadError;
    private bool _canReloadAgentConfig;
    private bool _canExecuteDataCleanup;
    private string _pruneDataStatusText = "Prune expired data based on retentionDays.";
    private string _lastMaintenanceStatusText = "No maintenance performed in this session.";
    private bool _isClearHistoryConfirming;
    private string _clearHistoryConfirmationInput = string.Empty;
    private string _clearHistoryStatusText = "Ready for data management.";
    private bool _hasClearHistoryError;

    private bool _isDirty;
    private bool _isLoading;
    private bool _isSaving;
    private string _refreshIntervalSecondsText = "15";
    private bool _autoStartAgentWhenAppStarts;
    private string _autoStartAgentWhenAppStartsText = "Disabled";
    private bool _startAppOnWindowsLogin;
    private string _startAppOnWindowsLoginText = "Disabled";
    private string _lastSelectedPageText = "Dashboard";
    private string _samplingIntervalSecondsText = "3";
    private string _idleThresholdSecondsText = "60";
    private string _heartbeatIntervalSecondsText = "3";
    private string _staleThresholdSecondsText = "15";
    private string _retentionDaysText = "30";
    private string _idleSummaryIntervalMinutesText = "5";
    private bool _enableJsonlJournal = true;
    private string _enableJsonlJournalText = "Enabled";
    private bool _enableAgentEventJournal = true;
    private string _enableAgentEventJournalText = "Enabled";
    private bool _enableSessionMerge = true;
    private string _enableSessionMergeText = "Enabled";
    private bool _maskWindowTitles = true;
    private string _maskWindowTitlesText = "Enabled";
    private string _excludedProcessesText = "(none)";
    private string _excludedTitlePatternsText = "(none)";
    private string _useMockCaptureText = "Disabled";

    public SettingsViewModel(SettingsService settingsService, WindowsAgentPaths paths)
        : this(
            settingsService.ReadAppSettingsAsync,
            settingsService.SaveAppSettingsAsync,
            settingsService.ReadAgentOptionsAsync,
            settingsService.SaveAgentOptionsWithBackupAsync,
            settingsService.RestoreAgentOptionsBackupAsync,
            null,
            null,
            null,
            null,
            null,
            new AgentOptionsValidator(),
            paths)
    {
    }

    public SettingsViewModel(
        SettingsService settingsService,
        AgentStatusService statusService,
        AgentControlService controlService,
        DiagnosticsDataService diagnosticsDataService,
        WindowsAgentPaths paths)
        : this(
            settingsService.ReadAppSettingsAsync,
            settingsService.SaveAppSettingsAsync,
            settingsService.ReadAgentOptionsAsync,
            settingsService.SaveAgentOptionsWithBackupAsync,
            settingsService.RestoreAgentOptionsBackupAsync,
            statusService.GetStatusAsync,
            controlService.ReloadConfigAsync,
            controlService.PruneDataAsync,
            controlService.ClearHistoryAsync,
            ct => diagnosticsDataService.GetRecentEventsAsync(cancellationToken: ct),
            new AgentOptionsValidator(),
            paths)
    {
    }

    public SettingsViewModel(
        Func<CancellationToken, Task<AppSettings>> readAppSettingsAsync,
        Func<AppSettings, CancellationToken, Task> saveAppSettingsAsync,
        Func<CancellationToken, Task<WindowsAgentOptions>> readAgentOptionsAsync,
        WindowsAgentPaths paths)
        : this(readAppSettingsAsync, saveAppSettingsAsync, readAgentOptionsAsync, new AgentOptionsValidator(), paths)
    {
    }

    public SettingsViewModel(
        Func<CancellationToken, Task<AppSettings>> readAppSettingsAsync,
        Func<AppSettings, CancellationToken, Task> saveAppSettingsAsync,
        Func<CancellationToken, Task<WindowsAgentOptions>> readAgentOptionsAsync,
        AgentOptionsValidator agentOptionsValidator,
        WindowsAgentPaths paths)
        : this(
            readAppSettingsAsync,
            saveAppSettingsAsync,
            readAgentOptionsAsync,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            null,
            null,
            null,
            null,
            null,
            agentOptionsValidator,
            paths)
    {
    }

    public SettingsViewModel(
        Func<CancellationToken, Task<AppSettings>> readAppSettingsAsync,
        Func<AppSettings, CancellationToken, Task> saveAppSettingsAsync,
        Func<CancellationToken, Task<WindowsAgentOptions>> readAgentOptionsAsync,
        Func<WindowsAgentOptions, CancellationToken, Task> saveAgentOptionsAsync,
        Func<CancellationToken, Task> restoreAgentOptionsBackupAsync,
        WindowsAgentPaths paths)
        : this(readAppSettingsAsync, saveAppSettingsAsync, readAgentOptionsAsync, saveAgentOptionsAsync, restoreAgentOptionsBackupAsync, null, null, null, null, null, new AgentOptionsValidator(), paths)
    {
    }

    public SettingsViewModel(
        Func<CancellationToken, Task<AppSettings>> readAppSettingsAsync,
        Func<AppSettings, CancellationToken, Task> saveAppSettingsAsync,
        Func<CancellationToken, Task<WindowsAgentOptions>> readAgentOptionsAsync,
        Func<WindowsAgentOptions, CancellationToken, Task> saveAgentOptionsAsync,
        Func<CancellationToken, Task> restoreAgentOptionsBackupAsync,
        Func<CancellationToken, Task<AgentStatusSnapshot>>? getAgentStatusAsync,
        Func<CancellationToken, Task<AgentCommandResult>>? requestReloadConfigAsync,
        Func<CancellationToken, Task<AgentCommandResult>>? requestPruneDataAsync,
        Func<CancellationToken, Task<AgentCommandResult>>? requestClearHistoryAsync,
        Func<CancellationToken, Task<IReadOnlyList<AgentEvent>>>? getRecentAgentEventsAsync,
        AgentOptionsValidator agentOptionsValidator,
        WindowsAgentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(readAppSettingsAsync);
        ArgumentNullException.ThrowIfNull(saveAppSettingsAsync);
        ArgumentNullException.ThrowIfNull(readAgentOptionsAsync);
        ArgumentNullException.ThrowIfNull(saveAgentOptionsAsync);
        ArgumentNullException.ThrowIfNull(restoreAgentOptionsBackupAsync);
        ArgumentNullException.ThrowIfNull(agentOptionsValidator);
        ArgumentNullException.ThrowIfNull(paths);

        _readAppSettingsAsync = readAppSettingsAsync;
        _saveAppSettingsAsync = saveAppSettingsAsync;
        _readAgentOptionsAsync = readAgentOptionsAsync;
        _saveAgentOptionsAsync = saveAgentOptionsAsync;
        _restoreAgentOptionsBackupAsync = restoreAgentOptionsBackupAsync;
        _getAgentStatusAsync = getAgentStatusAsync;
        _requestReloadConfigAsync = requestReloadConfigAsync;
        _requestPruneDataAsync = requestPruneDataAsync;
        _requestClearHistoryAsync = requestClearHistoryAsync;
        _getRecentAgentEventsAsync = getRecentAgentEventsAsync;
        _agentOptionsValidator = agentOptionsValidator;
        _paths = paths;

        AppSettingsPathText = Path.Combine(_paths.ConfigDir, "app-settings.json");
        AgentOptionsPathText = _paths.AgentOptionsPath;
        DataRootText = _paths.Root;
        ConfigDirectoryText = _paths.ConfigDir;
        DatabasePathText = _paths.DatabasePath;
        LogsDirectoryText = _paths.LogsDir;
        RuntimeDirectoryText = _paths.RuntimeDir;

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading && !IsSaving);
        SaveAppSettingsCommand = new AsyncRelayCommand(SaveAppSettingsAsync, () => !IsLoading && !IsSaving);
        SaveAgentOptionsCommand = new AsyncRelayCommand(SaveAgentOptionsAsync, () => !IsLoading && !IsSaving);
        SaveAndReloadAgentOptionsCommand = new AsyncRelayCommand(SaveAndReloadAgentOptionsAsync, () => !IsLoading && !IsSaving && CanReloadAgentConfig);
        ReloadAgentConfigCommand = new AsyncRelayCommand(ReloadAgentConfigAsync, () => !IsLoading && !IsSaving && CanReloadAgentConfig);
        RestoreAgentOptionsBackupCommand = new AsyncRelayCommand(RestoreAgentOptionsBackupAsync, () => !IsLoading && !IsSaving);
        ValidateAgentOptionsCommand = new RelayCommand(ValidateAgentOptions, () => !IsLoading && !IsSaving);
        ResetAgentOptionsEditorCommand = new RelayCommand(ResetAgentOptionsEditor, () => !IsLoading && !IsSaving);
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync, () => !IsLoading && !IsSaving && CanExecuteDataCleanup);
        ConfirmClearHistoryCommand = new AsyncRelayCommand(ConfirmClearHistoryAsync, () => !IsLoading && !IsSaving && CanExecuteDataCleanup && !string.IsNullOrWhiteSpace(ClearHistoryConfirmationInput));
        PruneDataCommand = new AsyncRelayCommand(PruneDataAsync, () => !IsLoading && !IsSaving && CanExecuteDataCleanup);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(_paths.Root));
        OpenLogsFolderCommand = new RelayCommand(() => OpenFolder(_paths.LogsDir));
        OpenConfigFolderCommand = new RelayCommand(() => OpenFolder(_paths.ConfigDir));
    }

    public AppSettings AppSettings
    {
        get => _appSettings;
        private set => SetProperty(ref _appSettings, value);
    }

    public WindowsAgentOptions AgentOptions
    {
        get => _agentOptions;
        private set
        {
            if (SetProperty(ref _agentOptions, value))
            {
                OnPropertyChanged(nameof(CurrentRetentionDaysText));
            }
        }
    }

    public string AppSettingsPathText { get; }

    public string AgentOptionsPathText { get; }

    public string DataRootText { get; }

    public string ConfigDirectoryText { get; }

    public string DatabasePathText { get; }

    public string LogsDirectoryText { get; }

    public string RuntimeDirectoryText { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand SaveAppSettingsCommand { get; }

    public IAsyncRelayCommand SaveAgentOptionsCommand { get; }

    public IAsyncRelayCommand SaveAndReloadAgentOptionsCommand { get; }

    public IAsyncRelayCommand ReloadAgentConfigCommand { get; }

    public IAsyncRelayCommand RestoreAgentOptionsBackupCommand { get; }

    public IRelayCommand ValidateAgentOptionsCommand { get; }

    public IRelayCommand ResetAgentOptionsEditorCommand { get; }

    public IAsyncRelayCommand ClearHistoryCommand { get; }

    public IAsyncRelayCommand ConfirmClearHistoryCommand { get; }

    public IAsyncRelayCommand PruneDataCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public ICommand OpenLogsFolderCommand { get; }

    public ICommand OpenConfigFolderCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string EmptyStateText
    {
        get => _emptyStateText;
        private set => SetProperty(ref _emptyStateText, value);
    }

    public string SaveStatusText
    {
        get => _saveStatusText;
        private set => SetProperty(ref _saveStatusText, value);
    }

    public string AgentOptionsValidationText
    {
        get => _agentOptionsValidationText;
        private set => SetProperty(ref _agentOptionsValidationText, value);
    }

    public string AgentOptionsValidationDetailsText
    {
        get => _agentOptionsValidationDetailsText;
        private set => SetProperty(ref _agentOptionsValidationDetailsText, value);
    }

    public string AgentOptionsSaveStatusText
    {
        get => _agentOptionsSaveStatusText;
        private set => SetProperty(ref _agentOptionsSaveStatusText, value);
    }

    public string AgentOptionsReloadStatusText
    {
        get => _agentOptionsReloadStatusText;
        private set => SetProperty(ref _agentOptionsReloadStatusText, value);
    }

    public bool CanReloadAgentConfig
    {
        get => _canReloadAgentConfig;
        private set
        {
            if (SetProperty(ref _canReloadAgentConfig, value))
            {
                SaveAndReloadAgentOptionsCommand.NotifyCanExecuteChanged();
                ReloadAgentConfigCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanExecuteDataCleanup
    {
        get => _canExecuteDataCleanup;
        private set
        {
            if (SetProperty(ref _canExecuteDataCleanup, value))
            {
                PruneDataCommand.NotifyCanExecuteChanged();
                ClearHistoryCommand.NotifyCanExecuteChanged();
                ConfirmClearHistoryCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasAgentOptionsReloadError
    {
        get => _hasAgentOptionsReloadError;
        private set => SetProperty(ref _hasAgentOptionsReloadError, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public string NormalizedExcludedProcessesText
    {
        get => _normalizedExcludedProcessesText;
        private set => SetProperty(ref _normalizedExcludedProcessesText, value);
    }

    public string NormalizedExcludedTitlePatternsText
    {
        get => _normalizedExcludedTitlePatternsText;
        private set => SetProperty(ref _normalizedExcludedTitlePatternsText, value);
    }

    public bool HasLoadError
    {
        get => _hasLoadError;
        private set => SetProperty(ref _hasLoadError, value);
    }

    public bool HasSaveError
    {
        get => _hasSaveError;
        private set => SetProperty(ref _hasSaveError, value);
    }

    public bool HasValidationError
    {
        get => _hasValidationError;
        private set => SetProperty(ref _hasValidationError, value);
    }

    public bool HasAgentOptionsValidationError
    {
        get => _hasAgentOptionsValidationError;
        private set => SetProperty(ref _hasAgentOptionsValidationError, value);
    }

    public bool HasAgentOptionsSaveError
    {
        get => _hasAgentOptionsSaveError;
        private set => SetProperty(ref _hasAgentOptionsSaveError, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
                SaveAppSettingsCommand.NotifyCanExecuteChanged();
                SaveAgentOptionsCommand.NotifyCanExecuteChanged();
                SaveAndReloadAgentOptionsCommand.NotifyCanExecuteChanged();
                ReloadAgentConfigCommand.NotifyCanExecuteChanged();
                RestoreAgentOptionsBackupCommand.NotifyCanExecuteChanged();
                ValidateAgentOptionsCommand.NotifyCanExecuteChanged();
                ResetAgentOptionsEditorCommand.NotifyCanExecuteChanged();
                ClearHistoryCommand.NotifyCanExecuteChanged();
                ConfirmClearHistoryCommand.NotifyCanExecuteChanged();
                PruneDataCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                SaveAppSettingsCommand.NotifyCanExecuteChanged();
                RefreshCommand.NotifyCanExecuteChanged();
                SaveAgentOptionsCommand.NotifyCanExecuteChanged();
                SaveAndReloadAgentOptionsCommand.NotifyCanExecuteChanged();
                ReloadAgentConfigCommand.NotifyCanExecuteChanged();
                RestoreAgentOptionsBackupCommand.NotifyCanExecuteChanged();
                ValidateAgentOptionsCommand.NotifyCanExecuteChanged();
                ResetAgentOptionsEditorCommand.NotifyCanExecuteChanged();
                ClearHistoryCommand.NotifyCanExecuteChanged();
                ConfirmClearHistoryCommand.NotifyCanExecuteChanged();
                PruneDataCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string RefreshIntervalSecondsText
    {
        get => _refreshIntervalSecondsText;
        set
        {
            if (SetProperty(ref _refreshIntervalSecondsText, value))
            {
                IsDirty = true;
            }
        }
    }

    public bool AutoStartAgentWhenAppStarts
    {
        get => _autoStartAgentWhenAppStarts;
        set
        {
            if (SetProperty(ref _autoStartAgentWhenAppStarts, value))
            {
                AutoStartAgentWhenAppStartsText = FormatSwitch(value);
                IsDirty = true;
            }
        }
    }

    public string AutoStartAgentWhenAppStartsText
    {
        get => _autoStartAgentWhenAppStartsText;
        private set => SetProperty(ref _autoStartAgentWhenAppStartsText, value);
    }

    public bool StartAppOnWindowsLogin
    {
        get => _startAppOnWindowsLogin;
        set
        {
            if (SetProperty(ref _startAppOnWindowsLogin, value))
            {
                StartAppOnWindowsLoginText = FormatSwitch(value);
                IsDirty = true;
            }
        }
    }

    public string StartAppOnWindowsLoginText
    {
        get => _startAppOnWindowsLoginText;
        private set => SetProperty(ref _startAppOnWindowsLoginText, value);
    }

    public string LastSelectedPageText
    {
        get => _lastSelectedPageText;
        set => SetProperty(ref _lastSelectedPageText, value);
    }

    public string SamplingIntervalSecondsText
    {
        get => _samplingIntervalSecondsText;
        set
        {
            if (SetProperty(ref _samplingIntervalSecondsText, value))
            {
                IsDirty = true;
            }
        }
    }

    public string IdleThresholdSecondsText
    {
        get => _idleThresholdSecondsText;
        set
        {
            if (SetProperty(ref _idleThresholdSecondsText, value))
            {
                IsDirty = true;
            }
        }
    }

    public string HeartbeatIntervalSecondsText
    {
        get => _heartbeatIntervalSecondsText;
        set
        {
            if (SetProperty(ref _heartbeatIntervalSecondsText, value))
            {
                IsDirty = true;
            }
        }
    }

    public string StaleThresholdSecondsText
    {
        get => _staleThresholdSecondsText;
        set
        {
            if (SetProperty(ref _staleThresholdSecondsText, value))
            {
                IsDirty = true;
            }
        }
    }

    public string RetentionDaysText
    {
        get => _retentionDaysText;
        set
        {
            if (SetProperty(ref _retentionDaysText, value))
            {
                IsDirty = true;
            }
        }
    }

    public string IdleSummaryIntervalMinutesText
    {
        get => _idleSummaryIntervalMinutesText;
        private set => SetProperty(ref _idleSummaryIntervalMinutesText, value);
    }

    public bool EnableJsonlJournal
    {
        get => _enableJsonlJournal;
        set
        {
            if (SetProperty(ref _enableJsonlJournal, value))
            {
                EnableJsonlJournalText = FormatSwitch(value);
                IsDirty = true;
            }
        }
    }

    public string EnableJsonlJournalText
    {
        get => _enableJsonlJournalText;
        private set => SetProperty(ref _enableJsonlJournalText, value);
    }

    public bool EnableAgentEventJournal
    {
        get => _enableAgentEventJournal;
        set
        {
            if (SetProperty(ref _enableAgentEventJournal, value))
            {
                EnableAgentEventJournalText = FormatSwitch(value);
                IsDirty = true;
            }
        }
    }

    public string EnableAgentEventJournalText
    {
        get => _enableAgentEventJournalText;
        private set => SetProperty(ref _enableAgentEventJournalText, value);
    }

    public bool EnableSessionMerge
    {
        get => _enableSessionMerge;
        set
        {
            if (SetProperty(ref _enableSessionMerge, value))
            {
                EnableSessionMergeText = FormatSwitch(value);
                IsDirty = true;
            }
        }
    }

    public string EnableSessionMergeText
    {
        get => _enableSessionMergeText;
        private set => SetProperty(ref _enableSessionMergeText, value);
    }

    public bool MaskWindowTitles
    {
        get => _maskWindowTitles;
        set
        {
            if (SetProperty(ref _maskWindowTitles, value))
            {
                MaskWindowTitlesText = FormatSwitch(value);
                IsDirty = true;
            }
        }
    }

    public string MaskWindowTitlesText
    {
        get => _maskWindowTitlesText;
        private set => SetProperty(ref _maskWindowTitlesText, value);
    }

    public string ExcludedProcessesText
    {
        get => _excludedProcessesText;
        set
        {
            if (SetProperty(ref _excludedProcessesText, value))
            {
                IsDirty = true;
            }
        }
    }

    public string ExcludedTitlePatternsText
    {
        get => _excludedTitlePatternsText;
        set
        {
            if (SetProperty(ref _excludedTitlePatternsText, value))
            {
                IsDirty = true;
            }
        }
    }

    public string UseMockCaptureText
    {
        get => _useMockCaptureText;
        private set => SetProperty(ref _useMockCaptureText, value);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsLoading = true;

            var errors = new List<string>();

            try
            {
                AppSettings = await _readAppSettingsAsync(cancellationToken) ?? new AppSettings();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(FormatLoadFailure("App settings", ex));
            }

            try
            {
                AgentOptions = await _readAgentOptionsAsync(cancellationToken) ?? new WindowsAgentOptions();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(FormatLoadFailure("Agent options", ex));
            }

            UpdatePresentation();

            HasLoadError = errors.Count > 0;
            StatusText = errors.Count == 0
                ? "Settings loaded."
                : string.Join(" ", errors);
            EmptyStateText = errors.Count == 0
                ? "Manage App Settings and Agent Options from this page."
                : "Settings could not be fully loaded. Refresh to retry.";
            SaveStatusText = "Ready to save App Settings.";
            AgentOptionsSaveStatusText = "Ready to save Agent Options.";
            HasSaveError = false;
            HasValidationError = false;
            HasAgentOptionsSaveError = false;
            HasAgentOptionsReloadError = false;
            IsDirty = false;

            await UpdateAgentStatusAsync(cancellationToken);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SaveAppSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsSaving = true;

            if (!TryBuildAppSettings(out var appSettings, out var validationMessage))
            {
                HasValidationError = true;
                HasSaveError = false;
                SaveStatusText = validationMessage;
                return;
            }

            HasValidationError = false;
            await _saveAppSettingsAsync(appSettings, cancellationToken);
            AppSettings = appSettings;
            ApplyAppSettingsToEditor(appSettings);
            SaveStatusText = "App settings saved.";
            HasSaveError = false;
            IsDirty = false;
            AppSettingsSaved?.Invoke(appSettings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            HasSaveError = true;
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            SaveStatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "App settings save failed."
                : $"App settings save failed: {safeMessage}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    public event Action<AppSettings>? AppSettingsSaved;

    public async Task SaveAgentOptionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsSaving = true;

            var draft = BuildAgentOptionsDraft(out var parseErrors);
            if (parseErrors.Count > 0)
            {
                SetAgentOptionsValidationFailure(parseErrors);
                AgentOptionsSaveStatusText = "Cannot save: fix validation errors first.";
                HasAgentOptionsSaveError = false;
                return;
            }

            var validationResult = _agentOptionsValidator.Validate(draft);
            if (!validationResult.IsValid)
            {
                SetAgentOptionsValidationFailure(validationResult.Errors);
                AgentOptionsSaveStatusText = "Cannot save: fix validation errors first.";
                HasAgentOptionsSaveError = false;
                RefreshNormalizedPreview(validationResult.NormalizedOptions);
                return;
            }

            var normalized = validationResult.NormalizedOptions;
            await _saveAgentOptionsAsync(normalized, cancellationToken);

            AgentOptions = normalized;
            ApplyAgentOptionsToEditor(normalized);
            HasAgentOptionsValidationError = false;
            HasAgentOptionsSaveError = false;
            AgentOptionsValidationText = "Agent options saved.";
            AgentOptionsValidationDetailsText = "Configuration file written. The running Agent has not applied it yet; use ReloadConfig or restart the Agent.";
            AgentOptionsSaveStatusText = "Saved to file. Running Agent has not applied the change; ReloadConfig or next Agent start required.";
            RefreshNormalizedPreview(normalized);
            IsDirty = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            HasAgentOptionsSaveError = true;
            HasAgentOptionsValidationError = false;
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            AgentOptionsSaveStatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "Agent options save failed."
                : $"Agent options save failed: {safeMessage}";
            AgentOptionsValidationText = "Agent options save failed.";
            AgentOptionsValidationDetailsText = AgentOptionsSaveStatusText;
        }
        finally
        {
            IsSaving = false;
        }
    }

    public async Task SaveAndReloadAgentOptionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsSaving = true;
            HasAgentOptionsReloadError = false;

            var draft = BuildAgentOptionsDraft(out var parseErrors);
            if (parseErrors.Count > 0)
            {
                SetAgentOptionsValidationFailure(parseErrors);
                AgentOptionsSaveStatusText = "Cannot save: fix validation errors first.";
                AgentOptionsReloadStatusText = "Cannot reload: current draft has validation errors.";
                HasAgentOptionsReloadError = true;
                return;
            }

            var validationResult = _agentOptionsValidator.Validate(draft);
            if (!validationResult.IsValid)
            {
                SetAgentOptionsValidationFailure(validationResult.Errors);
                AgentOptionsSaveStatusText = "Cannot save: fix validation errors first.";
                AgentOptionsReloadStatusText = "Cannot reload: current draft has validation errors.";
                HasAgentOptionsReloadError = true;
                RefreshNormalizedPreview(validationResult.NormalizedOptions);
                return;
            }

            var normalized = validationResult.NormalizedOptions;
            await _saveAgentOptionsAsync(normalized, cancellationToken);

            AgentOptions = normalized;
            ApplyAgentOptionsToEditor(normalized);
            HasAgentOptionsValidationError = false;
            HasAgentOptionsSaveError = false;
            AgentOptionsValidationText = "Agent options saved.";
            AgentOptionsValidationDetailsText = "Configuration file written. Sending ReloadConfig to running Agent.";
            AgentOptionsSaveStatusText = "Saved to file. Sending ReloadConfig to running Agent.";
            RefreshNormalizedPreview(normalized);
            IsDirty = false;

            if (_requestReloadConfigAsync is null)
            {
                AgentOptionsReloadStatusText = "ReloadConfig is not available in this configuration.";
                HasAgentOptionsReloadError = true;
                return;
            }

            var result = await _requestReloadConfigAsync(cancellationToken);
            AgentOptionsReloadStatusText = "ReloadConfig command queued. Waiting for Agent to apply the saved configuration...";
            await PollForReloadConfigResultAsync(result.RequestId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            HasAgentOptionsReloadError = true;
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            AgentOptionsReloadStatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "Save and Reload failed."
                : $"Save and Reload failed: {safeMessage}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    public async Task ReloadAgentConfigAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsSaving = true;
            HasAgentOptionsReloadError = false;

            if (_requestReloadConfigAsync is null)
            {
                AgentOptionsReloadStatusText = "ReloadConfig is not available in this configuration.";
                HasAgentOptionsReloadError = true;
                return;
            }

            var result = await _requestReloadConfigAsync(cancellationToken);
            AgentOptionsReloadStatusText = "ReloadConfig command queued. Waiting for Agent to reload the saved configuration...";
            await PollForReloadConfigResultAsync(result.RequestId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            HasAgentOptionsReloadError = true;
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            AgentOptionsReloadStatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "ReloadConfig request failed."
                : $"ReloadConfig request failed: {safeMessage}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task PollForReloadConfigResultAsync(string requestId, CancellationToken cancellationToken)
    {
        if (_getRecentAgentEventsAsync is null)
        {
            AgentOptionsReloadStatusText = "ReloadConfig command queued. Agent event store is unavailable; check Diagnostics for the result.";
            return;
        }

        var pollDelay = ReloadConfigPollDelay;
        var requestTime = DateTime.UtcNow.AddSeconds(-2);

        for (var attempt = 0; attempt < ReloadConfigPollMaxAttempts; attempt++)
        {
            if (pollDelay > TimeSpan.Zero)
            {
                await Task.Delay(pollDelay, cancellationToken);
            }

            IReadOnlyList<AgentEvent> events;
            try
            {
                events = await _getRecentAgentEventsAsync(cancellationToken);
            }
            catch
            {
                continue;
            }

            var failureEvent = events.FirstOrDefault(e =>
                e.EventType == AgentEventType.CommandFailed &&
                string.Equals(e.RequestId, requestId, StringComparison.OrdinalIgnoreCase));
            if (failureEvent is not null)
            {
                HasAgentOptionsReloadError = true;
                var code = failureEvent.ErrorCode;
                var message = failureEvent.Message;
                var detail = string.IsNullOrWhiteSpace(message)
                    ? (string.IsNullOrWhiteSpace(code) ? "" : $" ({code})")
                    : $" ({code}): {message}";
                AgentOptionsReloadStatusText = string.IsNullOrWhiteSpace(detail)
                    ? "ReloadConfig failed."
                    : $"ReloadConfig failed{detail}";
                return;
            }

            var completedEvent = events.FirstOrDefault(e =>
                e.EventType == AgentEventType.CommandCompleted &&
                string.Equals(e.RequestId, requestId, StringComparison.OrdinalIgnoreCase));
            if (completedEvent is not null)
            {
                var reloadedEvent = events.FirstOrDefault(e =>
                    e.EventType == AgentEventType.ConfigReloaded &&
                    string.Equals(e.RequestId, requestId, StringComparison.OrdinalIgnoreCase));
                if (reloadedEvent is not null)
                {
                    AgentOptionsReloadStatusText = "ReloadConfig succeeded: Agent applied the saved configuration.";
                    return;
                }
            }
        }

        AgentOptionsReloadStatusText = "ReloadConfig command queued. Agent did not report completion within the polling window; check Diagnostics for the result.";
    }

    public async Task RestoreAgentOptionsBackupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsSaving = true;

            await _restoreAgentOptionsBackupAsync(cancellationToken);
            AgentOptions = await _readAgentOptionsAsync(cancellationToken) ?? new WindowsAgentOptions();
            ApplyAgentOptionsToEditor(AgentOptions);

            HasAgentOptionsValidationError = false;
            HasAgentOptionsSaveError = false;
            AgentOptionsValidationText = "Agent options restored from backup.";
            AgentOptionsValidationDetailsText = "Configuration file restored. The running Agent has not applied it yet; use ReloadConfig or restart the Agent.";
            AgentOptionsSaveStatusText = "Restored from backup. Running Agent has not applied the change; ReloadConfig or next Agent start required.";
            IsDirty = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            HasAgentOptionsSaveError = true;
            HasAgentOptionsValidationError = false;
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            AgentOptionsSaveStatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "Agent options restore failed."
                : $"Agent options restore failed: {safeMessage}";
            AgentOptionsValidationText = "Agent options restore failed.";
            AgentOptionsValidationDetailsText = AgentOptionsSaveStatusText;
        }
        finally
        {
            IsSaving = false;
        }
    }

    public void ValidateAgentOptions()
    {
        var draft = BuildAgentOptionsDraft(out var parseErrors);
        var validationResult = _agentOptionsValidator.Validate(draft);

        var issues = parseErrors
            .Concat(validationResult.Issues.Select(issue => issue.SafeText))
            .ToArray();

        HasAgentOptionsValidationError = issues.Length > 0;
        AgentOptionsValidationText = issues.Length == 0
            ? "Agent options are valid."
            : "Agent options validation failed.";
        AgentOptionsValidationDetailsText = issues.Length == 0
            ? "Normalized preview updated."
            : string.Join(Environment.NewLine, issues);
        RefreshNormalizedPreview(validationResult.NormalizedOptions);
        HasAgentOptionsSaveError = false;
    }

    public void ResetAgentOptionsEditor()
    {
        ApplyAgentOptionsToEditor(AgentOptions);
        AgentOptionsValidationText = "Agent options editor reset to loaded values.";
        AgentOptionsValidationDetailsText = "Validate Agent Options to review ranges and normalized privacy rules.";
        HasAgentOptionsValidationError = false;
        HasAgentOptionsSaveError = false;
        IsDirty = false;
    }

    /// <summary>
    /// Called by MainWindowViewModel when Agent status is applied (from polling or full refresh).
    /// Uses the shared AgentCommandAvailability for consistency with MainWindow buttons.
    /// </summary>
    public void UpdateAgentStatus(AgentStatusSnapshot status)
    {
        var availability = AgentCommandAvailability.FromStatus(status);
        CanReloadAgentConfig = availability.CanReloadConfigNow;
        CanExecuteDataCleanup = availability.CanPruneData; // same rule as CanClearHistory
        AgentOptionsReloadStatusText = availability.ReloadConfigStatusText;
    }

    private async Task UpdateAgentStatusAsync(CancellationToken cancellationToken)
    {
        if (_getAgentStatusAsync is null)
        {
            CanReloadAgentConfig = false;
            CanExecuteDataCleanup = false;
            AgentOptionsReloadStatusText = "Agent status unavailable.";
            return;
        }

        try
        {
            var status = await _getAgentStatusAsync(cancellationToken);
            UpdateAgentStatus(status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            CanReloadAgentConfig = false;
            CanExecuteDataCleanup = false;
            AgentOptionsReloadStatusText = "Unable to determine Agent status. Reload is unavailable.";
        }
    }

    private void UpdatePresentation()
    {
        ApplyAppSettingsToEditor(AppSettings);
        ApplyAgentOptionsToEditor(AgentOptions);
        OnPropertyChanged(nameof(CurrentRetentionDaysText));
    }

    private void ApplyAgentOptionsToEditor(WindowsAgentOptions agentOptions)
    {
        SamplingIntervalSecondsText = agentOptions.SamplingIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        IdleThresholdSecondsText = agentOptions.IdleThresholdSeconds.ToString(CultureInfo.InvariantCulture);
        HeartbeatIntervalSecondsText = agentOptions.HeartbeatIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        StaleThresholdSecondsText = agentOptions.StaleThresholdSeconds.ToString(CultureInfo.InvariantCulture);
        RetentionDaysText = agentOptions.RetentionDays.ToString(CultureInfo.InvariantCulture);
        IdleSummaryIntervalMinutesText = agentOptions.IdleSummaryIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        EnableJsonlJournal = agentOptions.EnableJsonlJournal;
        EnableAgentEventJournal = agentOptions.EnableAgentEventJournal;
        EnableSessionMerge = agentOptions.EnableSessionMerge;
        MaskWindowTitles = agentOptions.MaskWindowTitles;
        ExcludedProcessesText = FormatList(agentOptions.ExcludedProcesses);
        ExcludedTitlePatternsText = FormatList(agentOptions.ExcludedTitlePatterns);
        UseMockCaptureText = FormatSwitch(agentOptions.UseMockCapture);
        RefreshNormalizedPreview(agentOptions);
        AgentOptionsValidationText = "Ready to validate Agent options.";
        AgentOptionsValidationDetailsText = "Validate Agent Options to review ranges and normalized privacy rules.";
        HasAgentOptionsValidationError = false;
        HasAgentOptionsSaveError = false;
    }

    private void RefreshNormalizedPreview(WindowsAgentOptions agentOptions)
    {
        var normalized = _agentOptionsValidator.Validate(agentOptions).NormalizedOptions;
        NormalizedExcludedProcessesText = FormatList(normalized.ExcludedProcesses);
        NormalizedExcludedTitlePatternsText = FormatList(normalized.ExcludedTitlePatterns);
    }

    private WindowsAgentOptions BuildAgentOptionsDraft(out List<string> parseErrors)
    {
        parseErrors = new List<string>();

        var samplingIntervalSeconds = ParseEditorInt(
            SamplingIntervalSecondsText,
            "samplingIntervalSeconds",
            parseErrors,
            _agentOptions.SamplingIntervalSeconds);

        var idleThresholdSeconds = ParseEditorInt(
            IdleThresholdSecondsText,
            "idleThresholdSeconds",
            parseErrors,
            _agentOptions.IdleThresholdSeconds);

        var heartbeatIntervalSeconds = ParseEditorInt(
            HeartbeatIntervalSecondsText,
            "heartbeatIntervalSeconds",
            parseErrors,
            _agentOptions.HeartbeatIntervalSeconds);

        var staleThresholdSeconds = ParseEditorInt(
            StaleThresholdSecondsText,
            "staleThresholdSeconds",
            parseErrors,
            _agentOptions.StaleThresholdSeconds);

        var retentionDays = ParseEditorInt(
            RetentionDaysText,
            "retentionDays",
            parseErrors,
            _agentOptions.RetentionDays);

        return new WindowsAgentOptions
        {
            SamplingIntervalSeconds = samplingIntervalSeconds,
            IdleThresholdSeconds = idleThresholdSeconds,
            IdleSummaryIntervalMinutes = _agentOptions.IdleSummaryIntervalMinutes,
            RetentionDays = retentionDays,
            HeartbeatIntervalSeconds = heartbeatIntervalSeconds,
            StaleThresholdSeconds = staleThresholdSeconds,
            UseMockCapture = _agentOptions.UseMockCapture,
            EnableJsonlJournal = EnableJsonlJournal,
            EnableAgentEventJournal = EnableAgentEventJournal,
            EnableSessionMerge = EnableSessionMerge,
            MaskWindowTitles = MaskWindowTitles,
            ExcludedProcesses = ParseMultilineText(ExcludedProcessesText),
            ExcludedTitlePatterns = ParseMultilineText(ExcludedTitlePatternsText)
        };
    }

    private void SetAgentOptionsValidationFailure(IEnumerable<string> issues)
    {
        HasAgentOptionsValidationError = true;
        AgentOptionsValidationText = "Agent options validation failed.";
        AgentOptionsValidationDetailsText = string.Join(Environment.NewLine, issues);
    }

    private static int ParseEditorInt(
        string? value,
        string fieldName,
        ICollection<string> parseErrors,
        int fallbackValue)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        parseErrors.Add($"{fieldName} must be an integer.");
        return fallbackValue;
    }

    private static List<string> ParseMultilineText(string? value)
    {
        var items = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return items;
        }

        using var reader = new StringReader(value);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            items.Add(line);
        }

        return items;
    }

    private void ApplyAppSettingsToEditor(AppSettings appSettings)
    {
        RefreshIntervalSecondsText = appSettings.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        AutoStartAgentWhenAppStarts = appSettings.AutoStartAgentWhenAppStarts;
        StartAppOnWindowsLogin = appSettings.StartAppOnWindowsLogin;
        LastSelectedPageText = NormalizeLastSelectedPage(appSettings.LastSelectedPage);
    }

    private bool TryBuildAppSettings(out AppSettings appSettings, out string validationMessage)
    {
        appSettings = new AppSettings();
        validationMessage = string.Empty;

        if (!int.TryParse(RefreshIntervalSecondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var refreshIntervalSeconds))
        {
            validationMessage = "Refresh interval must be an integer between 5 and 300 seconds.";
            return false;
        }

        if (refreshIntervalSeconds < 5 || refreshIntervalSeconds > 300)
        {
            validationMessage = "Refresh interval must be an integer between 5 and 300 seconds.";
            return false;
        }

        appSettings = new AppSettings
        {
            AutoStartAgentWhenAppStarts = AutoStartAgentWhenAppStarts,
            StartAppOnWindowsLogin = StartAppOnWindowsLogin,
            RefreshIntervalSeconds = refreshIntervalSeconds,
            LastSelectedPage = AppSettings.LastSelectedPage,
            MinimizeToTray = AppSettings.MinimizeToTray,
            CloseToTray = AppSettings.CloseToTray,
            Theme = AppSettings.Theme
        };
        return true;
    }

    private static string FormatSwitch(bool enabled)
    {
        return enabled ? "Enabled" : "Disabled";
    }

    private static string FormatList(IEnumerable<string>? items)
    {
        var values = items?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToArray() ?? [];

        return values.Length == 0
            ? "(none)"
            : string.Join(Environment.NewLine, values);
    }

    private bool IsKnownPage(string? page)
    {
        return !string.IsNullOrWhiteSpace(page)
            && LastSelectedPageOptions.Any(option => string.Equals(option, page.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private string NormalizeLastSelectedPage(string? page)
    {
        if (string.IsNullOrWhiteSpace(page))
        {
            return "Dashboard";
        }

        var trimmed = page.Trim();
        var match = LastSelectedPageOptions.FirstOrDefault(option => string.Equals(option, trimmed, StringComparison.OrdinalIgnoreCase));
        return match ?? "Dashboard";
    }

    private static string FormatLoadFailure(string section, Exception exception)
    {
        var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(exception);
        return string.IsNullOrWhiteSpace(safeMessage)
            ? $"{section} load failed."
            : $"{section} load failed: {safeMessage}.";
    }

    public bool IsClearHistoryConfirming
    {
        get => _isClearHistoryConfirming;
        set
        {
            if (SetProperty(ref _isClearHistoryConfirming, value))
            {
                ClearHistoryCommand.NotifyCanExecuteChanged();
                ConfirmClearHistoryCommand.NotifyCanExecuteChanged();
                if (!value)
                {
                    ClearHistoryConfirmationInput = string.Empty;
                }
            }
        }
    }

    public string ClearHistoryConfirmationInput
    {
        get => _clearHistoryConfirmationInput;
        set
        {
            if (SetProperty(ref _clearHistoryConfirmationInput, value))
            {
                ConfirmClearHistoryCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ClearHistoryStatusText
    {
        get => _clearHistoryStatusText;
        private set => SetProperty(ref _clearHistoryStatusText, value);
    }

    public bool HasClearHistoryError
    {
        get => _hasClearHistoryError;
        private set => SetProperty(ref _hasClearHistoryError, value);
    }

    public string PruneDataStatusText
    {
        get => _pruneDataStatusText;
        private set => SetProperty(ref _pruneDataStatusText, value);
    }

    public string LastMaintenanceStatusText
    {
        get => _lastMaintenanceStatusText;
        private set => SetProperty(ref _lastMaintenanceStatusText, value);
    }

    public string CurrentRetentionDaysText
    {
        get => _agentOptions.RetentionDays > 0
            ? $"Current retention: {_agentOptions.RetentionDays} days"
            : "Retention is disabled.";
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (IsClearHistoryConfirming)
        {
            IsClearHistoryConfirming = false;
            ClearHistoryStatusText = "Ready for data management.";
            return;
        }
        IsClearHistoryConfirming = true;
        ClearHistoryStatusText = "This will clear all historical samples, sessions, app usage, and diagnostic events. Configuration files will not be removed. This cannot be undone. Type CLEAR to continue.";
        HasClearHistoryError = false;
        await Task.CompletedTask;
    }

    public async Task ConfirmClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (ClearHistoryConfirmationInput.Trim() != "CLEAR")
        {
            ClearHistoryStatusText = "Confirmation text does not match. Type CLEAR to confirm.";
            HasClearHistoryError = true;
            return;
        }

        if (_requestClearHistoryAsync is null)
        {
            ClearHistoryStatusText = "ClearHistory is not available in this configuration.";
            HasClearHistoryError = true;
            IsClearHistoryConfirming = false;
            return;
        }

        if (!CanExecuteDataCleanup)
        {
            ClearHistoryStatusText = "Cannot clear history while the Agent is not running or is in maintenance.";
            HasClearHistoryError = true;
            IsClearHistoryConfirming = false;
            return;
        }

        try
        {
            IsSaving = true;
            HasClearHistoryError = false;
            var result = await _requestClearHistoryAsync(cancellationToken);
            if (result.Accepted && result.Completed)
            {
                ClearHistoryStatusText = "ClearHistory command queued. Check Diagnostics for results.";
            }
            else
            {
                ClearHistoryStatusText = result.Message ?? "ClearHistory request was not accepted.";
                HasClearHistoryError = true;
            }
        }
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            ClearHistoryStatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "ClearHistory request failed."
                : $"ClearHistory request failed: {safeMessage}";
            HasClearHistoryError = true;
        }
        finally
        {
            IsClearHistoryConfirming = false;
            IsSaving = false;
        }
    }

    public async Task PruneDataAsync(CancellationToken cancellationToken = default)
    {
        if (_requestPruneDataAsync is null)
        {
            PruneDataStatusText = "PruneData is not available in this configuration.";
            return;
        }

        if (!CanExecuteDataCleanup)
        {
            PruneDataStatusText = "Cannot prune data while the Agent is not running or is in maintenance.";
            return;
        }

        try
        {
            IsSaving = true;
            var result = await _requestPruneDataAsync(cancellationToken);
            if (result.Accepted && result.Completed)
            {
                PruneDataStatusText = "PruneData command queued. Check Diagnostics for results.";
                LastMaintenanceStatusText = $"PruneData queued at {DateTime.Now:HH:mm:ss}";
            }
            else if (result.Accepted && !result.Completed)
            {
                PruneDataStatusText = result.Message ?? "PruneData request timed out. Check Diagnostics for result.";
            }
            else
            {
                PruneDataStatusText = result.Message ?? "PruneData request was not accepted.";
            }
        }
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            PruneDataStatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "PruneData request failed."
                : $"PruneData request failed: {safeMessage}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void OpenFolder(string folderPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = folderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            StatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "Unable to open the requested folder."
                : $"Unable to open the requested folder: {safeMessage}";
        }
    }
}
