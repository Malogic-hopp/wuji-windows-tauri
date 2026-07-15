namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class TodayPageViewModel
{
    public TodayPageViewModel(DashboardViewModel dashboardViewModel)
    {
        DashboardViewModel = dashboardViewModel;
    }

    public DashboardViewModel DashboardViewModel { get; }
}
