namespace GRAR.Models;

/// <summary>Persisted application options.</summary>
public sealed class AppSettings
{
    // Window / layout
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 780;
    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double TreeWidth { get; set; } = 250;
    public bool Maximized { get; set; }

    // Browsing
    public bool ShowHiddenFiles { get; set; }
    public bool RestoreLastFolder { get; set; } = true;
    public string LastFolder { get; set; } = string.Empty;

    // Confirmations
    public bool ConfirmDelete { get; set; } = true;
    public bool ConfirmExit { get; set; }

    // "Add to archive" defaults
    public string DefaultFormat { get; set; } = "zip";
    public int CompressionLevel { get; set; } = 5;
    public bool IncludeSubfolders { get; set; } = true;
    public bool AddEmptyFolders { get; set; } = true;
    public bool DeleteAfterArchiving { get; set; }
    public bool TestAfterCreation { get; set; }
    public string LastAddDirectory { get; set; } = string.Empty;

    // Extraction defaults
    public string LastExtractPath { get; set; } = string.Empty;
    public bool ExtractUseFolderNames { get; set; } = true;
    public int ExtractOverwriteMode { get; set; }      // 0 = ask, 1 = overwrite, 2 = skip
    public bool ExtractShowFiles { get; set; }
    public bool ExtractOpenFolder { get; set; }

    // Recent files / favourites
    public List<string> RecentArchives { get; set; } = new();
    public List<FavoriteEntry> Favorites { get; set; } = new();
}

public sealed class FavoriteEntry
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
