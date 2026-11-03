using System.Text;

namespace GRAR.Helpers;

/// <summary>Path helpers that work for both disk paths and virtual archive paths.</summary>
public static class PathUtil
{
    public static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary>Normalises an archive entry key to '/' separated form without a leading slash.</summary>
    public static string NormalizeKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        var sb = new StringBuilder(key.Length);
        foreach (var ch in key.Replace('\\', '/'))
        {
            if (ch == '/')
            {
                if (sb.Length == 0 || sb[^1] == '/')
                    continue;
            }
            sb.Append(ch);
        }

        var result = sb.ToString().Trim('/');
        return result;
    }

    public static string CombineKey(string folder, string name)
    {
        folder = NormalizeKey(folder);
        name = NormalizeKey(name);
        if (folder.Length == 0) return name;
        if (name.Length == 0) return folder;
        return folder + "/" + name;
    }

    /// <summary>
    /// Makes an archive key safe to use as a relative disk path (guards against zip-slip).
    /// Returns null when the key cannot be represented safely.
    /// </summary>
    public static string? SanitizeRelativePath(string key)
    {
        key = NormalizeKey(key);
        if (key.Length == 0)
            return null;

        var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part is "." or "..")
                continue;

            var cleaned = string.Concat(part.Select(c => InvalidFileNameChars.Contains(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c));
            if (cleaned.Length == 0)
                continue;

            // Windows reserved device names.
            var stem = cleaned.Split('.')[0];
            if (stem.Length > 0 && IsReservedName(stem))
                cleaned = "_" + cleaned;

            safe.Add(cleaned);
        }

        if (safe.Count == 0)
            return null;

        return Path.Combine(safe.ToArray());
    }

    private static bool IsReservedName(string stem)
    {
        var upper = stem.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL"
            || (upper.Length == 4 && upper[0] == 'C' && upper[1] == 'O' && upper[2] == 'M' && upper[3] >= '1' && upper[3] <= '9')
            || (upper.Length == 4 && upper[0] == 'L' && upper[1] == 'P' && upper[2] == 'T' && upper[3] >= '1' && upper[3] <= '9');
    }

    public static string SanitizeFileName(string name, string fallback = "new")
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(InvalidFileNameChars.Contains(ch) || ch is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : ch);

        var result = sb.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? fallback : result;
    }

    public static string GetFileName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }

    public static string GetParentKey(string key)
    {
        key = NormalizeKey(key);
        var index = key.LastIndexOf('/');
        return index < 0 ? string.Empty : key[..index];
    }

    /// <summary>True when the path points at a folder that exists on disk.</summary>
    public static bool IsExistingDirectory(string path)
        => path.Length > 0 && Directory.Exists(path);

    public static string CombineDisk(string folder, string name)
        => Path.Combine(folder, name);

    /// <summary>Expands environment variables and '~' in a user supplied path.</summary>
    public static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        path = Environment.ExpandEnvironmentVariables(path.Trim());

        if (path.StartsWith("~/") || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = home + path[1..];
        }

        return path;
    }
}
