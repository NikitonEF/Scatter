using System;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;

namespace Scatter;

public static class ImageLoader
{
    /// <summary>
    /// Ширины уровней детализации (в пикселях). Каждый следующий вдвое больше предыдущего,
    /// поэтому картинку никогда не приходится сжимать больше, чем в два раза.
    /// </summary>
    public static readonly int[] LevelWidths = { 32, 64, 128, 256, 512, 1024, 2048 };

    public const int LevelCount = 7;

    /// <summary>Уровни 0..2 (32, 64, 128) лежат в памяти у всех картинок всегда.</summary>
    public const int ResidentLevels = 3;

    /// <summary>Самый крупный из постоянных уровней. Он есть у любой загруженной картинки.</summary>
    public const int ThumbLevel = 2;

    /// <summary>
    /// Наименьший уровень, ширина которого не меньше нужной (в физических пикселях экрана).
    /// Если картинка крупнее самого большого уровня, возвращается самый большой.
    /// </summary>
    public static int LevelFor(double widthPx)
    {
        for (int i = 0; i < LevelCount; i++)
        {
            if (LevelWidths[i] >= widthPx) return i;
        }

        return LevelCount - 1;
    }

    /// <summary>
    /// Читает файл и декодирует копию заданной ширины.
    /// Можно вызывать из любого потока. При ошибке возвращает null.
    /// </summary>
    public static Bitmap? TryDecode(string path, int width)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            return null; // битый файл или неподдерживаемый формат
        }
    }

    /// <summary>
    /// Делает уменьшенную копию из уже готовой, без повторного чтения файла.
    /// Можно вызывать из любого потока. При ошибке возвращает null.
    /// </summary>
    public static Bitmap? TryScale(Bitmap source, int width)
    {
        try
        {
            int h = Math.Max(1, (int)Math.Round(source.PixelSize.Height * (double)width / Math.Max(1, source.PixelSize.Width)));
            return source.CreateScaledBitmap(new PixelSize(width, h), BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            return null;
        }
    }
}