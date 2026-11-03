using System.Diagnostics;
using System.IO.Compression;
using GRAR.Helpers;
using GRAR.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;

namespace GRAR.Services;

/// <summary>
/// Reading, extraction, testing, creation and editing of archives.
/// Reading is powered by SharpCompress (zip, rar, 7z, tar, gzip, ...); ZIP and TAR
/// writing happens in-process, 7z/RAR creation uses the external tools when present.
/// </summary>
public static class ArchiveService
{
    // ================================================================ reading

    public static Task<ArchiveListing> ReadListingAsync(string path, string? password, CancellationToken ct = default)
        => Task.Run(() => ReadListing(path, password), ct);

    public static ArchiveListing ReadListing(string path, string? password)
    {
        using var archive = OpenArchive(path, password);
        var entries = ToEntries(archive, out var totalSize, out var totalPacked, out var encrypted);

        return new ArchiveListing
        {
            Path = path,
            Format = FormatLabel(path, archive.Type),
            FileSize = new FileInfo(path).Length,
            IsSolid = archive.IsSolid,
            IsComplete = archive.IsComplete,
            HasEncryptedEntries = encrypted,
            TotalSize = totalSize,
            TotalPacked = totalPacked,
            Entries = entries,
        };
    }

    private static IReadOnlyList<ArchiveEntryInfo> ToEntries(
        IArchive archive,
        out long totalSize,
        out long totalPacked,
        out bool encrypted)
    {
        var entries = new List<ArchiveEntryInfo>();
        totalSize = 0;
        totalPacked = 0;
        encrypted = false;

        foreach (var entry in archive.Entries)
        {
            var key = PathUtil.NormalizeKey(entry.Key);
            if (key.Length == 0)
                continue;

            var isDirectory = entry.IsDirectory || key.EndsWith('/');
            var name = PathUtil.GetFileName(key.TrimEnd('/'));
            if (name.Length == 0)
                name = key;

            // Some formats throw from individual accessors (TAR: Attrib, split RAR: Crc),
            // and a listing must never fail because one optional field is unavailable.
            var size = Safe(() => entry.Size, 0L);
            var packed = Safe(() => entry.CompressedSize, 0L);

            if (!isDirectory)
            {
                totalSize += size;
                totalPacked += packed;
            }

            encrypted |= entry.IsEncrypted;

            entries.Add(new ArchiveEntryInfo(
                key,
                name,
                isDirectory,
                size,
                packed,
                Safe(() => entry.LastModifiedTime, (DateTime?)null),
                Safe(() => entry.Crc, 0L),
                MethodName(entry.CompressionType),
                Safe(() => entry.Attrib, (int?)null),
                entry.IsEncrypted));
        }

        return entries;
    }

    /// <summary>
    /// Reads an optional entry field without letting a format specific quirk (TAR does not
    /// implement <c>Attrib</c>, split RAR entries throw from <c>Crc</c>) break the listing.
    /// </summary>
    private static T Safe<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    // ============================================================= extraction

    public static Task ExtractAsync(
        string archivePath,
        IReadOnlyList<string>? keys,
        ExtractOptions options,
        IProgress<ArchiveProgress>? progress,
        CancellationToken ct = default)
        => Task.Run(() => Extract(archivePath, keys, options, progress, ct), ct);

    private static void Extract(
        string archivePath,
        IReadOnlyList<string>? keys,
        ExtractOptions options,
        IProgress<ArchiveProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(options.Destination);

        using var archive = OpenArchive(archivePath, options.Password);
        var selected = keys is null ? null : new HashSet<string>(keys, StringComparer.Ordinal);

        var plan = new List<(IArchiveEntry Entry, string Target, string Display)>();
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var key = PathUtil.NormalizeKey(entry.Key);
            if (key.Length == 0 || (selected is not null && !selected.Contains(key)))
                continue;

            if (entry.IsDirectory && !options.UseFolderNames)
                continue;

            var target = ResolveTarget(options, key);
            if (target is null)
                continue;

            plan.Add((entry, target, options.UseFolderNames ? key : PathUtil.GetFileName(key)));
        }

        if (options.Overwrite == OverwriteMode.Ask)
        {
            var conflicts = plan.Where(p => !p.Entry.IsDirectory && File.Exists(p.Target)).ToList();
            if (conflicts.Count > 0)
                throw new ArchiveConflictException(conflicts.Count, conflicts[0].Display);
        }

        var files = plan.Where(p => !p.Entry.IsDirectory).ToList();
        var reporter = new ProgressReporter(progress, "Extracting", plan.Count, files.Sum(p => p.Entry.Size));

        foreach (var (entry, target, _) in plan)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                reporter.FileDone();
                continue;
            }

            if (File.Exists(target) && options.Overwrite == OverwriteMode.Skip)
            {
                reporter.FileDone();
                continue;
            }

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            reporter.OnFile(entry.Key);

            try
            {
                using var source = OpenEntryStream(entry);
                using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, read);
                    reporter.OnBytes(read);
                }
            }
            catch (CryptographicException ex)
            {
                TryDelete(target);
                throw PasswordOrDamage(options.Password, ex);
            }
            catch
            {
                TryDelete(target);
                throw;
            }

            try
            {
                if (entry.LastModifiedTime is { } time && time > DateTime.MinValue)
                    File.SetLastWriteTime(target, time);
            }
            catch
            {
                // Timestamps are best effort.
            }

            reporter.FileDone();
        }

        reporter.Finish();
    }

    private static string? ResolveTarget(ExtractOptions options, string key)
    {
        var relative = options.UseFolderNames ? key : PathUtil.GetFileName(key);
        var safe = PathUtil.SanitizeRelativePath(relative);
        return safe is null ? null : Path.Combine(options.Destination, safe);
    }

    // ================================================================ testing

    public static Task TestAsync(
        string archivePath,
        string? password,
        IProgress<ArchiveProgress>? progress,
        CancellationToken ct = default)
        => Task.Run(() => Test(archivePath, password, progress, ct), ct);

    private static void Test(string archivePath, string? password, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        using var archive = OpenArchive(archivePath, password);

        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        var reporter = new ProgressReporter(progress, "Testing", entries.Count, entries.Sum(e => e.Size));
        var buffer = new byte[81920];

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            reporter.OnFile(entry.Key);

            long read = 0;
            try
            {
                using var source = OpenEntryStream(entry);
                int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    read += count;
                    reporter.OnBytes(count);
                }
            }
            catch (CryptographicException ex)
            {
                throw PasswordOrDamage(password, ex);
            }

            if (entry.Size >= 0 && read != entry.Size)
                throw new ArchiveCorruptedException($"Size mismatch for \"{entry.Key}\" (expected {entry.Size}, got {read}).");

            reporter.FileDone();
        }

        reporter.Finish();
    }

    // ============================================ extracting a single entry

    /// <summary>Extracts one entry into a temp folder and returns the created path.</summary>
    public static string ExtractToTemp(string archivePath, string key, string? password, CancellationToken ct = default)
    {
        using var archive = OpenArchive(archivePath, password);

        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(PathUtil.NormalizeKey(e.Key), key, StringComparison.Ordinal));

        if (entry is null)
            throw new FileNotFoundException($"\"{key}\" was not found in the archive.");

        var name = PathUtil.SanitizeFileName(PathUtil.GetFileName(key.TrimEnd('/')), "entry");
        var folder = Path.Combine(Path.GetTempPath(), "G-RAR", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, name);
        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(target);
            return target;
        }

        try
        {
            using var source = OpenEntryStream(entry);
            using var destination = File.Create(target);
            source.CopyTo(destination, 81920);
        }
        catch (CryptographicException ex)
        {
            TryDelete(target);
            throw PasswordOrDamage(password, ex);
        }

        if (entry.LastModifiedTime is { } time && time > DateTime.MinValue)
        {
            try { File.SetLastWriteTime(target, time); } catch { /* ignore */ }
        }

        return target;
    }

    // ============================================================== creation

    public static async Task CreateAsync(AddArchiveOptions options, IProgress<ArchiveProgress>? progress, CancellationToken ct = default)
    {
        var sources = await Task.Run(() => CollectSources(options), ct).ConfigureAwait(false);

        if (sources.Count == 0)
            throw new InvalidOperationException("There is nothing to add to the archive.");

        await Task.Run(() => CreateInProcessOrExternal(options, sources, progress, ct), ct).ConfigureAwait(false);
    }

    /// <summary>Adds files to an existing archive (creating it when missing) or removes entries.</summary>
    public static Task EditArchiveAsync(
        string archivePath,
        ArchiveFormat format,
        IReadOnlyList<string>? removeKeys,
        IReadOnlyList<string>? addPaths,
        string? password,
        UpdateMode updateMode,
        int compressionLevel,
        IProgress<ArchiveProgress>? progress,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (removeKeys is { Count: > 0 } && addPaths is { Count: > 0 })
                throw new ArchiveUnsupportedException("Removing and adding in a single step is not supported.");

            if (removeKeys is { Count: > 0 })
            {
                if (format is ArchiveFormat.SevenZip or ArchiveFormat.Rar)
                {
                    RunExternalDelete(format, archivePath, removeKeys, password, progress, ct);
                }
                else
                {
                    Rewrite(archivePath, format, new List<SourceEntry>(),
                        new HashSet<string>(removeKeys.Select(PathUtil.NormalizeKey), StringComparer.Ordinal),
                        password, compressionLevel, progress, ct);
                }

                return;
            }

            if (addPaths is { Count: > 0 })
            {
                var options = new AddArchiveOptions(
                    archivePath, format, compressionLevel, password,
                    IncludeSubfolders: true, AddEmptyFolders: true,
                    DeleteAfterArchiving: false, TestAfterCreation: false,
                    updateMode, addPaths);

                var sources = CollectSources(options);
                CreateInProcessOrExternal(options, sources, progress, ct);
            }
        }, ct);
    }

    private static void CreateInProcessOrExternal(AddArchiveOptions options, List<SourceEntry> sources, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        switch (options.Format)
        {
            case ArchiveFormat.SevenZip:
            case ArchiveFormat.Rar:
                CreateWithExternalTool(options, progress, ct);
                break;
            case ArchiveFormat.Tar:
            case ArchiveFormat.TarGz:
                CreateTar(options, sources, progress, ct);
                break;
            default:
                CreateZip(options, sources, progress, ct);
                break;
        }
    }

    private static void CreateZip(AddArchiveOptions options, List<SourceEntry> sources, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var tempPath = options.ArchivePath + ".gtmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var writer = new ZipArchiveWriter(stream, new ZipWriterOptions
                {
                    CompressionLevel = ToCompressionLevel(options.CompressionLevel),
                    Password = options.Password,
                }, leaveOpen: true);

                using (writer)
                {
                    WriteEntries(new ZipSinkAdapter(writer), options.ArchivePath, options.Format, sources,
                        options.UpdateMode, options.Password, progress, ct);
                }

                stream.Flush(true);
            }

            File.Move(tempPath, options.ArchivePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void CreateTar(AddArchiveOptions options, List<SourceEntry> sources, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var tempPath = options.ArchivePath + ".gtmp";
        try
        {
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                Stream output = fileStream;
                GZipStream? gzip = null;
                if (options.Format == ArchiveFormat.TarGz)
                {
                    gzip = new GZipStream(fileStream, CompressionLevel.Optimal, leaveOpen: true);
                    output = gzip;
                }

                try
                {
                    using var writer = WriterFactory.Open(output, ArchiveType.Tar, new WriterOptions(CompressionType.None));
                    WriteEntries(new SharpCompressWriterSink(writer), options.ArchivePath, options.Format, sources,
                        options.UpdateMode, password: null, progress, ct);
                }
                finally
                {
                    gzip?.Dispose();
                }
            }

            File.Move(tempPath, options.ArchivePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void CreateWithExternalTool(AddArchiveOptions options, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var executable = options.Format == ArchiveFormat.SevenZip
            ? ExternalTools.FindSevenZip() ?? throw new ArchiveUnsupportedException(ExternalTools.AvailabilityNote(options.Format))
            : ExternalTools.FindRar() ?? throw new ArchiveUnsupportedException(ExternalTools.AvailabilityNote(options.Format));

        progress?.Report(new ArchiveProgress
        {
            Phase = "Creating",
            CurrentFile = Path.GetFileName(options.ArchivePath),
            Indeterminate = true,
        });

        foreach (var group in GroupByDirectory(options.Sources))
        {
            ct.ThrowIfCancellationRequested();
            var arguments = BuildExternalArguments(options.Format, "a", options.ArchivePath, group.Names, options.Password, options.CompressionLevel);
            ExternalTools.Run(executable, arguments, group.Directory, ct);
        }
    }

    private static void RunExternalDelete(
        ArchiveFormat format,
        string archivePath,
        IReadOnlyList<string> keys,
        string? password,
        IProgress<ArchiveProgress>? progress,
        CancellationToken ct)
    {
        var executable = format == ArchiveFormat.SevenZip
            ? ExternalTools.FindSevenZip() ?? throw new ArchiveUnsupportedException(ExternalTools.AvailabilityNote(format))
            : ExternalTools.FindRar() ?? throw new ArchiveUnsupportedException(ExternalTools.AvailabilityNote(format));

        progress?.Report(new ArchiveProgress
        {
            Phase = "Updating",
            CurrentFile = Path.GetFileName(archivePath),
            Indeterminate = true,
        });

        var arguments = new List<string> { "d", "-y" };
        if (!string.IsNullOrEmpty(password))
            arguments.Add("-p" + password);
        if (format == ArchiveFormat.Rar)
            arguments.Add("-idq");
        arguments.Add(archivePath);
        arguments.AddRange(keys);

        ExternalTools.Run(executable, arguments, ct);
    }

    private static List<string> BuildExternalArguments(
        ArchiveFormat format,
        string verb,
        string archivePath,
        IReadOnlyList<string> names,
        string? password,
        int compressionLevel)
    {
        var arguments = new List<string> { verb };

        if (format == ArchiveFormat.SevenZip)
        {
            arguments.Add("-t7z");
            arguments.Add($"-mx={Math.Clamp(compressionLevel, 0, 9)}");
            arguments.Add("-y");
            if (!string.IsNullOrEmpty(password))
            {
                arguments.Add("-p" + password);
                arguments.Add("-mhe=on");
            }
        }
        else
        {
            // RAR spells the level switch -m<0..5>, not -mx=...
            arguments.Add("-r");
            arguments.Add($"-m{Math.Clamp(compressionLevel, 0, 5)}");
            arguments.Add("-idq");
            arguments.Add("-y");
            if (!string.IsNullOrEmpty(password))
                arguments.Add("-p" + password);
        }

        arguments.Add(archivePath);
        arguments.AddRange(names);
        return arguments;
    }

    /// <summary>Groups paths by their parent folder so external tools store names relative to it.</summary>
    private static IEnumerable<(string Directory, List<string> Names)> GroupByDirectory(IReadOnlyList<string> paths)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            if (!groups.TryGetValue(directory, out var list))
                groups[directory] = list = new List<string>();

            list.Add(Path.GetFileName(path));
        }

        return groups.Select(g => (g.Key, g.Value)).ToList();
    }

    // ------------------------------------------------------- in-process writers

    private static void WriteEntries(
        IArchiveSink sink,
        string archivePath,
        ArchiveFormat format,
        List<SourceEntry> sources,
        UpdateMode updateMode,
        string? password,
        IProgress<ArchiveProgress>? progress,
        CancellationToken ct)
    {
        var additions = sources.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var reporter = new ProgressReporter(progress, "Creating", sources.Count, sources.Sum(s => s.Size));

        // 1. Keep the entries of an existing archive of the same format that survive the update mode.
        var existingKeys = new HashSet<string>(StringComparer.Ordinal);
        if (IsSameFormat(archivePath, format) && TryOpenExisting(archivePath, password, out var existing, out var existingEntries))
        {
            using (existing)
            {
                foreach (var entry in existingEntries)
                {
                    ct.ThrowIfCancellationRequested();

                    var key = PathUtil.NormalizeKey(entry.Key);
                    if (key.Length == 0)
                        continue;

                    existingKeys.Add(key);

                    var replaced = additions.TryGetValue(key, out var addition)
                                   && ShouldReplaceExisting(updateMode, addition, entry.LastModifiedTime);
                    if (replaced)
                        continue;

                    reporter.OnFile(key);
                    if (entry.IsDirectory || key.EndsWith('/'))
                    {
                        sink.AddDirectory(key, entry.LastModifiedTime);
                    }
                    else
                    {
                        using var source = OpenEntryStream(entry);
                        sink.AddFile(key, source, entry.LastModifiedTime);
                    }

                    reporter.FileDone();
                }
            }
        }

        // 2. Write the new sources.
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            if (updateMode == UpdateMode.AddNewOnly && existingKeys.Contains(source.Key))
                continue;

            reporter.OnFile(source.Key);

            if (source.IsDirectory)
            {
                sink.AddDirectory(source.Key, source.Modified);
            }
            else
            {
                using var stream = File.OpenRead(source.Path);
                sink.AddFile(source.Key, stream, source.Modified, ModeOf(source.Path));
            }

            reporter.OnBytes(source.Size);
            reporter.FileDone();
        }

        reporter.Finish();
    }

    private static bool ShouldReplaceExisting(UpdateMode mode, SourceEntry addition, DateTime? existingModified)
        => mode switch
        {
            UpdateMode.AddAndReplace => true,
            UpdateMode.AddNewOnly => false,
            _ => addition.Modified > existingModified,
        };

    private static void Rewrite(
        string archivePath,
        ArchiveFormat format,
        List<SourceEntry> additions,
        HashSet<string> removeKeys,
        string? password,
        int compressionLevel,
        IProgress<ArchiveProgress>? progress,
        CancellationToken ct)
    {
        var tempPath = archivePath + ".gtmp";

        try
        {
            if (format is ArchiveFormat.Tar or ArchiveFormat.TarGz)
            {
                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    Stream output = fileStream;
                    GZipStream? gzip = null;
                    if (format == ArchiveFormat.TarGz)
                    {
                        gzip = new GZipStream(fileStream, CompressionLevel.Optimal, leaveOpen: true);
                        output = gzip;
                    }

                    try
                    {
                        using var writer = WriterFactory.Open(output, ArchiveType.Tar, new WriterOptions(CompressionType.None));
                        CopyArchiveContents(new SharpCompressWriterSink(writer), archivePath, additions, removeKeys, password, progress, ct);
                    }
                    finally
                    {
                        gzip?.Dispose();
                    }
                }
            }
            else
            {
                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    var writer = new ZipArchiveWriter(fileStream, new ZipWriterOptions
                    {
                        CompressionLevel = ToCompressionLevel(compressionLevel),
                        Password = password,
                    }, leaveOpen: true);

                    using (writer)
                    {
                        CopyArchiveContents(new ZipSinkAdapter(writer), archivePath, additions, removeKeys, password, progress, ct);
                    }

                    fileStream.Flush(true);
                }
            }

            File.Move(tempPath, archivePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void CopyArchiveContents(
        IArchiveSink sink,
        string archivePath,
        List<SourceEntry> additions,
        HashSet<string> removeKeys,
        string? password,
        IProgress<ArchiveProgress>? progress,
        CancellationToken ct)
    {
        if (File.Exists(archivePath) && TryOpenExisting(archivePath, password, out var existing, out var entries))
        {
            using (existing)
            {
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    var key = PathUtil.NormalizeKey(entry.Key);
                    if (key.Length == 0 || removeKeys.Contains(key))
                        continue;

                    if (entry.IsDirectory || key.EndsWith('/'))
                    {
                        sink.AddDirectory(key, entry.LastModifiedTime);
                    }
                    else
                    {
                        using var source = OpenEntryStream(entry);
                        sink.AddFile(key, source, entry.LastModifiedTime);
                    }
                }
            }
        }

        foreach (var addition in additions)
        {
            ct.ThrowIfCancellationRequested();

            if (addition.IsDirectory)
            {
                sink.AddDirectory(addition.Key, addition.Modified);
            }
            else
            {
                using var stream = File.OpenRead(addition.Path);
                sink.AddFile(addition.Key, stream, addition.Modified, ModeOf(addition.Path));
            }
        }

        progress?.Report(new ArchiveProgress
        {
            Phase = "Updating",
            CurrentFile = Path.GetFileName(archivePath),
            Indeterminate = true,
        });
    }

    // ================================================================ helpers

    private sealed class SourceEntry
    {
        public required string Key { get; init; }
        public required string Path { get; init; }
        public bool IsDirectory { get; init; }
        public long Size { get; init; }
        public DateTime Modified { get; init; }
    }

    private static List<SourceEntry> CollectSources(AddArchiveOptions options)
    {
        var result = new List<SourceEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string key, string path, bool isDirectory, long size, DateTime modified)
        {
            key = PathUtil.NormalizeKey(key);
            if (key.Length == 0 || !seen.Add(key))
                return;

            result.Add(new SourceEntry { Key = key, Path = path, IsDirectory = isDirectory, Size = size, Modified = modified });
        }

        foreach (var source in options.Sources)
        {
            try
            {
                if (File.Exists(source))
                {
                    var info = new FileInfo(source);
                    Add(Path.GetFileName(source), source, false, info.Length, info.LastWriteTime);
                }
                else if (Directory.Exists(source))
                {
                    var folderName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, '/'));
                    if (folderName.Length == 0)
                        folderName = PathUtil.SanitizeFileName(source, "folder");

                    var root = folderName;
                    var dirInfo = new DirectoryInfo(source);
                    Add(root + "/", source, true, 0, dirInfo.LastWriteTime);

                    if (options.IncludeSubfolders)
                    {
                        foreach (var file in FileSystemService.EnumerateFilesRecursive(source, includeSubfolders: true, showHidden: true))
                        {
                            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                            var fileInfo = new FileInfo(file);
                            Add(root + "/" + relative, file, false, fileInfo.Length, fileInfo.LastWriteTime);
                        }

                        var parents = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var key in seen)
                        {
                            if (key.StartsWith(root + "/", StringComparison.Ordinal))
                                parents.Add(PathUtil.GetParentKey(key));
                        }

                        foreach (var directory in FileSystemService.EnumerateDirectoriesRecursive(source))
                        {
                            var relative = Path.GetRelativePath(source, directory).Replace('\\', '/');
                            var key = root + "/" + relative;
                            if (!options.AddEmptyFolders && !parents.Contains(key))
                                continue;

                            var info = new DirectoryInfo(directory);
                            Add(key + "/", directory, true, 0, info.LastWriteTime);
                        }
                    }
                    else
                    {
                        foreach (var file in Directory.EnumerateFiles(source))
                        {
                            var info = new FileInfo(file);
                            Add(root + "/" + info.Name, file, false, info.Length, info.LastWriteTime);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new IOException($"Cannot read \"{source}\": {ex.Message}", ex);
            }
        }

        return result;
    }

    private static IArchive OpenArchive(string path, string? password)
    {
        var options = new ReaderOptions { LookForHeader = true };
        if (!string.IsNullOrEmpty(password))
            options.Password = password;

        try
        {
            // Gzip needs a detour: SharpCompress sees only the outer container, while ".tar.gz"
            // and plain ".gz" files both have to be inflated before they can be read.
            if (GZipContainer.IsGZipFile(path))
                return GZipContainer.Open(path, options);

            return ArchiveFactory.Open(path, options);
        }
        catch (CryptographicException ex)
        {
            throw new ArchivePasswordRequiredException("This archive is password protected.", ex);
        }
        catch (Exception ex) when (ex is InvalidFormatException or SharpCompressException or NotSupportedException)
        {
            throw new ArchiveCorruptedException($"\"{Path.GetFileName(path)}\" cannot be opened: {ex.Message}", ex);
        }
    }

    private static bool TryOpenExisting(string path, string? password, out IArchive archive, out IReadOnlyList<IArchiveEntry> entries)
    {
        archive = null!;
        entries = Array.Empty<IArchiveEntry>();

        try
        {
            archive = OpenArchive(path, password);
            entries = archive.Entries.ToList();
            return true;
        }
        catch
        {
            archive?.Dispose();
            archive = null!;
            return false;
        }
    }

    private static bool IsSameFormat(string archivePath, ArchiveFormat format)
        => File.Exists(archivePath)
           && string.Equals(ArchiveFormats.Extension(format), Path.GetExtension(archivePath), StringComparison.OrdinalIgnoreCase);

    private static Stream OpenEntryStream(IArchiveEntry entry)
    {
        try
        {
            return entry.OpenEntryStream();
        }
        catch (CryptographicException ex)
        {
            throw new ArchivePasswordRequiredException("A password is required to read this entry.", ex);
        }
    }

    private static Exception PasswordOrDamage(string? password, Exception inner)
        => string.IsNullOrEmpty(password)
            ? new ArchivePasswordRequiredException("A password is required to read this archive.", inner)
            : new ArchiveCorruptedException("The password is incorrect or the archive is damaged.", inner);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static UnixFileMode? ModeOf(string path)
    {
        if (OperatingSystem.IsWindows())
            return null;

        try
        {
            return File.GetUnixFileMode(path);
        }
        catch
        {
            return null;
        }
    }

    public static CompressionLevel ToCompressionLevel(int level) => level switch
    {
        <= 0 => CompressionLevel.NoCompression,
        <= 3 => CompressionLevel.Fastest,
        _ => CompressionLevel.Optimal,
    };

    private static string MethodName(CompressionType type) => type switch
    {
        CompressionType.None => "Stored",
        CompressionType.Deflate => "Deflate",
        CompressionType.Deflate64 => "Deflate64",
        CompressionType.BZip2 => "BZip2",
        CompressionType.LZMA => "LZMA",
        CompressionType.PPMd => "PPMd",
        CompressionType.Rar => "RAR",
        CompressionType.GZip => "GZip",
        CompressionType.Xz => "XZ",
        CompressionType.ZStandard => "ZStandard",
        _ => type.ToString(),
    };

    private static string FormatName(ArchiveType type) => type switch
    {
        ArchiveType.Zip => "ZIP",
        ArchiveType.Rar => "RAR",
        ArchiveType.SevenZip => "7-Zip",
        ArchiveType.Tar => "TAR",
        ArchiveType.GZip => "GZip",
        ArchiveType.Ace => "ACE",
        ArchiveType.Arc => "ARC",
        ArchiveType.Arj => "ARJ",
        _ => type.ToString(),
    };

    // ================================================================== sinks

    private interface IArchiveSink
    {
        void AddDirectory(string key, DateTime? modified);
        void AddFile(string key, Stream source, DateTime? modified, UnixFileMode? mode = null);
    }

    private sealed class ZipSinkAdapter : IArchiveSink
    {
        private readonly ZipArchiveWriter _writer;
        public ZipSinkAdapter(ZipArchiveWriter writer) => _writer = writer;
        public void AddDirectory(string key, DateTime? modified) => _writer.AddDirectory(key, modified);
        public void AddFile(string key, Stream source, DateTime? modified, UnixFileMode? mode = null)
            => _writer.AddFile(key, source, modified, mode);
    }

    private sealed class SharpCompressWriterSink : IArchiveSink
    {
        private readonly IWriter _writer;
        public SharpCompressWriterSink(IWriter writer) => _writer = writer;
        public void AddDirectory(string key, DateTime? modified) => _writer.WriteDirectory(key, modified);
        public void AddFile(string key, Stream source, DateTime? modified, UnixFileMode? mode = null)
            => _writer.Write(key, source, modified);
    }

    // =============================================================== progress

    private sealed class ProgressReporter
    {
        private readonly IProgress<ArchiveProgress>? _progress;
        private readonly string _phase;
        private readonly int _totalFiles;
        private readonly long _totalBytes;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _bytes;
        private int _files;
        private string _currentFile = string.Empty;
        private long _lastReport;

        public ProgressReporter(IProgress<ArchiveProgress>? progress, string phase, int totalFiles = 0, long totalBytes = 0)
        {
            _progress = progress;
            _phase = phase;
            _totalFiles = totalFiles;
            _totalBytes = totalBytes;
        }

        public void OnFile(string key)
        {
            _currentFile = key;
            Report(force: false);
        }

        public void OnBytes(long count)
        {
            _bytes += count;
            Report(force: false);
        }

        public void FileDone()
        {
            _files++;
            Report(force: true);
        }

        public void Finish() => Report(force: true);

        private void Report(bool force)
        {
            if (_progress is null)
                return;

            var now = Stopwatch.GetTimestamp();
            if (!force && now - _lastReport < Stopwatch.Frequency / 10)
                return;

            _lastReport = now;
            _progress.Report(new ArchiveProgress
            {
                Phase = _phase,
                CurrentFile = _currentFile,
                BytesProcessed = _bytes,
                TotalBytes = _totalBytes,
                FilesProcessed = _files,
                TotalFiles = _totalFiles,
                Elapsed = _stopwatch.Elapsed,
            });
        }
    }
}
