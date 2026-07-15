using CommunityToolkit.Mvvm.ComponentModel;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed partial class NavigationItemViewModel : ObservableObject
{
    private bool _isSelected;

    public NavigationItemViewModel(string key, string title, string subtitle)
    {
        Key = key;
        Title = title;
        Subtitle = subtitle;
    }

    public string Key { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
