using System.Windows;
using QuantifiedSelf.Windows.App.ViewModels;

namespace QuantifiedSelf.Windows.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += MainWindow_KeyDown;
    }

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    /// <summary>
    /// Left-click / Enter / Space on the Agent pill toggles the Popup.
    /// Right-click is handled by the ContextMenu (no code needed).
    /// </summary>
    private void AgentStatusButton_Click(object sender, RoutedEventArgs e)
    {
        AgentStatusPopup.IsOpen = !AgentStatusPopup.IsOpen;
    }

    private void MainWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && AgentStatusPopup.IsOpen)
        {
            AgentStatusPopup.IsOpen = false;
            e.Handled = true;
        }
    }
}
