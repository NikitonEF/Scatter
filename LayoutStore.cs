using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scatter;

public enum SaveResult
{
    SavedToFolder,
    SavedToAppData,
    Failed
}

/// <summary>
/// Хранит раскладку в файле .scatter.json внутри папки с картинками.
/// Если в папку писать нельзя (только для чтения, диск защищён), пишет в
/// папку приложения (%AppData% на Windows, ~/.config на Linux), не падая.
/// </summary>
public static class LayoutStore
{
    public const string FileName = ".scatter.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Как сравнивать имена файлов: на Windows без учёта регистра, на Linux и macOS как есть.</summary>
    public static StringComparer FileNameComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string FolderFilePath(string folder) => Path.Combine(folder, FileName);

    private static string AppDataFilePath(string folder)
    {
        var full = Path.GetFullPath(folder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) full = full.ToLowerInvariant();

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Scatter", "layouts");

        return Path.Combine(dir, hash + ".json");
    }

    /// <summary>
    /// Читает раскладку для папки. Если есть и файл в папке, и запасной в AppData,
    /// берётся тот, что свежее. Битые файлы игнорируются.
    /// </summary>
    public static LayoutData? Load(string folder)
    {
        string[] candidates;
        try
        {
            candidates = new[] { FolderFilePath(folder), AppDataFilePath(folder) }
                .Where(File.Exists)
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .ToArray();
        }
        catch
        {
            return null;
        }

        foreach (var path in candidates)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var data = JsonSerializer.Deserialize<LayoutData>(stream, Json);
                if (data != null) return data;
            }
            catch
            {
                // битый файл, пробуем следующий
            }
        }

        return null;
    }

    public static SaveResult Save(string folder, LayoutData data)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(data, Json);
        }
        catch
        {
            return SaveResult.Failed;
        }

        if (TryWrite(FolderFilePath(folder), json)) return SaveResult.SavedToFolder;
        if (TryWrite(AppDataFilePath(folder), json)) return SaveResult.SavedToAppData;
        return SaveResult.Failed;
    }

    /// <summary>Пишет через временный файл, чтобы сбой посреди записи не испортил старый.</summary>
    internal static bool TryWrite(string path, string content)
    {
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, content, Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            try { File.Delete(temp); } catch { /* ничего страшного */ }
            return false;
        }
    }
}