using System;
using Avalonia;

namespace Scatter;

/// <summary>
/// Кладка колонками, как на Pinterest: каждая следующая картинка
/// ставится в самую короткую колонку. Работает по одной картинке,
/// поэтому подходит для постепенной загрузки.
/// </summary>
public sealed class MasonryLayout
{
    private readonly double[] _columnHeights;
    private readonly double _columnWidth;
    private readonly double _gap;

    public MasonryLayout(int columns, double columnWidth, double gap)
    {
        _columnHeights = new double[Math.Max(1, columns)];
        _columnWidth = columnWidth;
        _gap = gap;
    }

    /// <summary>
    /// Подбирает число колонок так, чтобы вся раскладка получилась
    /// примерно в пропорциях экрана 16:9.
    /// </summary>
    public static int SuggestColumns(int imageCount) =>
        Math.Clamp((int)Math.Round(1.2 * Math.Sqrt(imageCount)), 1, 24);

    /// <summary>Возвращает место для картинки с отношением сторон aspect = ширина / высота.</summary>
    public Rect Add(double aspect)
    {
        if (aspect <= 0) aspect = 1;

        int column = 0;
        for (int i = 1; i < _columnHeights.Length; i++)
        {
            if (_columnHeights[i] < _columnHeights[column] - 1e-6)
                column = i;
        }

        double height = _columnWidth / aspect;
        var rect = new Rect(column * (_columnWidth + _gap), _columnHeights[column], _columnWidth, height);
        _columnHeights[column] += height + _gap;
        return rect;
    }
}

