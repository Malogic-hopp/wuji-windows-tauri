using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class TimelinePageViewModel : ObservableObject
{
    private DateOnly _selectedDate = DateOnly.FromDateTime(DateTime.Today);
    private int? _targetHour;
    private bool _isLoading;

    public TimelinePageViewModel(AppsViewModel appsViewModel, SessionsViewModel sessionsViewModel, SamplesViewModel samplesViewModel)
    {
        AppsViewModel = appsViewModel;
        SessionsViewModel = sessionsViewModel;
        SamplesViewModel = samplesViewModel;
        PreviousDayCommand = new RelayCommand(() => SetDate(SelectedDate.AddDays(-1)));
        NextDayCommand = new RelayCommand(() => SetDate(SelectedDate.AddDays(1)), () => SelectedDate < DateOnly.FromDateTime(DateTime.Today));
        TodayCommand = new RelayCommand(() => SetDate(DateOnly.FromDateTime(DateTime.Today)));
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        SessionsViewModel.Sessions.CollectionChanged += OnSessionsChanged;
    }

    public AppsViewModel AppsViewModel { get; }

    public SessionsViewModel SessionsViewModel { get; }

    public SamplesViewModel SamplesViewModel { get; }

    public ObservableCollection<SessionListItemViewModel> SessionTimelineItems { get; } = new();

    public IRelayCommand PreviousDayCommand { get; }

    public IRelayCommand NextDayCommand { get; }

    public IRelayCommand TodayCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public DateOnly SelectedDate
    {
        get => _selectedDate;
        private set
        {
            if (SetProperty(ref _selectedDate, value))
            {
                OnPropertyChanged(nameof(SelectedDateText));
                NextDayCommand.NotifyCanExecuteChanged();
                RebuildTimeline();
            }
        }
    }

    public string SelectedDateText => SelectedDate == DateOnly.FromDateTime(DateTime.Today)
        ? $"今天 · {SelectedDate:M月d日}"
        : SelectedDate.ToString("yyyy年M月d日");

    public int? TargetHour
    {
        get => _targetHour;
        private set
        {
            if (SetProperty(ref _targetHour, value))
            {
                OnPropertyChanged(nameof(TargetHourText));
            }
        }
    }

    public string TargetHourText => TargetHour is int hour
        ? $"已定位到 {hour:D2}:00–{(hour + 1):D2}:00"
        : "按时间顺序查看应用会话";

    public string TotalDurationText => $"{SessionTimelineItems.Count} 个会话";

    public string AppCountText => AppsViewModel.Apps.Count.ToString();

    public string SessionCountText => SessionTimelineItems.Count.ToString();

    public string SampleCountText => SamplesViewModel.Samples.Count(sample => DateOnly.FromDateTime(sample.LocalTime) == SelectedDate).ToString();

    public bool HasAnyActivity => SessionTimelineItems.Count > 0;

    public bool HasLoadError => AppsViewModel.HasLoadError || SessionsViewModel.HasLoadError || SamplesViewModel.HasLoadError;

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

    public PageState State => IsLoading
        ? PageState.Loading
        : HasLoadError ? PageState.Error
        : HasAnyActivity ? PageState.Ready
        : PageState.Empty;

    public void NavigateTo(DateOnly date, int hour)
    {
        TargetHour = Math.Clamp(hour, 0, 23);
        SetDate(date);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            SessionsViewModel.SelectedRange = SelectedDate == DateOnly.FromDateTime(DateTime.Today) ? "Today" : "Recent";
            await AppsViewModel.LoadAsync(cancellationToken);
            await SessionsViewModel.LoadAsync(cancellationToken);
            await SamplesViewModel.LoadAsync(cancellationToken);
            RebuildTimeline();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SetDate(DateOnly value)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        SelectedDate = value > today ? today : value;
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildTimeline();

    private void RebuildTimeline()
    {
        SessionTimelineItems.Clear();
        foreach (var session in SessionsViewModel.Sessions
                     .Where(session => DateOnly.FromDateTime(session.StartedLocalTime) == SelectedDate)
                     .OrderBy(session => session.StartedLocalTime))
        {
            SessionTimelineItems.Add(session);
        }

        OnPropertyChanged(nameof(TotalDurationText));
        OnPropertyChanged(nameof(AppCountText));
        OnPropertyChanged(nameof(SessionCountText));
        OnPropertyChanged(nameof(SampleCountText));
        OnPropertyChanged(nameof(HasAnyActivity));
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(State));
    }
}
