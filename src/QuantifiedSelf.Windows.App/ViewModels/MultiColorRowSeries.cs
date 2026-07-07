using LiveChartsCore.Kernel.Events;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using SkiaSharp;

namespace QuantifiedSelf.Windows.App.ViewModels;

/// <summary>
/// A horizontal bar series that assigns a distinct colour to each bar based on
/// its Y‑axis category index.  Uses <see cref="ColoredRectangleGeometry"/> so
/// per‑point colours are set directly on the geometry after measurement.
/// </summary>
public sealed class MultiColorRowSeries : RowSeries<double, ColoredRectangleGeometry, LabelGeometry>
{
    private readonly SKColor[] _palette;

    public MultiColorRowSeries(SKColor[] palette)
    {
        _palette = palette;

        // After each point is measured, stamp its geometry with the right colour.
        this.OnPointMeasured(chartPoint =>
        {
            if (_palette.Length == 0 || chartPoint.Visual is null)
                return;

            var index = (int)Math.Round(chartPoint.Coordinate.SecondaryValue);
            if (index < 0 || index >= _palette.Length)
                return;

            var c = _palette[index];
            chartPoint.Visual.Color = new LvcColor(c.Red, c.Green, c.Blue, c.Alpha);
        });
    }
}
