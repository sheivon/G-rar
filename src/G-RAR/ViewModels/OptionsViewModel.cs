using GRAR.Helpers;
using GRAR.Models;

namespace GRAR.ViewModels;

/// <summary>State for the options window; values are copied in and applied back on OK.</summary>
public sealed class OptionsViewModel : ObservableObject
{
    private static readonly ArchiveFormat[] Formats =
        { ArchiveFormat.Zip, ArchiveFormat.SevenZip, ArchiveFormat.Tar, ArchiveFormat.TarGz, ArchiveFormat.Rar };

    private static readonly string[] FormatNames = { "ZIP", "7Z", "TAR", "TAR.GZ", "RAR" };

    private bool _showHiddenFiles;
    private bool _confirmDelete;
    private bool _confirmExit;
    private bool _restoreLastFolder;
    private int _compressionLevel;
    private int _formatIndex;

    public OptionsViewModel(AppSettings settings)
    {
        _showHiddenFiles = settings.ShowHiddenFiles;
        _confirmDelete = settings.ConfirmDelete;
        _confirmExit = settings.ConfirmExit;
        _restoreLastFolder = settings.RestoreLastFolder;
        _compressionLevel = Math.Clamp(settings.CompressionLevel, 0, 9);

        var detected = Array.IndexOf(Formats, ArchiveFormats.FromExtension("." + settings.DefaultFormat.TrimStart('.')));
        _formatIndex = detected >= 0 ? detected : 0;
    }

    public bool ShowHiddenFiles
    {
        get => _showHiddenFiles;
        set => SetProperty(ref _showHiddenFiles, value);
    }

    public bool ConfirmDelete
    {
        get => _confirmDelete;
        set => SetProperty(ref _confirmDelete, value);
    }

    public bool ConfirmExit
    {
        get => _confirmExit;
        set => SetProperty(ref _confirmExit, value);
    }

    public bool RestoreLastFolder
    {
        get => _restoreLastFolder;
        set => SetProperty(ref _restoreLastFolder, value);
    }

    public int CompressionLevel
    {
        get => _compressionLevel;
        set
        {
            if (SetProperty(ref _compressionLevel, Math.Clamp(value, 0, 9)))
                OnPropertyChanged(nameof(CompressionText));
        }
    }

    public string CompressionText => CompressionLevel switch
    {
        0 => "0 — stored",
        <= 3 => $"{CompressionLevel} — fast",
        <= 6 => $"{CompressionLevel} — normal",
        <= 8 => $"{CompressionLevel} — good",
        _ => "9 — best",
    };

    public string[] FormatNamesList => FormatNames;

    public int FormatIndex
    {
        get => _formatIndex;
        set => SetProperty(ref _formatIndex, value);
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.ShowHiddenFiles = ShowHiddenFiles;
        settings.ConfirmDelete = ConfirmDelete;
        settings.ConfirmExit = ConfirmExit;
        settings.RestoreLastFolder = RestoreLastFolder;
        settings.CompressionLevel = CompressionLevel;
        settings.DefaultFormat = ArchiveFormats.Extension(Formats[Math.Clamp(FormatIndex, 0, Formats.Length - 1)]).TrimStart('.');
    }
}
