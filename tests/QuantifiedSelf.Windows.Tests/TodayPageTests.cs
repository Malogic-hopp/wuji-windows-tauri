using QuantifiedSelf.Windows.App.ViewModels;
using QuantifiedSelf.Windows.Core.Models;
using Xunit;

namespace QuantifiedSelf.Windows.Tests;

[Trait("Category", "Fast")]
public sealed class TodayPageTests
{
    [Fact]
    public async Task HasAnyActivity_True_When_SessionsExist()
    {
        var summary = new DailyActivitySummary
        {
            SessionCount = 3,
            SampleCount = 0,
            TotalActiveDurationSeconds = 0,
            TopApps = []
        };
        var vm = new DashboardViewModel((_, _, _) => Task.FromResult(summary));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasAnyActivity);
        Assert.False(vm.HasLoadError);
    }

    [Fact]
    public async Task HasAnyActivity_True_When_SamplesExist()
    {
        var summary = new DailyActivitySummary
        {
            SessionCount = 0,
            SampleCount = 50,
            TotalActiveDurationSeconds = 0,
            TopApps = []
        };
        var vm = new DashboardViewModel((_, _, _) => Task.FromResult(summary));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasAnyActivity);
    }

    [Fact]
    public async Task HasAnyActivity_True_When_ActiveDurationExists()
    {
        var summary = new DailyActivitySummary
        {
            SessionCount = 0,
            SampleCount = 0,
            TotalActiveDurationSeconds = 3600,
            TopApps = []
        };
        var vm = new DashboardViewModel((_, _, _) => Task.FromResult(summary));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasAnyActivity);
    }

    [Fact]
    public async Task HasAnyActivity_False_When_NoData()
    {
        var summary = new DailyActivitySummary
        {
            SessionCount = 0,
            SampleCount = 0,
            TotalActiveDurationSeconds = 0,
            TotalDurationSeconds = 0,
            TopApps = []
        };
        var vm = new DashboardViewModel((_, _, _) => Task.FromResult(summary));
        await vm.RefreshCommand.ExecuteAsync(null);

        // After loading empty summary, TotalActiveText uses FormatDurationLong(0) = "0m"
        Assert.False(vm.HasAnyActivity);
    }

    [Fact]
    public async Task HasLoadError_True_When_QueryFails()
    {
        var vm = new DashboardViewModel((_, _, _) => throw new InvalidOperationException("DB error"));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasLoadError);
        Assert.False(vm.HasAnyActivity);
    }

    [Fact]
    public async Task AppCountText_From_TopApps_Count()
    {
        var summary = new DailyActivitySummary
        {
            SessionCount = 1,
            SampleCount = 0,
            TotalActiveDurationSeconds = 60,
            TopApps = [
                new AppUsageSummary { ProcessName = "Code", ActiveDurationSeconds = 60 },
                new AppUsageSummary { ProcessName = "Edge", ActiveDurationSeconds = 30 }
            ]
        };
        var vm = new DashboardViewModel((_, _, _) => Task.FromResult(summary));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("2", vm.AppCountText);
    }

    [Fact]
    public async Task AppCountText_Dash_When_TopApps_Null()
    {
        var summary = new DailyActivitySummary
        {
            SessionCount = 1,
            SampleCount = 0,
            TotalActiveDurationSeconds = 60,
            TopApps = null!
        };
        var vm = new DashboardViewModel((_, _, _) => Task.FromResult(summary));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("-", vm.AppCountText);
    }

    [Fact]
    public async Task FormatDuration_Hours_ReturnsChineseFormat()
    {
        var summary = new DailyActivitySummary
        {
            TotalActiveDurationSeconds = 3661,
            SessionCount = 1,
            TopApps = []
        };
        var vm = new DashboardViewModel((_, _, _) => Task.FromResult(summary));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("1小时 1分", vm.TotalActiveText);
    }

    [Fact]
    public async Task FormatDuration_Seconds_ReturnsChineseFormat()
    {
        var summary = new DailyActivitySummary
        {
            TotalActiveDurationSeconds = 45,
            SessionCount = 1,
            TopApps = []
        };
        var vm = new DashboardViewModel((_, _, _) => Task.FromResult(summary));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("45秒", vm.TotalActiveText);
    }

    [Fact]
    public async Task Metrics_ShowZero_When_DataLoaded_ButEmpty()
    {
        // When data loads successfully with 0 values, "0" is truthful.
        // "-" only appears on load failure/exception.
        var summary = new DailyActivitySummary
        {
            SessionCount = 0,
            SampleCount = 0,
            TotalActiveDurationSeconds = 0,
            TotalDurationSeconds = 0,
            TopApps = []
        };
        var vm = new DashboardViewModel((_, _, _) => Task.FromResult(summary));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("0m", vm.TotalActiveText);
        Assert.Equal("0m", vm.TotalDurationText);
        Assert.Equal("0", vm.SessionCountText);
        Assert.Equal("0", vm.AppCountText);
    }

    [Fact]
    public async Task Metrics_RealValues_When_DataExists()
    {
        var summary = new DailyActivitySummary
        {
            SessionCount = 5,
            SampleCount = 100,
            TotalActiveDurationSeconds = 7200,
            TotalDurationSeconds = 9000,
            TopApps = [new AppUsageSummary { ProcessName = "Code", ActiveDurationSeconds = 3600 }]
        };
        var vm = new DashboardViewModel((_, _, _) => Task.FromResult(summary));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("2小时 0分", vm.TotalActiveText);
        Assert.Equal("2小时 30分", vm.TotalDurationText);
        Assert.Equal("5", vm.SessionCountText);
        Assert.Equal("1", vm.AppCountText);
    }
}
