using System.Diagnostics;
using GRAR.Helpers;
using GRAR.Models;

namespace GRAR.Services;

/// <summary>Disk browsing helpers (listing, creating, renaming, deleting, launching).</summary>
public static class FileSystemService
{
    public static IReadOnlyList<string> GetDrives()
    {
        var drives = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady)
                    drives.Add(drive.RootDirectory.FullName);
            }
        }
        catch
        {
            // ignore
        }

        if (drives.Count == 0)
            drives.Add(Path.DirectorySeparatorChar.ToString());

        return drives;
    }

    public static string GetDriveDisplayName(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            var label = drive.VolumeLabel;
            if (OperatingSystem.IsWindows())
            {
                return string.IsNullOrWhiteSpace(label)
                    ? $"{drive.Name} ({drive.DriveFormat})"
                    : $"{label} ({drive.Name})";
            }

            return string.IsNullOrWhiteSpace(label) ? $"{root} ({drive.DriveFormat})" : $"{label} ({root})";
        }
        catch
        {
            return root;
        }
    }

    public static IReadOnlyList<(string Name, string Path)> GetQuickAccessFolders()
    {
        var result = new List<(string, string)>();
        void Add(string name, Environment.SpecialFolder folder)
        {
            try
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    result.Add((name, path));
            }
            catch
            {
                // ignore
            }
        }

        Add("Home", Environment.SpecialFolder.UserProfile);
        Add("Desktop", Environment.SpecialFolder.DesktopDirectory);
        Add("Documents", Environment.SpecialFolder.MyDocuments);
        Add("Downloads", Environment.SpecialFolder.UserProfile);
        Add("Music", Environment.SpecialFolder.MyMusic);
        Add("Pictures", Environment.SpecialFolder.MyPictures);
        Add("Videos", Environment.SpecialFolder.MyVideos);

        // Downloads is not a well-known folder on every platform.
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads) && !result.Any(r => string.Equals(r.Item2, downloads, StringComparison.OrdinalIgnoreCase)))
        {
            var index = result.FindIndex(r => r.Item1 == "Documents");
            result.Insert(index + 1, ("Downloads", downloads));
        }

        return result;
    }

    /// <summary>Lists the immediate children of a disk folder as rows.</summary>
    public static IReadOnlyList<FileItem> ListFolder(string path, bool showHidden)
    {
        var items = new List<FileItem>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false,
        };

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path, "*", options))
                items.Add(FileItem.FromDisk(dir));

            foreach (var file in Directory.EnumerateFiles(path, "*", options))
                items.Add(FileItem.FromDisk(file));
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (DirectoryNotFoundException)
        {
            throw;
        }

        if (!showHidden)
            items.RemoveAll(i => !i.IsDirectory && i.Name.StartsWith('.'));
        else
            items.RemoveAll(i => i.Name is "." or "..");

        return items;
    }

    public static IReadOnlyList<FolderNode> ListFolderNodes(string path, bool showHidden)
    {
        var nodes = new List<FolderNode>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false,
        };

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path, "*", options))
            {
                var name = Path.GetFileName(dir);
                if (!showHidden && name.StartsWith('.'))
                    continue;

                nodes.Add(new FolderNode(name, NodeKind.DiskFolder, dir, diskPath: dir));
            }
        }
        catch
        {
            // Inaccessible folders simply contribute no children.
        }

        nodes.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        return nodes;
    }

    public static IReadOnlyList<string> EnumerateFilesRecursive(string root, bool includeSubfolders, bool showHidden)
    {
        var files = new List<string>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = includeSubfolders,
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };

        try
        {
            files.AddRange(Directory.EnumerateFiles(root, "*", options));
        }
        catch
        {
            // ignore
        }

        return files;
    }

    public static IReadOnlyList<string> EnumerateDirectoriesRecursive(string root)
    {
        var directories = new List<string>();
        try
        {
            directories.AddRange(Directory.EnumerateDirectories(root, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
            }));
        }
        catch
        {
            // ignore
        }

        return directories;
    }

    public static string CreateFolder(string parent, string name)
    {
        var safe = PathUtil.SanitizeFileName(name, "New folder");
        var path = Path.Combine(parent, safe);
        var suffix = 2;
        while (Directory.Exists(path) || File.Exists(path))
        {
            path = Path.Combine(parent, $"{safe} ({suffix++})");
        }

        Directory.CreateDirectory(path);
        return path;
    }

    public static string Rename(string path, string newName)
    {
        var safe = PathUtil.SanitizeFileName(newName, Path.GetFileName(path));
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
            throw new InvalidOperationException("Cannot determine the parent folder.");

        var target = Path.Combine(parent, safe);
        if (string.Equals(path, target, StringComparison.Ordinal))
            return path;

        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"An item named \"{safe}\" already exists.");

        if (Directory.Exists(path))
            Directory.Move(path, target);
        else
            File.Move(path, target);

        return target;
    }

    /// <summary>Deletes items, collecting failures instead of aborting the batch.</summary>
    public static IReadOnlyList<string> Delete(IReadOnlyList<string> paths)
    {
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return errors;
    }

    public static void OpenWithDefaultApplication(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    public static void OpenFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo { FileName = "open", Arguments = $"\"{path}\"", UseShellExecute = true });
            return;
        }

        var linuxPath = path;
        Process.Start(new ProcessStartInfo
        {
            FileName = "xdg-open",
            Arguments = $"\"{linuxPath}\"",
            UseShellExecute = true,
        });
    }

    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;

            if (Directory.Exists(path))
                return Directory.EnumerateFiles(path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                }).Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            // ignore
        }

        return 0;
    }
}
