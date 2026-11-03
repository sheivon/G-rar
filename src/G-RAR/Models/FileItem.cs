using Avalonia.Media;
using GRAR.Helpers;

namespace GRAR.Models;

/// <summary>Where a listed row comes from.</summary>
public enum ItemOrigin
{
    Disk,
    Archive,
}

/// <summary>A single row in the file list — either a disk file or an archive entry.</summary>
public sealed class FileItem
{
    public required string Name { get; init; }
    public ItemOrigin Origin { get; init; }

    /// <summary>Full disk path, or the virtual path ("archive.zip/folder/file") for archive entries.</summary>
    public required string FullPath { get; init; }

    /// <summary>Key of the entry inside the archive (only for <see cref="ItemOrigin.Archive"/>).</summary>
    public string? EntryKey { get; init; }

    /// <summary>Archive that owns this entry (only for <see cref="ItemOrigin.Archive"/>).</summary>
    public string? ArchivePath { get; init; }

    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public long CompressedSize { get; init; } = -1;
    public DateTime? Modified { get; init; }
    public string TypeName { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public string Attributes { get; init; } = string.Empty;
    public long Crc { get; init; }
    public string Method { get; init; } = string.Empty;
    public bool IsEncrypted { get; init; }
    public FileCategory Category { get; init; } = FileCategory.File;

    public bool IsArchive => Category == FileCategory.Archive;
    public bool FromArchive => Origin == ItemOrigin.Archive;

    public string SizeText => IsDirectory ? string.Empty : Fmt.Size(Size);
    public string PackedText => CompressedSize >= 0 && !IsDirectory ? Fmt.Size(CompressedSize) : string.Empty;
    public string RatioText => IsDirectory || Size <= 0 || CompressedSize < 0 ? string.Empty : Fmt.Ratio(Size, CompressedSize);

    /// <summary>Numeric sort key for the "Ratio" column (packed * 1000 / size).</summary>
    public double RatioValue => Size > 0 && CompressedSize >= 0 ? CompressedSize * 1000.0 / Size : double.MaxValue;
    public string ModifiedText => Fmt.Date(Modified);
    public string CrcText => IsDirectory ? string.Empty : Fmt.Crc(Crc);
    public string IconKey => IsDirectory ? FileTypes.IconKey(FileCategory.Folder) : FileTypes.IconKey(Category);

    public IImage? Icon => Icons.Get(IconKey);
    public string ToolTip => IsDirectory ? FullName : $"{FullPath}\n{TypeName}{(IsDirectory ? "" : " · " + Fmt.Size(Size))}";

    /// <summary>Path shown in the status bar / copyable path.</summary>
    public string FullName => FullPath;

    public static FileItem FromDisk(string path, bool showAttributes = true)
    {
        var isDirectory = Directory.Exists(path);
        var name = Path.GetFileName(path);
        if (name.Length == 0)
            name = path;

        string typeName;
        FileCategory category;
        long size = 0;
        DateTime? modified = null;
        var extension = string.Empty;

        if (isDirectory)
        {
            typeName = "File folder";
            category = FileCategory.Folder;
            try
            {
                var info = new DirectoryInfo(path);
                modified = info.LastWriteTime;
            }
            catch
            {
                // ignore inaccessible metadata
            }
        }
        else
        {
            extension = Path.GetExtension(path);
            (typeName, category) = FileTypes.Describe(extension);
            try
            {
                var info = new FileInfo(path);
                size = info.Length;
                modified = info.LastWriteTime;
            }
            catch
            {
                // ignore inaccessible metadata
            }
        }

        return new FileItem
        {
            Name = name,
            Origin = ItemOrigin.Disk,
            FullPath = path,
            IsDirectory = isDirectory,
            Size = size,
            Modified = modified,
            TypeName = typeName,
            Extension = extension,
            Category = category,
            Attributes = showAttributes ? DiskAttributes(path, isDirectory) : string.Empty,
        };
    }

    public static FileItem FromArchiveEntry(ArchiveEntryInfo entry, string archivePath)
    {
        var (typeName, category) = entry.IsDirectory
            ? ("File folder", FileCategory.Folder)
            : FileTypes.Describe(Path.GetExtension(entry.Name));

        return new FileItem
        {
            Name = entry.Name,
            Origin = ItemOrigin.Archive,
            FullPath = archivePath + "/" + entry.Key,
            EntryKey = entry.Key,
            ArchivePath = archivePath,
            IsDirectory = entry.IsDirectory,
            Size = entry.Size,
            CompressedSize = entry.CompressedSize,
            Modified = entry.Modified,
            TypeName = typeName,
            Extension = Path.GetExtension(entry.Name),
            Category = category,
            Attributes = FormatAttributes(entry),
            Crc = entry.Crc,
            Method = entry.Method,
            IsEncrypted = entry.IsEncrypted,
        };
    }

    private static string FormatAttributes(ArchiveEntryInfo entry)
    {
        if (entry.Attrib is not { } attrib)
            return string.Empty;

        // Tar stores the unix mode in the attribute field.
        if (attrib >= 0x1000 || (attrib & 0xF000) != 0)
        {
            var mode = attrib & 0xFFF;
            Span<char> buffer = stackalloc char[9];
            var flags = new[] { 'r', 'w', 'x' };
            for (var i = 0; i < 9; i++)
                buffer[i] = (mode & (1 << (8 - i))) != 0 ? flags[i % 3] : '-';
            return new string(buffer);
        }

        var text = string.Empty;
        if ((attrib & 0x01) != 0) text += "R";
        if ((attrib & 0x02) != 0) text += "H";
        if ((attrib & 0x04) != 0) text += "S";
        if ((attrib & 0x20) != 0) text += "A";
        return text;
    }

    private static string DiskAttributes(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var attributes = File.GetAttributes(path);
                var text = string.Empty;
                if ((attributes & FileAttributes.ReadOnly) != 0) text += "R";
                if ((attributes & FileAttributes.Hidden) != 0) text += "H";
                if ((attributes & FileAttributes.System) != 0) text += "S";
                if (!isDirectory && (attributes & FileAttributes.Archive) != 0) text += "A";
                if ((attributes & FileAttributes.Directory) != 0) text += "D";
                return text;
            }
            catch
            {
                return string.Empty;
            }
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            Span<char> buffer = stackalloc char[9];
            buffer[0] = (mode & UnixFileMode.UserRead) != 0 ? 'r' : '-';
            buffer[1] = (mode & UnixFileMode.UserWrite) != 0 ? 'w' : '-';
            buffer[2] = (mode & UnixFileMode.UserExecute) != 0 ? 'x' : '-';
            buffer[3] = (mode & UnixFileMode.GroupRead) != 0 ? 'r' : '-';
            buffer[4] = (mode & UnixFileMode.GroupWrite) != 0 ? 'w' : '-';
            buffer[5] = (mode & UnixFileMode.GroupExecute) != 0 ? 'x' : '-';
            buffer[6] = (mode & UnixFileMode.OtherRead) != 0 ? 'r' : '-';
            buffer[7] = (mode & UnixFileMode.OtherWrite) != 0 ? 'w' : '-';
            buffer[8] = (mode & UnixFileMode.OtherExecute) != 0 ? 'x' : '-';
            return new string(buffer);
        }
        catch
        {
            return string.Empty;
        }
    }
}
