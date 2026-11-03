namespace GRAR.Models;

/// <summary>Archive formats that can be read (and, where supported, written).</summary>
public enum ArchiveFormat
{
    Zip,
    SevenZip,
    Tar,
    TarGz,
    Rar,
}

public enum UpdateMode
{
    /// <summary>Add new files and replace files that already exist in the archive.</summary>
    AddAndReplace,
    /// <summary>Only add files that are not present in the archive yet.</summary>
    AddNewOnly,
    /// <summary>Only replace files that already exist and are newer.</summary>
    RefreshExisting,
}

public enum OverwriteMode
{
    Ask,
    Overwrite,
    Skip,
}

public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>An entry inside an archive, as read from the container.</summary>
public sealed record ArchiveEntryInfo(
    string Key,
    string Name,
    bool IsDirectory,
    long Size,
    long CompressedSize,
    DateTime? Modified,
    long Crc,
    string Method,
    int? Attrib,
    bool IsEncrypted);

/// <summary>Result of opening an archive for listing.</summary>
public sealed class ArchiveListing
{
    public required string Path { get; init; }
    public string Format { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public bool IsSolid { get; init; }
    public bool IsComplete { get; init; } = true;
    public string? Comment { get; init; }
    public bool HasEncryptedEntries { get; init; }
    public long TotalSize { get; init; }
    public long TotalPacked { get; init; }
    public IReadOnlyList<ArchiveEntryInfo> Entries { get; init; } = Array.Empty<ArchiveEntryInfo>();
}

/// <summary>Progress information reported while an archive operation runs.</summary>
public sealed class ArchiveProgress
{
    public string Phase { get; init; } = string.Empty;
    public string CurrentFile { get; init; } = string.Empty;
    public long BytesProcessed { get; init; }
    public long TotalBytes { get; init; }
    public int FilesProcessed { get; init; }
    public int TotalFiles { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Indeterminate { get; init; }

    public double Percent
    {
        get
        {
            if (Indeterminate)
                return -1;
            if (TotalBytes > 0)
                return Math.Clamp(BytesProcessed * 100.0 / TotalBytes, 0, 100);
            if (TotalFiles > 0)
                return Math.Clamp(FilesProcessed * 100.0 / TotalFiles, 0, 100);
            return 0;
        }
    }

    public long BytesPerSecond
        => Elapsed.TotalSeconds > 0.5 ? (long)(BytesProcessed / Elapsed.TotalSeconds) : 0;
}

/// <summary>Options returned by the "Add to archive" dialog.</summary>
public sealed record AddArchiveOptions(
    string ArchivePath,
    ArchiveFormat Format,
    int CompressionLevel,
    string? Password,
    bool IncludeSubfolders,
    bool AddEmptyFolders,
    bool DeleteAfterArchiving,
    bool TestAfterCreation,
    UpdateMode UpdateMode,
    IReadOnlyList<string> Sources);

/// <summary>Options returned by the "Extract" dialog.</summary>
public sealed record ExtractOptions(
    string Destination,
    bool UseFolderNames,
    OverwriteMode Overwrite,
    bool ShowExtracted,
    bool OpenDestination,
    string? Password);

/// <summary>Thrown when an archive (or one of its entries) needs a password.</summary>
public sealed class ArchivePasswordRequiredException : Exception
{
    public ArchivePasswordRequiredException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>Thrown when extraction would overwrite files and the user has not decided yet.</summary>
public sealed class ArchiveConflictException : Exception
{
    public ArchiveConflictException(int count, string sample)
        : base($"{count} file(s) already exist, e.g. {sample}")
    {
        Count = count;
        Sample = sample;
    }

    public int Count { get; }
    public string Sample { get; }
}

/// <summary>Thrown when the archive cannot be parsed (damage, wrong password, unsupported feature).</summary>
public sealed class ArchiveCorruptedException : Exception
{
    public ArchiveCorruptedException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>Thrown when the requested operation is not supported for this container.</summary>
public sealed class ArchiveUnsupportedException : Exception
{
    public ArchiveUnsupportedException(string message) : base(message) { }
}

public static class ArchiveFormats
{
    public static readonly string[] ReadableExtensions =
        { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz" };

    public static bool IsArchiveFile(string path)
        => ReadableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string DisplayName(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "ZIP",
        ArchiveFormat.SevenZip => "7Z",
        ArchiveFormat.Tar => "TAR",
        ArchiveFormat.TarGz => "TAR.GZ",
        ArchiveFormat.Rar => "RAR",
        _ => format.ToString(),
    };

    public static string Extension(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => ".zip",
        ArchiveFormat.SevenZip => ".7z",
        ArchiveFormat.Tar => ".tar",
        ArchiveFormat.TarGz => ".tgz",
        ArchiveFormat.Rar => ".rar",
        _ => ".zip",
    };

    public static ArchiveFormat FromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".7z" => ArchiveFormat.SevenZip,
        ".tar" => ArchiveFormat.Tar,
        ".tgz" => ArchiveFormat.TarGz,
        ".gz" => ArchiveFormat.TarGz,
        ".rar" => ArchiveFormat.Rar,
        _ => ArchiveFormat.Zip,
    };

    public static string FormatFromPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".zip" => "ZIP",
            ".rar" => "RAR",
            ".7z" => "7-Zip",
            ".tar" => "TAR",
            ".gz" or ".tgz" => "GZip",
            ".bz2" => "BZip2",
            ".xz" => "XZ",
            _ => "Archive",
        };
    }
}
