using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class AppsViewModel : ObservableObject
{
    private const int AppUsageLimit = 50;

    private readonly Func<int, CancellationToken, Task<IReadOnlyList<AppUsageSummary>>> _loadAppUsageAsync;
    private string _statusText = "No app usage loaded.";
    private string _emptyStateText = "No app usage loaded.";
    private bool _hasLoadError;
    private bool _isLoading;

    public AppsViewModel(AppsDataService appsDataService)
        : this(appsDataService.GetTodayAppUsageAsync)
    {
    }

    public AppsViewModel(Func<int, CancellationToken, Task<IReadOnlyList<AppUsageSummary>>> loadAppUsageAsync)
    {
        ArgumentNullException.ThrowIfNull(loadAppUsageAsync);

        _loadAppUsageAsync = loadAppUsageAsync;
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
    }

    public ObservableCollection<AppUsageListItemViewModel> Apps { get; } = new();

    public IAsyncRelayCommand RefreshCommand { get; }

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

    public bool HasLoadError
    {
        get => _hasLoadError;
        private set => SetProperty(ref _hasLoadError, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsLoading = true;
            var summaries = await _loadAppUsageAsync(AppUsageLimit, cancellationToken);
            Apps.Clear();

            var rank = 1;
            foreach (var summary in summaries)
            {
                Apps.Add(new AppUsageListItemViewModel(summary, rank++));
            }

            HasLoadError = false;
            StatusText = Apps.Count == 0
                ? "No app usage found for today."
                : $"Showing top {Apps.Count} apps for today, ranked by active duration.";
            EmptyStateText = "暂无今日应用使用记录。Agent 运行并写入 app_sessions 后会显示在这里。";
        }
        catch (Exception ex)
        {
            Apps.Clear();
            HasLoadError = true;
            EmptyStateText = "App usage could not be loaded. Refresh to retry.";

            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            StatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "App usage load failed."
                : $"App usage load failed: {safeMessage}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
