using System.Windows;
using System.Windows.Media;

namespace PhoneShell.Utilities;

public static class ProbeChartGeometryBuilder
{
    public static Geometry CreateRingArcGeometry(
        double percent,
        double width,
        double height,
        double padding)
    {
        var clamped = Math.Clamp(percent, 0d, 100d);
        if (clamped <= 0.01d)
            return Geometry.Empty;

        var radiusX = Math.Max((width / 2d) - padding, 0d);
        var radiusY = Math.Max((height / 2d) - padding, 0d);
        if (radiusX <= 0d || radiusY <= 0d)
            return Geometry.Empty;

        if (clamped >= 99.99d)
        {
            return new EllipseGeometry(
                new Point(width / 2d, height / 2d),
                radiusX,
                radiusY);
        }

        var startAngle = -90d;
        var endAngle = startAngle + (clamped / 100d * 359.99d);
        var start = PolarToCartesian(width / 2d, height / 2d, radiusX, radiusY, startAngle);
        var end = PolarToCartesian(width / 2d, height / 2d, radiusX, radiusY, endAngle);
        var isLargeArc = clamped >= 50d;

        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(start, false, false);
        context.ArcTo(
            end,
            new Size(radiusX, radiusY),
            0d,
            isLargeArc,
            SweepDirection.Clockwise,
            true,
            false);
        geometry.Freeze();
        return geometry;
    }

    public static Geometry CreateSparklineGeometry(
        IReadOnlyList<double> samples,
        double width,
        double height,
        double horizontalPadding,
        double verticalPadding)
    {
        if (samples.Count == 0 || width <= 0d || height <= 0d)
            return Geometry.Empty;

        var left = horizontalPadding;
        var top = verticalPadding;
        var drawableWidth = Math.Max(width - horizontalPadding * 2d, 1d);
        var drawableHeight = Math.Max(height - verticalPadding * 2d, 1d);
        var divisor = Math.Max(samples.Count - 1, 1);

        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        for (var index = 0; index < samples.Count; index++)
        {
            var x = left + (drawableWidth * index / divisor);
            var normalized = Math.Clamp(samples[index], 0d, 100d) / 100d;
            var y = top + drawableHeight - (normalized * drawableHeight);
            var point = new Point(x, y);

            if (index == 0)
                context.BeginFigure(point, false, false);
            else
                context.LineTo(point, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PolarToCartesian(
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        double angleDegrees)
    {
        var angleRadians = Math.PI * angleDegrees / 180d;
        return new Point(
            centerX + radiusX * Math.Cos(angleRadians),
            centerY + radiusY * Math.Sin(angleRadians));
    }
}
