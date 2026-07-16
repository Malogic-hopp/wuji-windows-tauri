using QuantifiedSelf.Windows.ApplicationLayer.Analytics;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Application.Tests;

[Trait("Category", "Fast")]
public sealed class ApplicationUseCaseTests
{
    [Fact]
    public void FocusMetricsCalculator_ComputesFocusWithoutDesktopRuntime()
    {
        var start = new DateTime(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc);
        var samples = Enumerable.Range(0, 13)
            .Select(minute => new ForegroundSample
            {
                SampleTimeUtc = start.AddMinutes(minute),
                ProcessName = "Code",
                WindowTitle = "Program.cs",
                ActivityState = "Active"
            })
            .ToArray();

        var result = FocusMetricsCalculator.Compute(samples);

        Assert.Equal(1, result.FocusSessionCount);
        Assert.NotNull(result.LongestFocusSession);
        Assert.Equal(TimeSpan.FromMinutes(12), result.LongestFocusSession!.Duration);
    }

    [Fact]
    public void HourActivityHeatmapCalculator_EmptyInputReturnsCompleteMatrix()
    {
        var result = HourActivityHeatmapCalculator.Compute([], new DateOnly(2026, 7, 16));

        Assert.Equal(168, result.Count);
        Assert.All(result, point => Assert.Equal(0, point.ActiveSamples));
        Assert.Equal(0, result[0].Hour);
        Assert.Equal(23, result[^1].Hour);
    }

    [Fact]
    public void InsightSuggestionEngine_InsufficientDataReturnsNoSuggestion()
    {
        var result = InsightSuggestionEngine.Generate(
            new DailyActivitySummary
            {
                TotalActiveDurationSeconds = 599,
                SessionCount = 0
            },
            trend: null);

        Assert.Empty(result);
    }
}
