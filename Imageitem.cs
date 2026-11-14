using System.IO;
using Avalonia.Media.Imaging;

namespace Scatter;

/// <summary>
/// Одна картинка на холсте: файл, копии разного размера (уровни детализации)
/// и положение в мировых координатах. Элемента интерфейса у неё нет,
/// её рисует MoodboardView.
/// </summary>
public sealed class ImageItem
{
    /// <summary>Уровни детализации, индекс = номер уровня из ImageLoader.LevelWidths. Незагруженные = null.</summary>
    public Bitmap?[] Levels { get; } = new Bitmap?[ImageLoader.LevelCount];

    public required string FilePath { get; init; }

    /// <summary>Уровень 0 (32 px). Может быть null, если не удалось сделать.</summary>
    public Bitmap? Mini
    {
        get => Levels[0];
        init => Levels[0] = value;
    }

    /// <summary>Уровень 1 (64 px). Может быть null, если не удалось сделать.</summary>
    public Bitmap? Small
    {
        get => Levels[1];
        init => Levels[1] = value;
    }

    /// <summary>Уровень 2 (128 px). Есть всегда.</summary>
    public required Bitmap Thumb
    {
        get => Levels[ImageLoader.ThumbLevel]!;
        init => Levels[ImageLoader.ThumbLevel] = value;
    }

    // Служебные поля подгрузки крупных уровней (трогаются только из UI-потока)
    public int DesiredLevel { get; set; } = -1;   // какой уровень нужен сейчас; -1 = картинка не видна
    public int LoadingLevel { get; set; } = -1;   // какой уровень сейчас грузится; -1 = ничего
    public int LastNeededStamp { get; set; }      // когда картинка была нужна в последний раз
    public bool LoadFailed { get; set; }
    public bool IsDisposed { get; private set; }

    public string FileName => Path.GetFileName(FilePath);

    // Положение и размер в мировых координатах (не экранных)
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>
    /// Лучшая из загруженных копий для нужного уровня: сначала такая же или крупнее,
    /// потом, если крупнее нет, самая большая из мелких.
    /// </summary>
    public Bitmap? GetBitmapFor(int desiredLevel)
    {
        for (int i = desiredLevel; i < Levels.Length; i++)
        {
            if (Levels[i] is { } bigger) return bigger;
        }

        for (int i = desiredLevel - 1; i >= 0; i--)
        {
            if (Levels[i] is { } smaller) return smaller;
        }

        return null;
    }

    /// <summary>Самая крупная из загруженных копий.</summary>
    public Bitmap? GetBestBitmap()
    {
        for (int i = Levels.Length - 1; i >= 0; i--)
        {
            if (Levels[i] is { } bitmap) return bitmap;
        }

        return null;
    }

    public void Dispose()
    {
        IsDisposed = true;

        for (int i = 0; i < Levels.Length; i++)
        {
            var bitmap = Levels[i];
            Levels[i] = null;
            bitmap?.Dispose();
        }
    }
}