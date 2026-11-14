using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Scatter;

/// <summary>
/// Фон-сетка. Рисует только видимые линии, всегда толщиной 1 px,
/// сдвигается и масштабируется вместе с камерой.
/// </summary>
public class GridBackground : Control
{
    public const double CellSize = 32.0;
    private const double MinVisibleCell = 6.0; // мельче этого сетку не рисуем

    private static readonly Pen LinePen = new(new SolidColorBrush(Color.Parse("#2C2C32")), 1);

    private double _zoom = 1.0;
    private double _offsetX;
    private double _offsetY;

    public void SetCamera(double zoom, double offsetX, double offsetY)
    {
        _zoom = zoom;
        _offsetX = offsetX;
        _offsetY = offsetY;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double cell = CellSize * _zoom;
        if (cell < MinVisibleCell) return;

        double w = Bounds.Width;
        double h = Bounds.Height;

        double startX = _offsetX % cell;
        if (startX < 0) startX += cell;
        double startY = _offsetY % cell;
        if (startY < 0) startY += cell;

        // +0.5, чтобы линии в 1 px были чёткими, а не размытыми
        for (double x = startX; x <= w; x += cell)
        {
            double px = Math.Round(x) + 0.5;
            context.DrawLine(LinePen, new Point(px, 0), new Point(px, h));
        }

        for (double y = startY; y <= h; y += cell)
        {
            double py = Math.Round(y) + 0.5;
            context.DrawLine(LinePen, new Point(0, py), new Point(w, py));
        }
    }
}