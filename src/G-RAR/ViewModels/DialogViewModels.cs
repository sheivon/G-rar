using GRAR.Helpers;
using GRAR.Models;

namespace GRAR.ViewModels;

/// <summary>State for the "enter password" window.</summary>
public sealed class PasswordDialogViewModel : ObservableObject
{
    private string _message = "This archive is password protected.";
    private string _password = string.Empty;
    private string _passwordAgain = string.Empty;
    private bool _requireConfirmation;

    public string Title { get; set; } = "Enter password";

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
                OnPropertyChanged(nameof(ConfirmEnabled));
        }
    }

    public string PasswordAgain
    {
        get => _passwordAgain;
        set
        {
            if (SetProperty(ref _passwordAgain, value))
                OnPropertyChanged(nameof(ConfirmEnabled));
        }
    }

    /// <summary>True when the password has to be typed twice (used when creating archives).</summary>
    public bool RequireConfirmation
    {
        get => _requireConfirmation;
        set
        {
            if (SetProperty(ref _requireConfirmation, value))
                OnPropertyChanged(nameof(ShowConfirmation));
        }
    }

    public bool ShowConfirmation => RequireConfirmation;

    public bool ConfirmEnabled
        => Password.Length > 0 && (!RequireConfirmation || Password == PasswordAgain);
}

/// <summary>State for the generic single line input window (rename, new folder, ...).</summary>
public sealed class InputDialogViewModel : ObservableObject
{
    private string _title = "Input";
    private string _label = "Name:";
    private string _value = string.Empty;
    private string _error = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
                OnPropertyChanged(nameof(ConfirmEnabled));
        }
    }

    public string Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool ConfirmEnabled => Value.Trim().Length > 0;

    public bool HasError => Error.Length > 0;
}

/// <summary>Buttons offered by the message window.</summary>
public enum MessageButtons
{
    Ok,
    YesNo,
    YesNoCancel,
}

public enum MessageIcon
{
    Info,
    Warning,
    Error,
    Question,
}

/// <summary>State for the message / confirmation window.</summary>
public sealed class MessageDialogViewModel
{
    public string Title { get; init; } = "G-RAR";
    public string Message { get; init; } = string.Empty;
    public MessageButtons Buttons { get; init; } = MessageButtons.Ok;
    public MessageIcon Icon { get; init; } = MessageIcon.Info;

    public string IconKey => Icon switch
    {
        MessageIcon.Warning => "icon.warning",
        MessageIcon.Error => "icon.error",
        MessageIcon.Question => "icon.info",
        _ => "icon.info",
    };

    public Avalonia.Media.IImage? IconImage => Helpers.Icons.Get(IconKey);

    public bool ShowYes => Buttons is MessageButtons.YesNo or MessageButtons.YesNoCancel;
    public bool ShowNo => Buttons is MessageButtons.YesNo or MessageButtons.YesNoCancel;
    public bool ShowCancel => Buttons is MessageButtons.YesNoCancel;
    public bool ShowOk => Buttons is MessageButtons.Ok;

    public bool Result { get; set; } = true;
}

/// <summary>State for the "Add to archive" window.</summary>
public sealed class AddArchiveViewModel : ObservableObject
{
    private static readonly string[] FormatNames = { "ZIP", "7Z", "TAR", "TAR.GZ", "RAR" };
    private static readonly ArchiveFormat[] Formats =
        { ArchiveFormat.Zip, ArchiveFormat.SevenZip, ArchiveFormat.Tar, ArchiveFormat.TarGz, ArchiveFormat.Rar };

    private static readonly string[] UpdateModeNames =
        { "Add and replace files", "Add new files only", "Refresh existing files" };

    private string _archivePath;
    private int _formatIndex;
    private int _compressionLevel;
    private string _password = string.Empty;
    private string _passwordAgain = string.Empty;
    private bool _requirePassword;
    private bool _includeSubfolders;
    private bool _addEmptyFolders;
    private bool _deleteAfterArchiving;
    private bool _testAfterCreation;
    private int _updateModeIndex;
    private string _validationError = string.Empty;

    public AddArchiveViewModel(string defaultDirectory, string suggestedName, IEnumerable<string> sources, AppSettings settings)
    {
        Sources = sources.ToList();
        _archivePath = Path.Combine(defaultDirectory, suggestedName);
        if (Path.GetExtension(_archivePath).Length == 0)
            _archivePath += ".zip";

        _compressionLevel = Math.Clamp(settings.CompressionLevel, 0, 9);
        _includeSubfolders = settings.IncludeSubfolders;
        _addEmptyFolders = settings.AddEmptyFolders;
        _deleteAfterArchiving = settings.DeleteAfterArchiving;
        _testAfterCreation = settings.TestAfterCreation;
        _formatIndex = Math.Max(0, Array.IndexOf(Formats, ArchiveFormats.FromExtension(Path.GetExtension(_archivePath))));

        if (Sources.Count == 1)
            SourcesSummary = Sources[0];
        else
            SourcesSummary = $"{Sources.Count} items selected";
    }

    public IReadOnlyList<string> Sources { get; }

    public string SourcesSummary { get; }

    /// <summary>Invoked by the view to open a "save file" picker.</summary>
    public Func<Task<string?>>? BrowseSave { get; set; }

    public string ArchivePath
    {
        get => _archivePath;
        set
        {
            if (!SetProperty(ref _archivePath, value))
                return;

            var detected = Array.IndexOf(Formats, ArchiveFormats.FromExtension(Path.GetExtension(value)));
            if (detected >= 0 && detected != _formatIndex)
                FormatIndex = detected;

            OnPropertyChanged(nameof(SuggestedName));
        }
    }

    public string SuggestedName => Path.GetFileName(_archivePath);

    public string[] FormatNamesList => FormatNames;

    public int FormatIndex
    {
        get => _formatIndex;
        set
        {
            if (!SetProperty(ref _formatIndex, value))
                return;

            // Keep the extension consistent with the selected format.
            var extension = ArchiveFormats.Extension(Formats[value]);
            var path = _archivePath;
            if (path.Length > 0)
            {
                var directory = Path.GetDirectoryName(path) ?? string.Empty;
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.Length == 0)
                    name = "archive";

                ArchivePath = Path.Combine(directory, name + extension);
            }

            ValidationError = string.Empty;
            OnPropertyChanged(nameof(FormatWarning));
            OnPropertyChanged(nameof(HasFormatWarning));
            OnPropertyChanged(nameof(PasswordEnabled));
        }
    }

    public ArchiveFormat Format => Formats[Math.Clamp(FormatIndex, 0, Formats.Length - 1)];

    public string FormatWarning => ExternalToolAvailability(Format);

    public bool HasFormatWarning => FormatWarning.Length > 0;

    public bool PasswordEnabled => Format is not (ArchiveFormat.Tar or ArchiveFormat.TarGz);

    public int CompressionLevel
    {
        get => _compressionLevel;
        set => SetProperty(ref _compressionLevel, Math.Clamp(value, 0, 9));
    }

    public string CompressionText => CompressionLevel switch
    {
        0 => "0 — stored",
        <= 3 => $"{CompressionLevel} — fast",
        <= 6 => $"{CompressionLevel} — normal",
        <= 8 => $"{CompressionLevel} — good",
        _ => "9 — best",
    };

    public bool RequirePassword
    {
        get => _requirePassword;
        set
        {
            if (SetProperty(ref _requirePassword, value))
            {
                OnPropertyChanged(nameof(ShowPasswordFields));
                OnPropertyChanged(nameof(ConfirmEnabled));
            }
        }
    }

    public bool ShowPasswordFields => RequirePassword && PasswordEnabled;

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
                OnPropertyChanged(nameof(ConfirmEnabled));
        }
    }

    public string PasswordAgain
    {
        get => _passwordAgain;
        set
        {
            if (SetProperty(ref _passwordAgain, value))
                OnPropertyChanged(nameof(ConfirmEnabled));
        }
    }

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set => SetProperty(ref _includeSubfolders, value);
    }

    public bool AddEmptyFolders
    {
        get => _addEmptyFolders;
        set => SetProperty(ref _addEmptyFolders, value);
    }

    public bool DeleteAfterArchiving
    {
        get => _deleteAfterArchiving;
        set => SetProperty(ref _deleteAfterArchiving, value);
    }

    public bool TestAfterCreation
    {
        get => _testAfterCreation;
        set => SetProperty(ref _testAfterCreation, value);
    }

    public string[] UpdateModeNamesList => UpdateModeNames;

    public int UpdateModeIndex
    {
        get => _updateModeIndex;
        set => SetProperty(ref _updateModeIndex, value);
    }

    public UpdateMode UpdateMode => UpdateModeIndex switch
    {
        1 => UpdateMode.AddNewOnly,
        2 => UpdateMode.RefreshExisting,
        _ => UpdateMode.AddAndReplace,
    };

    public bool ConfirmEnabled
        => ArchivePath.Trim().Length > 0
           && (!RequirePassword || !PasswordEnabled || (Password.Length > 0 && Password == PasswordAgain));

    public string? Validate()
    {
        if (ArchivePath.Trim().Length == 0)
            return "Please choose a name for the archive.";

        if (RequirePassword && PasswordEnabled && Password != PasswordAgain)
            return "The two passwords do not match.";

        var directory = Path.GetDirectoryName(PathUtilExpand(ArchivePath));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            return $"The folder \"{directory}\" does not exist.";

        return null;
    }

    public string ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
                OnPropertyChanged(nameof(HasValidationError));
        }
    }

    public bool HasValidationError => ValidationError.Length > 0;

    /// <summary>Shows a validation problem inside the dialog instead of a message box.</summary>
    public void ShowValidationError(string message) => ValidationError = message;

    public AddArchiveOptions BuildOptions(string? password)
        => new(
            PathUtilExpand(ArchivePath),
            Format,
            CompressionLevel,
            password,
            IncludeSubfolders,
            AddEmptyFolders,
            DeleteAfterArchiving,
            TestAfterCreation,
            UpdateMode,
            Sources);

    private static string PathUtilExpand(string path)
        => GRAR.Helpers.PathUtil.ExpandPath(path);

    private static string ExternalToolAvailability(ArchiveFormat format)
        => GRAR.Services.ExternalTools.AvailabilityNote(format);
}

/// <summary>State for the "Extract" window.</summary>
public sealed class ExtractViewModel : ObservableObject
{
    private static readonly string[] OverwriteNames = { "Ask before overwriting", "Overwrite existing files", "Skip existing files" };

    private string _destination;
    private bool _useFolderNames;
    private int _overwriteIndex;
    private bool _showExtracted;
    private bool _openDestination;
    private string _password = string.Empty;
    private bool _askPassword;
    private string _validationError = string.Empty;

    public ExtractViewModel(AppSettings settings, string suggestedFolder, int fileCount, long totalSize, bool needsPassword)
    {
        _destination = suggestedFolder;
        _useFolderNames = settings.ExtractUseFolderNames;
        _overwriteIndex = Math.Clamp(settings.ExtractOverwriteMode, 0, 2);
        _showExtracted = settings.ExtractShowFiles;
        _openDestination = settings.ExtractOpenFolder;
        _askPassword = needsPassword;
        FileCount = fileCount;
        TotalSize = totalSize;
        NeedsPassword = needsPassword;
    }

    public int FileCount { get; }
    public long TotalSize { get; }
    public bool NeedsPassword { get; }

    public string Summary
        => $"{Fmt.Plural(FileCount, "file", "files")} · {Helpers.Fmt.Size(TotalSize)}";

    /// <summary>Invoked by the view to open a folder picker.</summary>
    public Func<Task<string?>>? BrowseFolder { get; set; }

    public string Destination
    {
        get => _destination;
        set
        {
            if (SetProperty(ref _destination, value))
                ValidationError = string.Empty;
        }
    }

    public string ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
                OnPropertyChanged(nameof(HasValidationError));
        }
    }

    public bool HasValidationError => ValidationError.Length > 0;

    /// <summary>Shows a validation problem inside the dialog instead of a message box.</summary>
    public void ShowValidationError(string message) => ValidationError = message;

    public bool UseFolderNames
    {
        get => _useFolderNames;
        set => SetProperty(ref _useFolderNames, value);
    }

    public string[] OverwriteNamesList => OverwriteNames;

    public int OverwriteIndex
    {
        get => _overwriteIndex;
        set => SetProperty(ref _overwriteIndex, value);
    }

    public OverwriteMode Overwrite => OverwriteIndex switch
    {
        1 => OverwriteMode.Overwrite,
        2 => OverwriteMode.Skip,
        _ => OverwriteMode.Ask,
    };

    public bool ShowExtracted
    {
        get => _showExtracted;
        set => SetProperty(ref _showExtracted, value);
    }

    public bool OpenDestination
    {
        get => _openDestination;
        set => SetProperty(ref _openDestination, value);
    }

    public bool AskPassword
    {
        get => _askPassword;
        set
        {
            if (SetProperty(ref _askPassword, value))
                OnPropertyChanged(nameof(ShowPassword));
        }
    }

    public bool ShowPassword => NeedsPassword;

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public bool ConfirmEnabled => Destination.Trim().Length > 0;

    public string? Validate()
    {
        if (Destination.Trim().Length == 0)
            return "Please choose a destination folder.";
        return null;
    }

    public ExtractOptions BuildOptions(string? password)
        => new(
            Helpers.PathUtil.ExpandPath(Destination),
            UseFolderNames,
            Overwrite,
            ShowExtracted,
            OpenDestination,
            password);
}

/// <summary>State for the progress window shown while an operation runs.</summary>
public sealed class ProgressDialogViewModel : ObservableObject
{
    private string _phase = "Working…";
    private string _currentFile = string.Empty;
    private double _percent;
    private bool _indeterminate;
    private string _detail = string.Empty;
    private bool _cancellable = true;
    private bool _finished;

    public ProgressDialogViewModel(string title, Action cancel)
    {
        Title = title;
        CancelCommand = new RelayCommand(cancel);
    }

    public string Title { get; }

    public RelayCommand CancelCommand { get; }

    public string Phase
    {
        get => _phase;
        set => SetProperty(ref _phase, value);
    }

    public string CurrentFile
    {
        get => _currentFile;
        set => SetProperty(ref _currentFile, value);
    }

    public double Percent
    {
        get => _percent;
        set => SetProperty(ref _percent, value);
    }

    public bool Indeterminate
    {
        get => _indeterminate;
        set
        {
            if (SetProperty(ref _indeterminate, value))
                OnPropertyChanged(nameof(IsDeterminate));
        }
    }

    public bool IsDeterminate => !Indeterminate;

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public bool Cancellable
    {
        get => _cancellable;
        set
        {
            if (SetProperty(ref _cancellable, value))
                OnPropertyChanged(nameof(ShowCancel));
        }
    }

    public bool Finished
    {
        get => _finished;
        set
        {
            if (SetProperty(ref _finished, value))
                OnPropertyChanged(nameof(ShowCancel));
        }
    }

    public bool ShowCancel => Cancellable && !Finished;

    public void Report(ArchiveProgress progress)
    {
        Phase = string.IsNullOrEmpty(progress.Phase) ? Phase : progress.Phase;
        CurrentFile = progress.CurrentFile;
        Indeterminate = progress.Indeterminate;
        Percent = Math.Max(0, progress.Percent);

        var parts = new List<string>();
        if (progress.TotalFiles > 0)
            parts.Add($"{progress.FilesProcessed}/{progress.TotalFiles} files");
        if (progress.TotalBytes > 0)
            parts.Add($"{Helpers.Fmt.Size(progress.BytesProcessed)} of {Helpers.Fmt.Size(progress.TotalBytes)}");
        if (progress.Indeterminate)
            parts.Add(progress.CurrentFile);

        Detail = string.Join("  ·  ", parts);
    }
}
