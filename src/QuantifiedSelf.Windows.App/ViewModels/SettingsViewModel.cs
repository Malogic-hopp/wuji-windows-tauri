using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantifiedSelf.Windows.App.Services;
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
    private readonly AgentOptionsValidator _agentOptionsValidator;
    private readonly WindowsAgentPaths _paths;

    private AppSettings _appSettings = new();
    private WindowsAgentOptions _agentOptions = new();
    private string _statusText = "No settings loaded.";
    private string _emptyStateText = "No settings loaded.";
    private string _saveStatusText = "Ready to save App Settings.";
    private string _agentOptionsValidationText = "Agent options not validated yet.";
    private string _agentOptionsValidationDetailsText = "Validate Agent Options to review ranges and normalized privacy rules.";
    private string _normalizedExcludedProcessesText = "(none)";
    private string _normalizedExcludedTitlePatternsText = "(none)";
    private bool _hasLoadError;
    private bool _hasSaveError;
    private bool _hasValidationError;
    private bool _hasAgentOptionsValidationError;
    private bool _isLoading;
    private bool _isSaving;
    private string _refreshIntervalSecondsText = "15";
    private bool _autoStartAgentWhenAppStarts;
    private string _autoStartAgentWhenAppStartsText = "Disabled";
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
        : this(settingsService.ReadAppSettingsAsync, settingsService.SaveAppSettingsAsync, settingsService.ReadAgentOptionsAsync, new AgentOptionsValidator(), paths)
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
    {
        ArgumentNullException.ThrowIfNull(readAppSettingsAsync);
        ArgumentNullException.ThrowIfNull(saveAppSettingsAsync);
        ArgumentNullException.ThrowIfNull(readAgentOptionsAsync);
        ArgumentNullException.ThrowIfNull(agentOptionsValidator);
        ArgumentNullException.ThrowIfNull(paths);

        _readAppSettingsAsync = readAppSettingsAsync;
        _saveAppSettingsAsync = saveAppSettingsAsync;
        _readAgentOptionsAsync = readAgentOptionsAsync;
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
        ValidateAgentOptionsCommand = new RelayCommand(ValidateAgentOptions, () => !IsLoading && !IsSaving);
        ResetAgentOptionsEditorCommand = new RelayCommand(ResetAgentOptionsEditor, () => !IsLoading && !IsSaving);
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
        private set => SetProperty(ref _agentOptions, value);
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

    public IRelayCommand ValidateAgentOptionsCommand { get; }

    public IRelayCommand ResetAgentOptionsEditorCommand { get; }

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

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
                SaveAppSettingsCommand.NotifyCanExecuteChanged();
                ValidateAgentOptionsCommand.NotifyCanExecuteChanged();
                ResetAgentOptionsEditorCommand.NotifyCanExecuteChanged();
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
                ValidateAgentOptionsCommand.NotifyCanExecuteChanged();
                ResetAgentOptionsEditorCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string RefreshIntervalSecondsText
    {
        get => _refreshIntervalSecondsText;
        set => SetProperty(ref _refreshIntervalSecondsText, value);
    }

    public bool AutoStartAgentWhenAppStarts
    {
        get => _autoStartAgentWhenAppStarts;
        set
        {
            if (SetProperty(ref _autoStartAgentWhenAppStarts, value))
            {
                AutoStartAgentWhenAppStartsText = FormatSwitch(value);
            }
        }
    }

    public string AutoStartAgentWhenAppStartsText
    {
        get => _autoStartAgentWhenAppStartsText;
        private set => SetProperty(ref _autoStartAgentWhenAppStartsText, value);
    }

    public string LastSelectedPageText
    {
        get => _lastSelectedPageText;
        set => SetProperty(ref _lastSelectedPageText, value);
    }

    public string SamplingIntervalSecondsText
    {
        get => _samplingIntervalSecondsText;
        set => SetProperty(ref _samplingIntervalSecondsText, value);
    }

    public string IdleThresholdSecondsText
    {
        get => _idleThresholdSecondsText;
        set => SetProperty(ref _idleThresholdSecondsText, value);
    }

    public string HeartbeatIntervalSecondsText
    {
        get => _heartbeatIntervalSecondsText;
        set => SetProperty(ref _heartbeatIntervalSecondsText, value);
    }

    public string StaleThresholdSecondsText
    {
        get => _staleThresholdSecondsText;
        set => SetProperty(ref _staleThresholdSecondsText, value);
    }

    public string RetentionDaysText
    {
        get => _retentionDaysText;
        set => SetProperty(ref _retentionDaysText, value);
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
        set => SetProperty(ref _excludedProcessesText, value);
    }

    public string ExcludedTitlePatternsText
    {
        get => _excludedTitlePatternsText;
        set => SetProperty(ref _excludedTitlePatternsText, value);
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
            HasSaveError = false;
            HasValidationError = false;
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
    }

    public void ResetAgentOptionsEditor()
    {
        ApplyAgentOptionsToEditor(AgentOptions);
        AgentOptionsValidationText = "Agent options editor reset to loaded values.";
        AgentOptionsValidationDetailsText = "Validate Agent Options to review ranges and normalized privacy rules.";
        HasAgentOptionsValidationError = false;
    }

    private void UpdatePresentation()
    {
        ApplyAppSettingsToEditor(AppSettings);
        ApplyAgentOptionsToEditor(AgentOptions);
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

        if (!IsKnownPage(LastSelectedPageText))
        {
            validationMessage = $"Last selected page must be one of: {string.Join(", ", LastSelectedPageOptions)}.";
            return false;
        }

        appSettings = new AppSettings
        {
            AutoStartAgentWhenAppStarts = AutoStartAgentWhenAppStarts,
            RefreshIntervalSeconds = refreshIntervalSeconds,
            LastSelectedPage = NormalizeLastSelectedPage(LastSelectedPageText),
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
