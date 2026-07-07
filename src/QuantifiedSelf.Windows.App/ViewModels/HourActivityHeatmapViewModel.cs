using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.App.ViewModels;

/// <summary>
/// ViewModel for the 24h×7d activity heatmap.
/// Cells are ordered hour-major (row=hour, col=day) so UniformGrid Columns=7
/// produces 24 rows × 7 columns matching the old project's z[hour][date] layout.
/// </summary>
public sealed class HourActivityHeatmapViewModel : ObservableObject
{
    private bool _hasData;

    public HourActivityHeatmapViewModel()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        for (var di = 0; di < 7; di++)
            DateLabels.Add((today.AddDays(di - 6)).ToString("MM/dd"));

        for (var h = 0; h < 24; h++)
        {
            HourLabels.Add(PhaseLabelForHour(h));
            for (var d = 0; d < 7; d++)
                Cells.Add(new HeatmapCellViewModel());
        }

        HasData = false;
    }

    public HourActivityHeatmapViewModel(IReadOnlyList<DailyHourActivityPoint> points, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(points);

        var hasAnyData = false;

        // Column headers: date labels
        for (var di = 0; di < 7; di++)
        {
            var date = today.AddDays(di - 6);
            DateLabels.Add(date == today ? "今天" : $"{date.Month}/{date.Day}");
        }

        // Hour row labels
        for (var h = 0; h < 24; h++)
            HourLabels.Add(PhaseLabelForHour(h));

        // Fill cells hour-major: outer=hour (row), inner=day (column)
        for (var h = 0; h < 24; h++)
        {
            for (var di = 0; di < 7; di++)
            {
                var date = today.AddDays(di - 6);
                var dateStr = date.ToString("yyyy-MM-dd");
                var point = points.FirstOrDefault(p => p.Date == dateStr && p.Hour == h);
                var cell = BuildCell(point, h, dateStr);
                Cells.Add(cell);
                if (point is not null && point.TotalSamples > 0) hasAnyData = true;
            }
        }

        HasData = hasAnyData;
    }

    public ObservableCollection<HeatmapCellViewModel> Cells { get; } = new();

    public ObservableCollection<string> DateLabels { get; } = new();

    public ObservableCollection<string?> HourLabels { get; } = new();

    public bool HasData
    {
        get => _hasData;
        private set => SetProperty(ref _hasData, value);
    }

    private static HeatmapCellViewModel BuildCell(DailyHourActivityPoint? point, int hour, string dateStr)
    {
        // Use ActiveIntensity for color so the heatmap reflects activity volume
        // (normalized against the busiest hour), not the within-hour active/idle split.
        var intensity = point?.ActiveIntensity ?? 0.0;
        var ratio = point?.ActiveRatio ?? 0.0;
        var total = point?.TotalSamples ?? 0;
        var active = point?.ActiveSamples ?? 0;

        string tooltip;
        if (point is not null && total > 0)
        {
            var pct = (int)Math.Round(ratio * 100);
            tooltip = $"{point.Date} {hour:D2}:00 · 活跃 {active} · 活跃率 {pct}% · 共 {total} 个样本";
        }
        else
        {
            tooltip = $"{dateStr} {hour:D2}:00 · 无数据";
        }

        return new HeatmapCellViewModel
        {
            ActiveIntensity = intensity,
            Background = HeatmapCellViewModel.InterpolateColor(intensity),
            TooltipText = tooltip,
            IsPhaseBoundary = hour == 0 || hour == 6 || hour == 12 || hour == 18
        };
    }

    private static string? PhaseLabelForHour(int hour)
    {
        // Place label at the middle of each 6-hour band
        return hour switch
        {
            3 => "睡觉 00-06",
            9 => "上午 06-12",
            15 => "下午 12-18",
            21 => "晚上 18-24",
            _ => null
        };
    }
}
