using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Scatter;

/// <summary>Содержимое settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Последние открытые папки, самая свежая первой.</summary>
    public List<string> RecentFolders { get; set; } = new();
}

/// <summary>
/// Настройки лежат в %AppData%\Scatter\settings.json на Windows
/// и в ~/.config/Scatter/settings.json на Linux.
/// </summary>
public static class SettingsStore
{
    private const int MaxRecent = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Scatter", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();

            using var stream = File.OpenRead(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(stream, Json);
            if (settings == null) return new AppSettings();

            settings.RecentFolders ??= new List<string>();
            return settings;
        }
        catch
        {
            return new AppSettings(); // битый файл настроек не должен ронять программу
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            LayoutStore.TryWrite(FilePath, JsonSerializer.Serialize(settings, Json));
        }
        catch
        {
            // настройки не критичны
        }
    }

    /// <summary>Ставит папку на первое место в списке (если она там уже была, переносит).</summary>
    public static void AddRecent(AppSettings settings, string folder)
    {
        var full = Normalize(folder);
        Remove(settings, full);
        settings.RecentFolders.Insert(0, full);

        if (settings.RecentFolders.Count > MaxRecent)
            settings.RecentFolders.RemoveRange(MaxRecent, settings.RecentFolders.Count - MaxRecent);
    }

    public static void Remove(AppSettings settings, string folder)
    {
        var full = Normalize(folder);
        var comparer = LayoutStore.FileNameComparer;
        settings.RecentFolders.RemoveAll(p => comparer.Equals(Normalize(p), full));
    }

    private static string Normalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);

            // Корень диска (C:\ или /) не трогаем, у остальных убираем хвостовой разделитель
            if (full.Length > (root?.Length ?? 0))
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return full;
        }
        catch
        {
            return path;
        }
    }
}