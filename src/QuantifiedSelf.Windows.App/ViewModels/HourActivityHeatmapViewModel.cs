using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using QuantifiedSelf.Windows.Core.Models;
using SkiaSharp;

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

        HeatmapSeries = CreateHeatmapSeries(Cells);
        HeatmapXAxes = CreateHeatmapXAxes(DateLabels);
        HeatmapYAxes = CreateHeatmapYAxes();
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
        HeatmapSeries = CreateHeatmapSeries(Cells);
        HeatmapXAxes = CreateHeatmapXAxes(DateLabels);
        HeatmapYAxes = CreateHeatmapYAxes();
    }

    public ObservableCollection<HeatmapCellViewModel> Cells { get; } = new();

    public ObservableCollection<string> DateLabels { get; } = new();

    public ObservableCollection<string?> HourLabels { get; } = new();

    public ISeries[] HeatmapSeries { get; }

    public Axis[] HeatmapXAxes { get; }

    public Axis[] HeatmapYAxes { get; }

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
            Date = DateOnly.Parse(dateStr),
            Hour = hour,
            IntensityLevel = GetIntensityLevel(intensity),
            AutomationName = point is not null && total > 0
                ? $"{DateOnly.Parse(dateStr):M月d日} {hour:D2}:00，有效使用记录 {active} 个样本"
                : $"{DateOnly.Parse(dateStr):M月d日} {hour:D2}:00，无数据",
            ActiveIntensity = intensity,
            Background = HeatmapCellViewModel.InterpolateColor(intensity),
            TooltipText = tooltip,
            IsPhaseBoundary = hour == 0 || hour == 6 || hour == 12 || hour == 18
        };
    }

    private static int GetIntensityLevel(double intensity) => intensity switch
    {
        <= 0 => 0,
        <= 0.25 => 1,
        <= 0.5 => 2,
        <= 0.75 => 3,
        _ => 4
    };

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

    private static ISeries[] CreateHeatmapSeries(IReadOnlyList<HeatmapCellViewModel> cells)
    {
        var values = new List<WeightedPoint>(cells.Count);
        for (var hour = 0; hour < 24; hour++)
        {
            for (var day = 0; day < 7; day++)
            {
                var cell = cells[(hour * 7) + day];
                values.Add(new WeightedPoint(day, 23 - hour, cell.ActiveIntensity));
            }
        }

        return
        [
            new HeatSeries<WeightedPoint>
            {
                Values = values,
                Name = "Activity",
                AnimationsSpeed = TimeSpan.Zero,
                HeatMap =
                [
                    new LvcColor(241, 245, 249),
                    new LvcColor(153, 246, 228),
                    new LvcColor(20, 184, 166),
                    new LvcColor(15, 118, 110)
                ],
                ColorStops = [0.0, 0.25, 0.65, 1.0],
                MinValue = 0,
                MaxValue = 1,
                PointPadding = new LiveChartsCore.Drawing.Padding(2),
                XToolTipLabelFormatter = point => FormatHeatmapTooltip(cells, point.Model),
                YToolTipLabelFormatter = _ => string.Empty
            }
        ];
    }

    private static Axis[] CreateHeatmapXAxes(IReadOnlyList<string> labels)
    {
        return
        [
            new Axis
            {
                Labels = labels.ToArray(),
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(new SKColor(82, 96, 109)),
                SeparatorsPaint = null,
                TicksPaint = null,
                MinLimit = -0.5,
                MaxLimit = 6.5,
                MinStep = 1,
                ForceStepToMin = true,
                AnimationsSpeed = TimeSpan.Zero
            }
        ];
    }

    private static Axis[] CreateHeatmapYAxes()
    {
        return
        [
            new Axis
            {
                MinLimit = -0.5,
                MaxLimit = 23.5,
                TextSize = 10,
                Labels = CreateHeatmapHourLabels(),
                LabelsPaint = new SolidColorPaint(new SKColor(82, 96, 109)),
                SeparatorsPaint = null,
                TicksPaint = null,
                MinStep = 1,
                ForceStepToMin = true,
                AnimationsSpeed = TimeSpan.Zero
            }
        ];
    }

    private static string[] CreateHeatmapHourLabels()
    {
        var labels = new string[24];
        labels[23 - 21] = "晚上";
        labels[23 - 15] = "下午";
        labels[23 - 9] = "上午";
        labels[23 - 3] = "睡觉";
        return labels;
    }

    private static string FormatHeatmapTooltip(IReadOnlyList<HeatmapCellViewModel> cells, object? model)
    {
        if (model is not WeightedPoint point)
        {
            return string.Empty;
        }

        var day = (int)Math.Round(point.X ?? -1);
        var hour = 23 - (int)Math.Round(point.Y ?? -1);
        if (day < 0 || day >= 7 || hour < 0 || hour >= 24)
        {
            return string.Empty;
        }

        var cell = cells[(hour * 7) + day];
        return cell.TooltipText;
    }
}
