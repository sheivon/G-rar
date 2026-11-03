using System.Text.Json;
using GRAR.Models;

namespace GRAR.Services;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in the user's config folder.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private bool _dirty;

    private SettingsService()
    {
        FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "G-RAR",
            "settings.json");

        Settings = Load();
    }

    public static SettingsService Instance { get; } = new();

    public string FilePath { get; }

    public AppSettings Settings { get; private set; }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt settings fall back to defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var json = JsonSerializer.Serialize(Settings, JsonOptions);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, FilePath, overwrite: true);
                _dirty = false;
            }
            catch
            {
                // Never let settings persistence break the application.
            }
        }
    }

    /// <summary>Marks the settings as changed; they are written on application exit.</summary>
    public void Touch() => _dirty = true;

    public bool HasChanges
    {
        get { lock (_gate) return _dirty; }
    }

    public void AddRecentArchive(string path)
    {
        var recent = Settings.RecentArchives;
        recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, path);
        if (recent.Count > 12)
            recent.RemoveRange(12, recent.Count - 12);
        Touch();
    }

    public void RemoveRecentArchive(string path)
    {
        Settings.RecentArchives.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Touch();
    }

    public void AddFavorite(string path)
    {
        if (Settings.Favorites.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;

        Settings.Favorites.Add(new FavoriteEntry
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path,
            Path = path,
        });
        Touch();
    }

    public void RemoveFavorite(string path)
    {
        Settings.Favorites.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        Touch();
    }
}
