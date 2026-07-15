using CommunityToolkit.Mvvm.ComponentModel;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class TrendsPageViewModel : ObservableObject
{
    public TrendsPageViewModel(DashboardViewModel dashboardViewModel)
    {
        DashboardViewModel = dashboardViewModel;
        DashboardViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DashboardViewModel.State)) OnPropertyChanged(nameof(State));
        };
    }

    public DashboardViewModel DashboardViewModel { get; }

    public PageState State => DashboardViewModel.State;
}
