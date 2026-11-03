using System.Diagnostics;
using System.Text;

namespace GRAR.Services;

/// <summary>Thrown when an external helper program (7z, rar) fails.</summary>
public sealed class ExternalToolException : Exception
{
    public ExternalToolException(string message, string output = "")
        : base(message)
    {
        Output = output;
    }

    public string Output { get; }
}

/// <summary>
/// Locates optional external programs used for formats that cannot be written natively:
/// 7-Zip (7z/7za/7zz) and the proprietary RAR engine.
/// </summary>
public static class ExternalTools
{
    private static readonly string[] SevenZipNames = { "7z", "7za", "7zz", "7z.exe", "7za.exe", "7zz.exe" };
    private static readonly string[] RarNames = { "rar", "rar.exe", "Rar.exe" };

    private static readonly object Gate = new();
    private static bool _searched;
    private static string? _sevenZip;
    private static string? _rar;

    public static string? FindSevenZip()
    {
        EnsureSearched();
        return _sevenZip;
    }

    public static string? FindRar()
    {
        EnsureSearched();
        return _rar;
    }

    public static bool SevenZipAvailable => FindSevenZip() is not null;
    public static bool RarAvailable => FindRar() is not null;

    public static string AvailabilityNote(Models.ArchiveFormat format) => format switch
    {
        Models.ArchiveFormat.SevenZip => SevenZipAvailable
            ? string.Empty
            : "7-Zip was not found on this system — install 7z/7za to create .7z archives.",
        Models.ArchiveFormat.Rar => RarAvailable
            ? string.Empty
            : "The RAR engine was not found on this system — install rar to create .rar archives.",
        _ => string.Empty,
    };

    private static void EnsureSearched()
    {
        lock (Gate)
        {
            if (_searched)
                return;

            _searched = true;
            _sevenZip = Search(SevenZipNames);
            _rar = Search(RarNames);
        }
    }

    private static string? Search(string[] names)
    {
        foreach (var candidate in CandidateDirectories())
        {
            foreach (var name in names)
            {
                try
                {
                    var path = Path.Combine(candidate, name);
                    if (File.Exists(path))
                        return path;
                }
                catch
                {
                    // ignore malformed entries
                }
            }
        }

        // Fall back to a plain PATH lookup.
        foreach (var name in names)
        {
            var path = Which(name);
            if (path is not null)
                return path;
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var dirs = new List<string>();

        void Add(string? dir)
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !dirs.Contains(dir))
                dirs.Add(dir);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                Add(entry.Trim());
        }

        if (OperatingSystem.IsWindows())
        {
            Add(Environment.GetEnvironmentVariable("ProgramFiles"));
            Add(Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
            Add(Environment.GetEnvironmentVariable("ProgramW6432"));
            Add(Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty, "7-Zip"));
            Add(Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty, "WinRAR"));
            Add(Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty, "WinRAR"));
        }

        return dirs;
    }

    private static string? Which(string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(name);

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);

            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return process.ExitCode == 0 && !string.IsNullOrEmpty(firstLine) && File.Exists(firstLine) ? firstLine : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Runs a helper program, returning its standard output. Non zero exits throw.</summary>
    public static string Run(string executable, IEnumerable<string> arguments, CancellationToken ct = default)
        => Run(executable, arguments, workingDirectory: null, ct);

    /// <summary>Runs a helper program from a specific working directory.</summary>
    public static string Run(string executable, IEnumerable<string> arguments, string? workingDirectory, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new ExternalToolException($"Could not start \"{Path.GetFileName(executable)}\".");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            if (!process.WaitForExit(30 * 60 * 1000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw new ExternalToolException($"{Path.GetFileName(executable)} did not finish in time.");
            }
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        var output = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // rar returns 1 for "warnings", which still means the archive was written.
        if (process.ExitCode != 0 && process.ExitCode != 1)
            throw new ExternalToolException(
                $"{Path.GetFileName(executable)} exited with code {process.ExitCode}.",
                output);

        return output;
    }
}
