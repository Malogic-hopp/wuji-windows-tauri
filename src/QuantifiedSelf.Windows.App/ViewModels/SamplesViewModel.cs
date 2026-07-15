using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantifiedSelf.Windows.ApplicationLayer.Activity;
using QuantifiedSelf.Windows.Client;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class SamplesViewModel : ObservableObject
{
    private const int RecentSampleLimit = 200;

    private readonly Func<int, CancellationToken, Task<IReadOnlyList<ForegroundSample>>> _loadSamplesAsync;
    private readonly List<ForegroundSample> _allSamples = new();
    private string _selectedActivityState = "All";
    private string _statusText = "No samples loaded.";
    private string _emptyStateText = "No samples loaded.";
    private bool _hasLoadError;
    private bool _isLoading;

    public SamplesViewModel(ISamplesDataService samplesDataService)
        : this(samplesDataService.GetRecentSamplesAsync)
    {
    }

    public SamplesViewModel(IActivityClient activityClient)
        : this(activityClient.Samples)
    {
    }

    public SamplesViewModel(Func<int, CancellationToken, Task<IReadOnlyList<ForegroundSample>>> loadSamplesAsync)
    {
        ArgumentNullException.ThrowIfNull(loadSamplesAsync);

        _loadSamplesAsync = loadSamplesAsync;
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
    }

    public IReadOnlyList<string> ActivityStateFilters { get; } = ["All", "Active", "Idle", "Unknown"];

    public ObservableCollection<SampleListItemViewModel> Samples { get; } = new();

    public IAsyncRelayCommand RefreshCommand { get; }

    public string SelectedActivityState
    {
        get => _selectedActivityState;
        set
        {
            if (SetProperty(ref _selectedActivityState, value))
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

    public PageState State => IsLoading ? PageState.Loading : HasLoadError ? PageState.Error : Samples.Count > 0 ? PageState.Ready : PageState.Empty;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsLoading = true;
            var samples = await _loadSamplesAsync(RecentSampleLimit, cancellationToken);
            _allSamples.Clear();
            _allSamples.AddRange(samples);
            HasLoadError = false;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _allSamples.Clear();
            Samples.Clear();
            HasLoadError = true;
            EmptyStateText = "Samples could not be loaded. Refresh to retry.";

            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            StatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "Samples load failed."
                : $"Samples load failed: {safeMessage}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var filteredSamples = string.Equals(SelectedActivityState, "All", StringComparison.OrdinalIgnoreCase)
            ? _allSamples
            : _allSamples
                .Where(sample => string.Equals(sample.ActivityState, SelectedActivityState, StringComparison.OrdinalIgnoreCase))
                .ToList();

        Samples.Clear();
        foreach (var sample in filteredSamples)
        {
            Samples.Add(new SampleListItemViewModel(sample));
        }

        StatusText = Samples.Count == 0
            ? "No samples found."
            : $"Showing {Samples.Count} of {_allSamples.Count} recent samples.";
        EmptyStateText = "暂无采样记录。Agent 运行并写入 foreground_samples 后会显示在这里。";
        OnPropertyChanged(nameof(State));
    }
}
