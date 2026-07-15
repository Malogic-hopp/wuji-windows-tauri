using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class SessionsViewModel : ObservableObject
{
    private const int SessionLimit = 200;

    private readonly Func<string, int, CancellationToken, Task<IReadOnlyList<AppSession>>> _loadSessionsAsync;
    private readonly List<SessionListItemViewModel> _allSessions = new();
    private int _loadVersion;
    private string _selectedRange = "Today";
    private string _selectedCloseReason = "All";
    private string _statusText = "No sessions loaded.";
    private string _emptyStateText = "No sessions loaded.";
    private bool _hasLoadError;
    private bool _isLoading;

    public SessionsViewModel(SessionsDataService sessionsDataService)
        : this((range, limit, cancellationToken) => range switch
        {
            "Recent" => sessionsDataService.GetRecentSessionsAsync(limit, cancellationToken),
            "Last 24 Hours" => sessionsDataService.GetLast24HoursSessionsAsync(limit, cancellationToken),
            _ => sessionsDataService.GetTodaySessionsAsync(limit, cancellationToken)
        })
    {
    }

    public SessionsViewModel(Func<string, int, CancellationToken, Task<IReadOnlyList<AppSession>>> loadSessionsAsync)
    {
        ArgumentNullException.ThrowIfNull(loadSessionsAsync);

        _loadSessionsAsync = loadSessionsAsync;
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
    }

    public IReadOnlyList<string> RangeFilters { get; } = ["Today", "Last 24 Hours", "Recent"];

    public IReadOnlyList<string> CloseReasonFilters { get; } = ["All", "Open", "ProcessChanged", "Paused", "Stopped", "Other"];

    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = new();

    public IAsyncRelayCommand RefreshCommand { get; }

    public string SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (SetProperty(ref _selectedRange, value))
            {
                _ = LoadAsync();
            }
        }
    }

    public string SelectedCloseReason
    {
        get => _selectedCloseReason;
        set
        {
            if (SetProperty(ref _selectedCloseReason, value))
            {
                ApplyFilter();
            }
        }
    }

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
        private set
        {
            if (SetProperty(ref _hasLoadError, value)) OnPropertyChanged(nameof(State));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(State));
            }
        }
    }

    public PageState State => IsLoading ? PageState.Loading : HasLoadError ? PageState.Error : Sessions.Count > 0 ? PageState.Ready : PageState.Empty;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var loadVersion = Interlocked.Increment(ref _loadVersion);
        var selectedRange = SelectedRange;

        try
        {
            IsLoading = true;
            var sessions = await _loadSessionsAsync(selectedRange, SessionLimit, cancellationToken);
            if (loadVersion != Volatile.Read(ref _loadVersion))
            {
                return;
            }

            _allSessions.Clear();
            _allSessions.AddRange(sessions.Select(session => new SessionListItemViewModel(session)));
            HasLoadError = false;
            ApplyFilter(selectedRange);
        }
        catch (Exception ex)
        {
            if (loadVersion != Volatile.Read(ref _loadVersion))
            {
                return;
            }

            _allSessions.Clear();
            Sessions.Clear();
            HasLoadError = true;
            EmptyStateText = "Sessions could not be loaded. Refresh to retry.";

            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            StatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "Sessions load failed."
                : $"Sessions load failed: {safeMessage}";
        }
        finally
        {
            if (loadVersion == Volatile.Read(ref _loadVersion))
            {
                IsLoading = false;
            }
        }
    }

    private void ApplyFilter()
    {
        ApplyFilter(SelectedRange);
    }

    private void ApplyFilter(string statusRange)
    {
        var filteredSessions = string.Equals(SelectedCloseReason, "All", StringComparison.OrdinalIgnoreCase)
            ? _allSessions
            : _allSessions
                .Where(session => string.Equals(session.CloseReasonFilter, SelectedCloseReason, StringComparison.OrdinalIgnoreCase))
                .ToList();

        Sessions.Clear();
        foreach (var session in filteredSessions)
        {
            Sessions.Add(session);
        }

        StatusText = Sessions.Count == 0
            ? "No sessions found."
            : $"Showing {Sessions.Count} of {_allSessions.Count} {statusRange.ToLowerInvariant()} sessions.";
        EmptyStateText = "暂无会话记录。Agent 运行并写入 app_sessions 后会显示在这里。";
        OnPropertyChanged(nameof(State));
    }
}
