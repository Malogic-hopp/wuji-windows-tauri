using System.Windows;
using QuantifiedSelf.Windows.App.ViewModels;

namespace QuantifiedSelf.Windows.App;

/// <summary>
/// Production shell retained while the redesigned experience is validated behind --ui-preview.
/// </summary>
public partial class LegacyMainWindow : Window
{
    public LegacyMainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }
}
