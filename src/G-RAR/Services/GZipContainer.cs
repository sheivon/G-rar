using System.IO.Compression;
using GRAR.Helpers;
using GRAR.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace GRAR.Services;

/// <summary>
/// Reads gzip containers. A gzip wrapping a TAR archive (".tar.gz", ".tgz") is inflated into a
/// temporary file and opened as a real archive; a gzip wrapping a plain file is presented as a
/// single entry archive, which is how WinRAR shows them too.
/// </summary>
internal static class GZipContainer
{
    private static readonly string TempDirectory = Path.Combine(Path.GetTempPath(), "G-RAR");
    private const string TempPrefix = "inflate-";
    private static readonly TimeSpan StaleAge = TimeSpan.FromHours(24);

    /// <summary>True when the file starts with the gzip magic number.</summary>
    public static bool IsGZipFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[2];
            return stream.Read(magic) == 2 && magic[0] == 0x1F && magic[1] == 0x8B;
        }
        catch
        {
            return false;
        }
    }

    public static IArchive Open(string path, ReaderOptions options)
    {
        CleanupStale();

        var temp = CreateTempPath();
        try
        {
            long compressedSize = new FileInfo(path).Length;
            var modified = File.GetLastWriteTime(path);
            var (size, crc) = Inflate(path, temp);

            var payload = TryOpenArchive(temp, options);
            if (payload is { } opened)
                return new TempFileArchive(opened.Archive, opened.Stream, temp);

            return new SingleFileArchive(temp, EntryNameFor(path), size, compressedSize, crc, modified);
        }
        catch (ArchiveCorruptedException)
        {
            TryDelete(temp);
            throw;
        }
        catch (InvalidDataException ex)
        {
            TryDelete(temp);
            throw new ArchiveCorruptedException($"\"{Path.GetFileName(path)}\" is not a valid gzip file.", ex);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static (long Size, uint Crc) Inflate(string path, string temp)
    {
        using var source = File.OpenRead(path);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);

        var hasher = new Crc32();
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.Update(buffer.AsSpan(0, read));
            target.Write(buffer, 0, read);
            size += read;
        }

        return (size, hasher.Value);
    }

    private static (IArchive Archive, Stream Stream)? TryOpenArchive(string temp, ReaderOptions options)
    {
        Stream? stream = null;
        try
        {
            stream = File.OpenRead(temp);
            return (ArchiveFactory.Open(stream, options), stream);
        }
        catch (Exception ex) when (ex is InvalidFormatException or SharpCompressException
                                       or NotSupportedException or ArgumentException or IOException)
        {
            stream?.Dispose();
            return null;
        }
    }

    /// <summary>"report.txt.gz" becomes "report.txt".</summary>
    private static string EntryNameFor(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var suffix in new[] { ".gz", ".tgz", ".taz" })
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length];
        }

        return name;
    }

    private static string CreateTempPath()
    {
        Directory.CreateDirectory(TempDirectory);
        return Path.Combine(TempDirectory, TempPrefix + Guid.NewGuid().ToString("N") + ".tmp");
    }

    /// <summary>Best effort removal of leftovers from sessions that ended without disposing.</summary>
    private static void CleanupStale()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(TempDirectory, TempPrefix + "*.tmp"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - StaleAge)
                    TryDelete(file);
            }
        }
        catch
        {
            // Cleaning up is never worth failing an open for.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignored; the stale sweep will retry later.
        }
    }
}

/// <summary>Owns the inflated temporary file of a gzip container and deletes it on dispose.</summary>
internal sealed class TempFileArchive : IArchive
{
    private readonly IArchive _inner;
    private readonly Stream _stream;
    private readonly string _tempPath;
    private bool _disposed;

    public TempFileArchive(IArchive inner, Stream stream, string tempPath)
    {
        _inner = inner;
        _stream = stream;
        _tempPath = tempPath;
    }

    public IEnumerable<IArchiveEntry> Entries => _inner.Entries;
    public IEnumerable<IVolume> Volumes => _inner.Volumes;
    public ArchiveType Type => _inner.Type;
    public bool IsSolid => _inner.IsSolid;
    public bool IsComplete => _inner.IsComplete;
    public long TotalSize => _inner.TotalSize;
    public long TotalUncompressSize => _inner.TotalUncompressSize;
    public IReader ExtractAllEntries() => _inner.ExtractAllEntries();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _inner.Dispose();
        _stream.Dispose();

        try
        {
            File.Delete(_tempPath);
        }
        catch
        {
            // Best effort; the stale sweep retries later.
        }
    }
}

/// <summary>A gzip that does not contain a TAR archive: one file, shown like any other entry.</summary>
internal sealed class SingleFileArchive : IArchive
{
    private readonly string _tempPath;
    private readonly SingleFileEntry _entry;

    public SingleFileArchive(string tempPath, string key, long size, long compressedSize, uint crc, DateTime modified)
    {
        _tempPath = tempPath;
        _entry = new SingleFileEntry(this, tempPath, key, size, compressedSize, crc, modified);
    }

    public IEnumerable<IArchiveEntry> Entries => [_entry];
    public IEnumerable<IVolume> Volumes => [];
    public ArchiveType Type => ArchiveType.GZip;
    public bool IsSolid => false;
    public bool IsComplete => true;
    public long TotalSize => _entry.Size;
    public long TotalUncompressSize => _entry.Size;
    public IReader ExtractAllEntries() => throw new NotSupportedException();

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempPath))
                File.Delete(_tempPath);
        }
        catch
        {
            // Best effort; the stale sweep retries later.
        }
    }
}

internal sealed class SingleFileEntry : IArchiveEntry
{
    private readonly string _tempPath;

    public SingleFileEntry(IArchive archive, string tempPath, string key, long size, long compressedSize, uint crc, DateTime modified)
    {
        Archive = archive;
        _tempPath = tempPath;
        Key = key;
        Size = size;
        CompressedSize = compressedSize;
        Crc = crc;
        LastModifiedTime = modified;
    }

    public IArchive Archive { get; }
    public string Key { get; }
    public long Size { get; }
    public long CompressedSize { get; }
    public long Crc { get; }
    public DateTime? LastModifiedTime { get; }
    public DateTime? ArchivedTime => null;
    public DateTime? CreatedTime => null;
    public DateTime? LastAccessedTime => null;
    public int? Attrib => null;
    public string? LinkTarget => null;
    public CompressionType CompressionType => CompressionType.GZip;
    public bool IsDirectory => false;
    public bool IsEncrypted => false;
    public bool IsSplitAfter => false;
    public bool IsSolid => false;
    public bool IsComplete => true;
    public int VolumeIndexFirst => 0;
    public int VolumeIndexLast => 0;

    public Stream OpenEntryStream()
        => new FileStream(_tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);

    public Task<Stream> OpenEntryStreamAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(OpenEntryStream());
}
