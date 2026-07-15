namespace QuantifiedSelf.Windows.App.UI;

/// <summary>
/// View-layer layout mode. Computed from Window width, not stored in any ViewModel.
/// </summary>
public enum LayoutMode
{
    /// <summary>Width &lt; 1280 DIP</summary>
    Compact,
    /// <summary>1280 &le; width &lt; 1600 DIP</summary>
    Standard,
    /// <summary>Width &ge; 1600 DIP</summary>
    Wide
}
