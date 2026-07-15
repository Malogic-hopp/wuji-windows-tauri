using System.Windows;
using System.Windows.Data;

namespace QuantifiedSelf.Windows.App.UI;

/// <summary>
/// View-layer attached behavior that monitors a Window's ActualWidth
/// and publishes a read-only LayoutMode attached property.
/// LayoutMode inherits so child elements can read it without walking the tree.
/// </summary>
public static class AdaptiveLayout
{
    // ── Breakpoints (DIP) ──
    internal const double CompactMaxWidth = 1280;
    internal const double WideMinWidth = 1600;

    // ── IsEnabled ──

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(AdaptiveLayout),
            new FrameworkPropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) =>
        (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) =>
        obj.SetValue(IsEnabledProperty, value);

    // ── Mode (read-only, inherits) ──

    private static readonly DependencyPropertyKey ModePropertyKey =
        DependencyProperty.RegisterAttachedReadOnly(
            "Mode",
            typeof(LayoutMode),
            typeof(AdaptiveLayout),
            new FrameworkPropertyMetadata(
                LayoutMode.Standard,
                FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty ModeProperty = ModePropertyKey.DependencyProperty;

    public static LayoutMode GetMode(DependencyObject obj) =>
        (LayoutMode)obj.GetValue(ModeProperty);

    internal static void SetMode(DependencyObject obj, LayoutMode value) =>
        obj.SetValue(ModePropertyKey, value);

    // ── Internal helpers ──

    private static readonly DependencyProperty SubscribedProperty =
        DependencyProperty.RegisterAttached(
            "_AdaptiveLayoutSubscribed",
            typeof(bool),
            typeof(AdaptiveLayout),
            new PropertyMetadata(false));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window) return;

        var wasSubscribed = (bool)d.GetValue(SubscribedProperty);
        var enabled = (bool)e.NewValue;

        if (enabled && !wasSubscribed)
        {
            d.SetValue(SubscribedProperty, true);
            window.Loaded += OnWindowLoaded;
            window.Closed += OnWindowClosed;
        }
        else if (!enabled && wasSubscribed)
        {
            d.SetValue(SubscribedProperty, false);
            window.Loaded -= OnWindowLoaded;
            window.SizeChanged -= OnWindowSizeChanged;
            window.Closed -= OnWindowClosed;
        }
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;
        UpdateMode(window);
        window.SizeChanged += OnWindowSizeChanged;
    }

    private static void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Window window) return;
        UpdateMode(window);
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.Loaded -= OnWindowLoaded;
        window.SizeChanged -= OnWindowSizeChanged;
        window.Closed -= OnWindowClosed;
        window.SetValue(SubscribedProperty, false);
    }

    internal static void UpdateMode(Window window)
    {
        var mode = ResolveMode(window.ActualWidth);
        SetMode(window, mode);
    }

    /// <summary>
    /// Pure function: maps a width (DIP) to a LayoutMode.
    /// Handles edge cases: NaN, negative, positive infinity.
    /// </summary>
    internal static LayoutMode ResolveMode(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width < 0)
            return LayoutMode.Standard;

        if (width >= WideMinWidth)
            return LayoutMode.Wide;

        if (width >= CompactMaxWidth)
            return LayoutMode.Standard;

        return LayoutMode.Compact;
    }
}
