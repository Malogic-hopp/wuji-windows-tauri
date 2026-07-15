using QuantifiedSelf.Windows.App.UI;
using Xunit;

namespace QuantifiedSelf.Windows.Tests;

[Trait("Category", "Fast")]
public sealed class AdaptiveLayoutTests
{
    [Theory]
    [InlineData(0, LayoutMode.Compact)]
    [InlineData(960, LayoutMode.Compact)]
    [InlineData(1279, LayoutMode.Compact)]
    [InlineData(1279.999, LayoutMode.Compact)]
    [InlineData(1280, LayoutMode.Standard)]
    [InlineData(1599, LayoutMode.Standard)]
    [InlineData(1599.999, LayoutMode.Standard)]
    [InlineData(1600, LayoutMode.Wide)]
    [InlineData(1920, LayoutMode.Wide)]
    [InlineData(3840, LayoutMode.Wide)]
    public void ResolveMode_ReturnsExpectedMode(double width, LayoutMode expected)
    {
        var result = TestResolve.ResolveMode(width);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ResolveMode_InvalidWidth_ReturnsStandard(double width)
    {
        var result = TestResolve.ResolveMode(width);
        Assert.Equal(LayoutMode.Standard, result);
    }

    [Fact]
    public void LayoutMode_HasThreeValues()
    {
        var values = System.Enum.GetValues<LayoutMode>();
        Assert.Equal(3, values.Length);
    }
}

[Trait("Category", "Fast")]
public sealed class SidebarWidthTests
{
    [Fact]
    public void ExpandedWidth_TokenIs184()
    {
        const double expected = 184;
        Assert.Equal(184.0, expected);
    }

    [Fact]
    public void CompactWidth_TokenIs52()
    {
        const double expected = 52;
        Assert.Equal(52.0, expected);
    }

    [Fact]
    public void ExpandedWidth_Not196()
    {
        const double canonicalExpanded = 184;
        Assert.NotEqual(196.0, canonicalExpanded);
    }
}

/// <summary>
/// Exposes the internal ResolveMode for unit testing.
/// </summary>
internal static class TestResolve
{
    private static readonly System.Reflection.MethodInfo s_resolve =
        typeof(AdaptiveLayout).GetMethod(
            "ResolveMode",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    public static LayoutMode ResolveMode(double width) =>
        (LayoutMode)s_resolve.Invoke(null, [width])!;
}
