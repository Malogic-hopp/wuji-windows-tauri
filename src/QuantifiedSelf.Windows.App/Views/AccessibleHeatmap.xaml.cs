using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuantifiedSelf.Windows.App.Views;

public partial class AccessibleHeatmap : UserControl
{
    public AccessibleHeatmap()
    {
        InitializeComponent();
    }

    private void HeatmapCell_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not Button current)
        {
            return;
        }

        var delta = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -7,
            Key.Down => 7,
            _ => 0
        };
        if (delta == 0)
        {
            return;
        }

        var index = CellsItemsControl.Items.IndexOf(current.DataContext);
        var targetIndex = index + delta;
        if (index < 0 || targetIndex < 0 || targetIndex >= CellsItemsControl.Items.Count)
        {
            return;
        }

        CellsItemsControl.UpdateLayout();
        if (CellsItemsControl.ItemContainerGenerator.ContainerFromIndex(targetIndex) is DependencyObject container
            && FindVisualChild<Button>(container) is { } target)
        {
            target.Focus();
            e.Handled = true;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
            {
                return result;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
