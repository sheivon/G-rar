using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GRAR.Helpers;
using GRAR.Models;
using GRAR.Services;
using GRAR.Views;

namespace GRAR.ViewModels;

/// <summary>
/// State and behaviour of the main window: navigation between disk folders and
/// archives, the file list, the folder tree and every command the UI exposes.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    private sealed record Location(string DiskPath, string? ArchivePath, string InternalPath);

    private sealed record OpOutcome(bool Success, bool Cancelled, bool PasswordRequired, ArchiveConflictException? Conflict)
    {
        public static OpOutcome Ok => new(true, false, false, null);
        public static OpOutcome WasCancelled => new(false, true, false, null);
        public static OpOutcome NeedPassword => new(false, false, true, null);
        public static OpOutcome ConflictWith(ArchiveConflictException ex) => new(false, false, false, ex);
    }

    private readonly SettingsService _settings;
    private readonly Dictionary<FolderNode, FolderNode?> _parents = new();
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Location> _back = new();
    private readonly List<Location> _forward = new();
    private readonly Dictionary<string, List<ArchiveEntryInfo>> _archiveRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FolderNode> _archiveNodes = new(StringComparer.Ordinal);
    private readonly ObservableCollection<FileItem> _items = new();
    private readonly DataGridCollectionView _itemsView;
    private readonly List<RelayCommand> _relayCommands = new();
    private readonly List<AsyncRelayCommand> _asyncCommands = new();

    private Window? _host;
    private CancellationTokenSource? _operationCts;
    private Location _current = new(string.Empty, null, string.Empty);
    private ArchiveListing? _listing;
    private string _archivePath = string.Empty;
    private FolderNode? _archiveRoot;
    private string _addressText = string.Empty;
    private string _windowTitle = "G-RAR";
    private string _statusText = "Ready";
    private string _selectionText = string.Empty;
    private string _archiveText = string.Empty;
    private FileItem? _selectedItem;
    private IReadOnlyList<FileItem> _selectedItems = Array.Empty<FileItem>();
    private bool _isBusy;
    private bool _showHidden;
    private bool _showTree = true;
    private bool _showStatus = true;
    private string _sortKey = "Name";
    private string _sortProperty = "Name";
    private bool _sortDescending;
    private double _treeWidth;
    private FolderNode? _selectedTreeNode;

    public MainWindowViewModel(SettingsService settings)
    {
        _settings = settings;
        _showHidden = settings.Settings.ShowHiddenFiles;
        _treeWidth = Math.Clamp(settings.Settings.TreeWidth, 140, 720);
        _itemsView = new DataGridCollectionView(_items);

        BuildTree();
        CreateCommands();
        ApplySortDescriptions();
    }

    // ============================================================== bindings

    public ObservableCollection<FolderNode> TreeRoots { get; } = new();

    public DataGridCollectionView ItemsView => _itemsView;

    public IReadOnlyList<FileItem> Items => _items;

    /// <summary>Raised after the rows were replaced; the view re-applies the selection.</summary>
    public event EventHandler? ItemsChanged;

    /// <summary>Paths that should be selected again after <see cref="ItemsChanged"/>.</summary>
    public IReadOnlyCollection<string> SelectionToRestore { get; private set; } = Array.Empty<string>();

    public string AddressText
    {
        get => _addressText;
        set => SetProperty(ref _addressText, value);
    }

    public string WindowTitle
    {
        get => _windowTitle;
        private set => SetProperty(ref _windowTitle, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }

    public string ArchiveText
    {
        get => _archiveText;
        private set => SetProperty(ref _archiveText, value);
    }

    public FileItem? SelectedItem
    {
        get => _selectedItem;
        private set => SetProperty(ref _selectedItem, value);
    }

    public IReadOnlyList<FileItem> SelectedItems => _selectedItems;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RaiseCommandStates();
        }
    }

    public bool IsArchiveMode => _archivePath.Length > 0;

    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (!SetProperty(ref _showHidden, value))
                return;

            _settings.Settings.ShowHiddenFiles = value;
            _settings.Touch();
            _ = RefreshAsync();
        }
    }

    public bool ShowTree
    {
        get => _showTree;
        set => SetProperty(ref _showTree, value);
    }

    public bool ShowStatus
    {
        get => _showStatus;
        set => SetProperty(ref _showStatus, value);
    }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => _current.DiskPath.Length > 0
                           || (_current.ArchivePath is not null && _current.InternalPath.Length > 0);

    public bool CanCloseArchive => IsArchiveMode;

    public FolderNode? SelectedTreeNode
    {
        get => _selectedTreeNode;
        private set => SetProperty(ref _selectedTreeNode, value);
    }

    public double TreeWidth
    {
        get => _treeWidth;
        set
        {
            var clamped = Math.Clamp(value, 140, 720);
            if (SetProperty(ref _treeWidth, clamped))
            {
                _settings.Settings.TreeWidth = clamped;
                _settings.Touch();
            }
        }
    }

    // =============================================================== commands

    public ICommand GoBackCommand { get; private set; } = null!;
    public ICommand GoForwardCommand { get; private set; } = null!;
    public ICommand GoUpCommand { get; private set; } = null!;
    public ICommand GoCommand { get; private set; } = null!;
    public ICommand OpenArchiveCommand { get; private set; } = null!;
    public ICommand RefreshCommand { get; private set; } = null!;
    public ICommand OpenCommand { get; private set; } = null!;
    public ICommand ExtractCommand { get; private set; } = null!;
    public ICommand AddCommand { get; private set; } = null!;
    public ICommand TestCommand { get; private set; } = null!;
    public ICommand DeleteCommand { get; private set; } = null!;
    public ICommand RenameCommand { get; private set; } = null!;
    public ICommand NewFolderCommand { get; private set; } = null!;
    public ICommand SelectAllCommand { get; private set; } = null!;
    public ICommand InvertSelectionCommand { get; private set; } = null!;
    public ICommand CopyPathCommand { get; private set; } = null!;
    public ICommand CloseArchiveCommand { get; private set; } = null!;
    public ICommand OpenContainingFolderCommand { get; private set; } = null!;
    public ICommand ToggleHiddenCommand { get; private set; } = null!;
    public ICommand ToggleTreeCommand { get; private set; } = null!;
    public ICommand ToggleStatusCommand { get; private set; } = null!;
    public ICommand OptionsCommand { get; private set; } = null!;
    public ICommand AboutCommand { get; private set; } = null!;
    public ICommand CancelOperationCommand { get; private set; } = null!;

    /// <summary>Raised when the file list should select everything (handled by the view).</summary>
    public event EventHandler? SelectAllRequested;

    /// <summary>Raised when the file list should invert its selection (handled by the view).</summary>
    public event EventHandler? InvertSelectionRequested;

    private void CreateCommands()
    {
        GoBackCommand = Register(new AsyncRelayCommand(GoBackAsync, () => CanGoBack && !IsBusy));
        GoForwardCommand = Register(new AsyncRelayCommand(GoForwardAsync, () => CanGoForward && !IsBusy));
        GoUpCommand = Register(new AsyncRelayCommand(GoUpAsync, () => CanGoUp && !IsBusy));
        GoCommand = Register(new AsyncRelayCommand(GoToAddressAsync, () => !IsBusy));
        OpenArchiveCommand = Register(new AsyncRelayCommand(OpenArchivePickerAsync, () => !IsBusy));
        RefreshCommand = Register(new AsyncRelayCommand(RefreshAsync, () => !IsBusy));
        OpenCommand = Register(new AsyncRelayCommand(OpenSelectedAsync, () => !IsBusy && _selectedItems.Count > 0));
        ExtractCommand = Register(new AsyncRelayCommand(ExtractAsync, () => !IsBusy && IsArchiveMode));
        AddCommand = Register(new AsyncRelayCommand(AddAsync, () => !IsBusy));
        TestCommand = Register(new AsyncRelayCommand(TestAsync, () => !IsBusy && (IsArchiveMode || _selectedItems.Any(i => i.IsArchive))));
        DeleteCommand = Register(new AsyncRelayCommand(DeleteAsync, () => !IsBusy && _selectedItems.Count > 0));
        RenameCommand = Register(new AsyncRelayCommand(RenameAsync, () => !IsBusy && _selectedItems.Count == 1 && !_selectedItems[0].FromArchive));
        NewFolderCommand = Register(new AsyncRelayCommand(NewFolderAsync, () => !IsBusy && !IsArchiveMode));
        SelectAllCommand = Register(new RelayCommand(() => SelectAllRequested?.Invoke(this, EventArgs.Empty)));
        InvertSelectionCommand = Register(new RelayCommand(() => InvertSelectionRequested?.Invoke(this, EventArgs.Empty)));
        CopyPathCommand = Register(new AsyncRelayCommand(CopyPathAsync));
        CloseArchiveCommand = Register(new AsyncRelayCommand(CloseArchiveAsync, () => CanCloseArchive && !IsBusy));
        OpenContainingFolderCommand = Register(new RelayCommand(OpenContainingFolder, () => _selectedItems.Count > 0 || !IsArchiveMode));
        ToggleHiddenCommand = Register(new RelayCommand(() => ShowHidden = !ShowHidden));
        ToggleTreeCommand = Register(new RelayCommand(() => ShowTree = !ShowTree));
        ToggleStatusCommand = Register(new RelayCommand(() => ShowStatus = !ShowStatus));
        OptionsCommand = Register(new AsyncRelayCommand(ShowOptionsAsync, () => !IsBusy));
        AboutCommand = Register(new AsyncRelayCommand(ShowAboutAsync));
        CancelOperationCommand = Register(new RelayCommand(() => _operationCts?.Cancel(), () => IsBusy));
    }

    private ICommand Register(RelayCommand command)
    {
        _relayCommands.Add(command);
        return command;
    }

    private ICommand Register(AsyncRelayCommand command)
    {
        _asyncCommands.Add(command);
        return command;
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanCloseArchive));
        OnPropertyChanged(nameof(IsArchiveMode));

        foreach (var command in _relayCommands)
            command.RaiseCanExecuteChanged();
        foreach (var command in _asyncCommands)
            command.RaiseCanExecuteChanged();
    }

    // ============================================================ public API

    public void AttachDialogHost(Window host) => _host = host;

    /// <summary>Opens the folder the application should start in.</summary>
    public async Task InitializeAsync()
    {
        var settings = _settings.Settings;
        var start = settings.RestoreLastFolder && !string.IsNullOrEmpty(settings.LastFolder)
            ? settings.LastFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!Directory.Exists(start))
            start = Path.GetTempPath();

        await NavigateToDiskAsync(start, pushHistory: false);
    }

    /// <summary>Called by the view when the user picks a row in the tree.</summary>
    public async Task OnTreeNodeSelectedAsync(FolderNode node)
    {
        SelectedTreeNode = node;

        switch (node.Kind)
        {
            case NodeKind.DiskFolder:
            case NodeKind.Drive:
            case NodeKind.Favorite:
                if (!string.IsNullOrEmpty(node.DiskPath))
                    await NavigateToDiskAsync(node.DiskPath, pushHistory: true);
                break;

            case NodeKind.ArchiveRoot:
                if (!string.IsNullOrEmpty(node.ArchivePath))
                    await ShowLocationAsync(new Location(_current.DiskPath, node.ArchivePath, string.Empty), pushHistory: true);
                break;

            case NodeKind.ArchiveFolder:
                if (!string.IsNullOrEmpty(node.ArchivePath))
                    await ShowLocationAsync(new Location(_current.DiskPath, node.ArchivePath, node.InternalPath), pushHistory: true);
                break;
        }
    }

    /// <summary>Called by the view whenever the selection of the file list changes.</summary>
    public void OnItemsSelectionChanged(IReadOnlyList<FileItem> selected)
    {
        _selectedItems = selected;
        SelectedItem = selected.FirstOrDefault();
        UpdateStatus();
        RaiseCommandStates();
    }

    /// <summary>Sorts the file list; invoked from the column header click.</summary>
    public void SortBy(string sortKey, string propertyPath, bool descending)
    {
        _sortKey = sortKey;
        _sortProperty = propertyPath;
        _sortDescending = descending;
        ApplySortDescriptions();
    }

    public string SortKey => _sortKey;
    public bool SortDescending => _sortDescending;

    /// <summary>Opens a double clicked (or otherwise activated) row.</summary>
    public async Task OpenItemAsync(FileItem item)
    {
        if (IsBusy)
            return;

        if (item.IsDirectory)
        {
            if (item.FromArchive)
                await ShowLocationAsync(new Location(_current.DiskPath, _archivePath, item.EntryKey ?? string.Empty), pushHistory: true);
            else
                await NavigateToDiskAsync(item.FullPath, pushHistory: true);
            return;
        }

        if (FileTypes.IsArchiveExtension(item.Extension) && !item.FromArchive)
        {
            await OpenArchiveAsync(item.FullPath);
            return;
        }

        if (!item.FromArchive)
        {
            FileSystemService.OpenWithDefaultApplication(item.FullPath);
            return;
        }

        var password = _passwords.GetValueOrDefault(_archivePath);
        if (item.IsEncrypted && string.IsNullOrEmpty(password))
        {
            password = await PromptPasswordAsync($"\"{item.Name}\" is password protected. Enter the password to open it.");
            if (password is null)
            {
                StatusText = "Ready";
                return;
            }

            _passwords[_archivePath] = password;
        }

        string path;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                StatusText = $"Extracting \"{item.Name}\" …";
                path = await Task.Run(() => ArchiveService.ExtractToTemp(_archivePath, item.EntryKey!, password));
                break;
            }
            catch (ArchivePasswordRequiredException)
            {
                if (attempt >= 2)
                {
                    await ShowMessageAsync("Open", "The password is incorrect or the file cannot be decrypted.", MessageIcon.Warning);
                    StatusText = "Ready";
                    return;
                }

                var prompted = await PromptPasswordAsync($"\"{item.Name}\" is password protected. Enter the password to open it.");
                if (prompted is null)
                {
                    StatusText = "Ready";
                    return;
                }

                password = prompted;
                _passwords[_archivePath] = prompted;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Open", ex);
                return;
            }
        }

        try
        {
            if (FileTypes.IsArchiveExtension(item.Extension))
                await OpenArchiveAsync(path, pushHistory: true, containerPath: Path.GetDirectoryName(path) ?? string.Empty);
            else
                FileSystemService.OpenWithDefaultApplication(path);
            StatusText = "Ready";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Open", ex);
        }
    }

    // ============================================================= navigation

    private async Task NavigateToDiskAsync(string path, bool pushHistory)
    {
        path = PathUtil.ExpandPath(path);
        if (!Directory.Exists(path))
        {
            await ShowMessageAsync("G-RAR", $"The folder \"{path}\" does not exist.", MessageIcon.Warning);
            return;
        }

        await ShowLocationAsync(new Location(path, null, string.Empty), pushHistory);
    }

    private async Task ShowLocationAsync(Location location, bool pushHistory)
    {
        if (pushHistory && !SameLocation(location, _current))
        {
            _back.Add(_current);
            if (_back.Count > 64)
                _back.RemoveAt(0);
            _forward.Clear();
        }

        if (location.ArchivePath is null)
        {
            if (IsArchiveMode)
                DetachArchive();

            _current = location;
            ShowDiskFolder(location.DiskPath);
        }
        else
        {
            if (!string.Equals(_archivePath, location.ArchivePath, StringComparison.OrdinalIgnoreCase))
            {
                var opened = await OpenArchiveCoreAsync(location.ArchivePath);
                if (!opened)
                {
                    // The archive could not be opened: stay on the folder that contains it.
                    location = new Location(location.DiskPath, null, string.Empty);
                    _current = location;
                    ShowDiskFolder(location.DiskPath);
                    AfterLocationChanged();
                    return;
                }
            }

            _current = location;
            ShowArchiveFolder(location.InternalPath);
        }

        AfterLocationChanged();
    }

    private static bool SameLocation(Location a, Location b)
        => string.Equals(a.DiskPath, b.DiskPath, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.ArchivePath, b.ArchivePath, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.InternalPath, b.InternalPath, StringComparison.Ordinal);

    private void AfterLocationChanged()
    {
        AddressText = DisplayPath(_current);
        WindowTitle = IsArchiveMode
            ? $"{Path.GetFileName(_archivePath)} — G-RAR"
            : $"{_current.DiskPath} — G-RAR";

        _settings.Settings.LastFolder = _current.DiskPath;
        _settings.Touch();

        UpdateStatus();
        UpdateTreeSelection();
        RaiseCommandStates();
    }

    private static string DisplayPath(Location location)
    {
        if (location.ArchivePath is null)
            return location.DiskPath;

        var internalPath = location.InternalPath.Replace('/', Path.DirectorySeparatorChar);
        return internalPath.Length == 0
            ? location.ArchivePath
            : location.ArchivePath + Path.DirectorySeparatorChar + internalPath;
    }

    private async Task GoBackAsync()
    {
        if (_back.Count == 0)
            return;

        var location = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(_current);
        await ShowLocationAsync(location, pushHistory: false);
    }

    private async Task GoForwardAsync()
    {
        if (_forward.Count == 0)
            return;

        var location = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(_current);
        await ShowLocationAsync(location, pushHistory: false);
    }

    private async Task GoUpAsync()
    {
        var location = _current;
        if (location.ArchivePath is not null)
        {
            if (location.InternalPath.Length > 0)
            {
                await ShowLocationAsync(
                    location with { InternalPath = PathUtil.GetParentKey(location.InternalPath) },
                    pushHistory: true);
                return;
            }

            await ShowLocationAsync(new Location(location.DiskPath, null, string.Empty), pushHistory: true);
            return;
        }

        var parent = Directory.GetParent(location.DiskPath)?.FullName;
        if (!string.IsNullOrEmpty(parent))
            await NavigateToDiskAsync(parent, pushHistory: true);
    }

    private async Task GoToAddressAsync()
    {
        var text = PathUtil.ExpandPath(AddressText.Trim().Trim('"'));
        if (text.Length == 0)
            return;

        // An address inside the archive that is currently open.
        if (IsArchiveMode)
        {
            var archivePrefix = _archivePath + Path.DirectorySeparatorChar;
            var normalized = text.Replace('\\', '/');
            var archiveNormalized = _archivePath.Replace('\\', '/');
            if (normalized.StartsWith(archiveNormalized + "/", StringComparison.OrdinalIgnoreCase))
            {
                var internalPath = PathUtil.NormalizeKey(normalized[(archiveNormalized.Length + 1)..]);
                await ShowLocationAsync(new Location(_current.DiskPath, _archivePath, internalPath), pushHistory: true);
                return;
            }
        }

        if (Directory.Exists(text))
        {
            await NavigateToDiskAsync(text, pushHistory: true);
            return;
        }

        if (File.Exists(text) && ArchiveFormats.IsArchiveFile(text))
        {
            await OpenArchiveAsync(text);
            return;
        }

        await ShowMessageAsync("G-RAR", $"\"{text}\" was not found.", MessageIcon.Warning);
        AddressText = DisplayPath(_current);
    }

    private async Task RefreshAsync()
    {
        if (IsArchiveMode)
        {
            var internalPath = _current.InternalPath;
            var archivePath = _archivePath;
            if (!await ReloadArchiveAsync())
                return;

            _current = new Location(_current.DiskPath, archivePath, internalPath);
            ShowArchiveFolder(internalPath);
            AfterLocationChanged();
            return;
        }

        if (string.IsNullOrEmpty(_current.DiskPath))
            return;

        ShowDiskFolder(_current.DiskPath);
        AfterLocationChanged();
        StatusText = "Refreshed.";
    }

    // ============================================================== archives

    private async Task OpenArchiveAsync(string path, bool pushHistory = true, string? containerPath = null)
    {
        path = PathUtil.ExpandPath(path);
        if (!File.Exists(path))
        {
            await ShowMessageAsync("G-RAR", $"\"{path}\" does not exist.", MessageIcon.Warning);
            return;
        }

        var container = containerPath ?? Path.GetDirectoryName(path) ?? string.Empty;
        if (!Directory.Exists(container))
            container = _current.DiskPath;

        if (await OpenArchiveCoreAsync(path))
            await ShowLocationAsync(new Location(container, path, string.Empty), pushHistory);
    }

    /// <summary>Loads the listing and prepares the tree/rows; returns false when the user cancelled.</summary>
    private async Task<bool> OpenArchiveCoreAsync(string path)
    {
        string? password = _passwords.GetValueOrDefault(path);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                StatusText = $"Opening \"{Path.GetFileName(path)}\" …";
                var listing = await ArchiveService.ReadListingAsync(path, password);

                DetachArchive();
                _listing = listing;
                _archivePath = path;
                BuildArchiveState(listing, path);
                _settings.AddRecentArchive(path);
                StatusText = "Ready";
                return true;
            }
            catch (ArchivePasswordRequiredException)
            {
                var prompted = await PromptPasswordAsync(
                    $"\"{Path.GetFileName(path)}\" is password protected. Enter the password to open it.",
                    attempt == 0 ? null : "The previous password was not accepted.");
                if (prompted is null)
                {
                    StatusText = "Ready";
                    return false;
                }

                password = prompted;
                _passwords[path] = prompted;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Open archive", ex);
                StatusText = "Ready";
                return false;
            }
        }

        StatusText = "Ready";
        return false;
    }

    private async Task<bool> ReloadArchiveAsync()
    {
        var path = _archivePath;
        if (path.Length == 0)
            return false;

        return await OpenArchiveCoreAsync(path);
    }

    private void BuildArchiveState(ArchiveListing listing, string path)
    {
        _archiveRows.Clear();
        _archiveNodes.Clear();

        foreach (var entry in listing.Entries)
        {
            var key = PathUtil.NormalizeKey(entry.Key);
            if (key.Length == 0)
                continue;

            AddRow(PathUtil.GetParentKey(key), entry);

            var segments = key.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var accumulated = string.Empty;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var parent = accumulated;
                accumulated = accumulated.Length == 0 ? segments[i] : accumulated + "/" + segments[i];
                EnsureFolderRow(parent, accumulated, segments[i], entry.Modified);
            }
        }

        var root = new FolderNode(
            Path.GetFileName(path),
            NodeKind.ArchiveRoot,
            "archive:" + path,
            archivePath: path,
            internalPath: string.Empty,
            hasChildren: true);

        _parents[root] = null;
        _archiveNodes[string.Empty] = root;

        var children = BuildArchiveChildNodes(path, string.Empty, root);
        if (children.Count == 0)
            root.SetChildren(Array.Empty<FolderNode>());
        else
            root.SetChildren(children);

        _archiveRoot = root;
        TreeRoots.Insert(0, root);
    }

    private List<FolderNode> BuildArchiveChildNodes(string archivePath, string parentPath, FolderNode? parent)
    {
        var result = new List<FolderNode>();
        if (!_archiveRows.TryGetValue(parentPath, out var rows))
            return result;

        foreach (var row in rows.Where(r => r.IsDirectory))
        {
            var children = BuildArchiveChildNodes(archivePath, row.Key, null);
            var node = new FolderNode(
                row.Name,
                NodeKind.ArchiveFolder,
                "archive-folder:" + archivePath + "/" + row.Key,
                archivePath: archivePath,
                internalPath: row.Key,
                hasChildren: children.Count > 0);

            _parents[node] = parent;
            _archiveNodes[row.Key] = node;

            foreach (var child in children)
                _parents[child] = node;

            if (children.Count > 0)
                node.SetChildren(children);

            result.Add(node);
        }

        return result;
    }

    private void AddRow(string parentKey, ArchiveEntryInfo entry)
    {
        if (!_archiveRows.TryGetValue(parentKey, out var list))
            _archiveRows[parentKey] = list = new List<ArchiveEntryInfo>();

        list.Add(entry);
    }

    private void EnsureFolderRow(string parentKey, string key, string name, DateTime? modified)
    {
        if (!_archiveRows.TryGetValue(parentKey, out var list))
            _archiveRows[parentKey] = list = new List<ArchiveEntryInfo>();

        if (list.Any(r => string.Equals(r.Key, key, StringComparison.Ordinal)))
            return;

        list.Add(new ArchiveEntryInfo(key, name, true, 0, 0, modified, 0, string.Empty, null, false));
    }

    private void ShowArchiveFolder(string internalPath)
    {
        if (_listing is null)
            return;

        var rows = _archiveRows.TryGetValue(internalPath, out var list) ? list : new List<ArchiveEntryInfo>();
        var items = rows.Select(r => FileItem.FromArchiveEntry(r, _archivePath)).ToList();

        SetItems(items);
        ArchiveText = BuildArchiveText();
    }

    private string BuildArchiveText()
    {
        if (_listing is null)
            return string.Empty;

        var files = _listing.Entries.Count(e => !e.IsDirectory);
        var parts = new List<string>
        {
            _listing.Format,
            Fmt.Plural(files, "file", "files"),
            $"{Fmt.Size(_listing.TotalSize)} → {Fmt.Size(_listing.TotalPacked)} ({Fmt.Ratio(_listing.TotalSize, _listing.TotalPacked)})",
        };

        if (_listing.IsSolid)
            parts.Add("solid");
        if (_listing.HasEncryptedEntries)
            parts.Add("encrypted");
        if (!_listing.IsComplete)
            parts.Add("damaged");

        return string.Join("  ·  ", parts);
    }

    private void DetachArchive()
    {
        if (_archiveRoot is not null)
        {
            foreach (var key in _parents.Keys.Where(k => k.IsArchiveNode).ToList())
                _parents.Remove(key);

            TreeRoots.Remove(_archiveRoot);
            _archiveRoot = null;
        }

        _listing = null;
        _archivePath = string.Empty;
        _archiveRows.Clear();
        _archiveNodes.Clear();
        ArchiveText = string.Empty;
    }

    private async Task CloseArchiveAsync()
    {
        if (!IsArchiveMode)
            return;

        var container = _current.DiskPath;
        await ShowLocationAsync(new Location(container, null, string.Empty), pushHistory: true);
    }

    // ============================================================== listings

    private void ShowDiskFolder(string path)
    {
        List<FileItem> items;
        try
        {
            items = FileSystemService.ListFolder(path, ShowHidden).ToList();
        }
        catch (Exception ex)
        {
            items = new List<FileItem>();
            StatusText = $"Cannot open \"{path}\": {ex.Message}";
        }

        SetItems(items);
        ArchiveText = string.Empty;
    }

    private void SetItems(IReadOnlyList<FileItem> items)
    {
        var restore = _selectedItems.Select(i => i.FullPath).ToHashSet(StringComparer.Ordinal);

        _items.Clear();
        foreach (var item in items)
            _items.Add(item);

        SelectionToRestore = restore;
        _selectedItems = Array.Empty<FileItem>();
        SelectedItem = null;

        ItemsChanged?.Invoke(this, EventArgs.Empty);
        UpdateStatus();
        RaiseCommandStates();
    }

    private void UpdateStatus()
    {
        StatusText = IsArchiveMode
            ? $"{Fmt.Plural(_items.Count, "item", "items")} in \"{Path.GetFileName(_archivePath)}\""
            : Fmt.Plural(_items.Count, "item", "items");

        if (_selectedItems.Count == 0)
        {
            SelectionText = string.Empty;
            return;
        }

        var size = _selectedItems.Where(i => !i.IsDirectory).Sum(i => i.Size);
        SelectionText = _selectedItems.Count == 1 && _selectedItems[0] is { IsDirectory: false } single
            ? $"{single.Name}  ·  {Fmt.Size(size)}"
            : $"{Fmt.Plural(_selectedItems.Count, "item", "items")} selected  ·  {Fmt.Size(size)}";
    }

    // ================================================================ tree

    private void BuildTree()
    {
        TreeRoots.Clear();
        _parents.Clear();

        var quickAccessChildren = new List<FolderNode>();
        var quickAccess = new FolderNode("Quick access", NodeKind.QuickAccess, "quick", hasChildren: false);
        foreach (var (name, path) in FileSystemService.GetQuickAccessFolders())
        {
            if (Directory.Exists(path))
                quickAccessChildren.Add(MakeDiskNode(name, path, quickAccess));
        }

        foreach (var child in quickAccessChildren)
            quickAccess.FindOrAddChild(child);
        TreeRoots.Add(quickAccess);

        var thisPcChildren = new List<FolderNode>();
        var thisPc = new FolderNode("This PC", NodeKind.ThisPc, "thispc", hasChildren: false);
        foreach (var root in FileSystemService.GetDrives())
            thisPcChildren.Add(MakeDiskNode(FileSystemService.GetDriveDisplayName(root), root, thisPc, NodeKind.Drive));

        foreach (var child in thisPcChildren)
            thisPc.FindOrAddChild(child);
        TreeRoots.Add(thisPc);

        var favoriteChildren = new List<FolderNode>();
        var favorites = new FolderNode("Favorites", NodeKind.Favorite, "favorites", hasChildren: false);
        foreach (var favorite in _settings.Settings.Favorites)
        {
            if (Directory.Exists(favorite.Path))
                favoriteChildren.Add(MakeDiskNode(favorite.Name, favorite.Path, favorites));
        }

        foreach (var child in favoriteChildren)
            favorites.FindOrAddChild(child);
        TreeRoots.Add(favorites);
    }

    private FolderNode MakeDiskNode(string name, string path, FolderNode? parent, NodeKind kind = NodeKind.DiskFolder)
    {
        var node = new FolderNode(name, kind, path, diskPath: path, hasChildren: true)
        {
            Loader = LoadDiskChildren,
        };

        _parents[node] = parent;
        return node;
    }

    private Task<IReadOnlyList<FolderNode>> LoadDiskChildren(FolderNode node)
    {
        IReadOnlyList<FolderNode> children = string.IsNullOrEmpty(node.DiskPath)
            ? Array.Empty<FolderNode>()
            : FileSystemService.ListFolderNodes(node.DiskPath, ShowHidden);

        foreach (var child in children)
        {
            child.Loader = LoadDiskChildren;
            _parents[child] = node;
        }

        return Task.FromResult(children);
    }

    private void UpdateTreeSelection()
    {
        FolderNode? target = null;

        if (IsArchiveMode)
        {
            if (_archiveNodes.TryGetValue(_current.InternalPath, out var archiveNode))
                target = archiveNode;
        }
        else if (_current.DiskPath.Length > 0)
        {
            target = FindDiskNode(_current.DiskPath);
        }

        if (target is not null && !ReferenceEquals(target, _selectedTreeNode))
            SelectedTreeNode = target;
    }

    private FolderNode? FindDiskNode(string path)
    {
        FolderNode? best = null;
        var bestLength = -1;

        void Walk(FolderNode node)
        {
            if (node.DiskPath is { } diskPath && IsSameOrParentOf(diskPath, path) && diskPath.Length > bestLength)
            {
                best = node;
                bestLength = diskPath.Length;
            }

            foreach (var child in node.Children)
            {
                if (child.Kind != NodeKind.Placeholder)
                    Walk(child);
            }
        }

        foreach (var root in TreeRoots)
            Walk(root);

        return best;
    }

    private static bool IsSameOrParentOf(string parent, string child)
    {
        parent = parent.Replace('\\', '/').TrimEnd('/');
        child = child.Replace('\\', '/').TrimEnd('/');

        if (parent.Length == 0)
            return child.Length == 0;

        return child.Equals(parent, StringComparison.OrdinalIgnoreCase)
               || child.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Root → node chain, used by the view to expand the tree before selecting a node.</summary>
    public IReadOnlyList<FolderNode> GetAncestorChain(FolderNode node)
    {
        var chain = new List<FolderNode>();
        var current = node;
        while (current is not null)
        {
            chain.Add(current);
            current = _parents.GetValueOrDefault(current);
        }

        chain.Reverse();
        return chain;
    }

    // ============================================================= operations

    private async Task OpenSelectedAsync()
    {
        if (_selectedItems.Count == 0)
            return;

        await OpenItemAsync(_selectedItems[0]);
    }

    private async Task ExtractAsync()
    {
        if (!IsArchiveMode || _listing is null || _host is null)
            return;

        var selectedEntries = _selectedItems.Where(i => i.FromArchive).Select(i => i.EntryKey!).Distinct().ToList();
        var keys = selectedEntries.Count > 0 ? (IReadOnlyList<string>?)selectedEntries : null;

        var files = keys is null
            ? _listing.Entries.Count(e => !e.IsDirectory)
            : _selectedItems.Count(i => !i.IsDirectory);
        var totalSize = keys is null
            ? _listing.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size)
            : _selectedItems.Where(i => !i.IsDirectory).Sum(i => i.Size);

        var viewModel = new ExtractViewModel(
            _settings.Settings,
            SuggestExtractFolder(),
            files,
            totalSize,
            _listing.HasEncryptedEntries);

        viewModel.BrowseFolder = () => BrowseFolderAsync("Select destination folder", viewModel.Destination);
        var dialog = new ExtractDialog { DataContext = viewModel };

        if (!await dialog.ShowDialog<bool>(_host))
            return;

        var error = viewModel.Validate();
        if (error is not null)
        {
            await ShowMessageAsync("Extract", error, MessageIcon.Warning);
            return;
        }

        _settings.Settings.LastExtractPath = viewModel.Destination;
        _settings.Settings.ExtractUseFolderNames = viewModel.UseFolderNames;
        _settings.Settings.ExtractOverwriteMode = viewModel.OverwriteIndex;
        _settings.Settings.ExtractShowFiles = viewModel.ShowExtracted;
        _settings.Settings.ExtractOpenFolder = viewModel.OpenDestination;
        _settings.Touch();

        var archivePath = _archivePath;
        var password = viewModel.Password;
        if (string.IsNullOrEmpty(password))
            password = _passwords.GetValueOrDefault(archivePath);

        if ((viewModel.NeedsPassword || _listing.HasEncryptedEntries) && string.IsNullOrEmpty(password))
        {
            var prompted = await PromptPasswordAsync(
                $"\"{Path.GetFileName(archivePath)}\" is password protected. Enter the password to extract it.");
            if (prompted is null)
                return;

            password = prompted;
            _passwords[archivePath] = prompted;
        }

        if (!string.IsNullOrEmpty(password))
            _passwords[archivePath] = password;

        var options = viewModel.BuildOptions(string.IsNullOrEmpty(password) ? null : password);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var outcome = await RunOperationAsync(
                "Extracting",
                (progress, ct) => ArchiveService.ExtractAsync(archivePath, keys, options, progress, ct));

            if (outcome.Success)
                break;

            if (outcome.PasswordRequired)
            {
                var prompted = await PromptPasswordAsync(
                    $"\"{Path.GetFileName(archivePath)}\" is password protected. Enter the password to extract it.");
                if (prompted is null)
                    return;

                _passwords[archivePath] = prompted;
                options = options with { Password = prompted };
                continue;
            }

            if (outcome.Conflict is { } conflict)
            {
                var mode = await AskOverwriteAsync(conflict);
                if (mode is null)
                    return;

                options = options with { Overwrite = mode.Value };
                continue;
            }

            return;
        }

        if (viewModel.OpenDestination)
        {
            try { FileSystemService.OpenFolder(viewModel.Destination); }
            catch { /* the folder may not be openable on this platform */ }
        }
    }

    private string SuggestExtractFolder()
    {
        var last = _settings.Settings.LastExtractPath;
        if (!string.IsNullOrEmpty(last))
            return last;

        var name = IsArchiveMode
            ? Path.GetFileNameWithoutExtension(_archivePath)
            : Path.GetFileName(_current.DiskPath.TrimEnd(Path.DirectorySeparatorChar, '/'));

        if (string.IsNullOrEmpty(name))
            name = "Extracted";

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Desktop",
            name);
    }

    private async Task AddAsync()
    {
        if (_host is null)
            return;

        string[] sources;
        string directory;
        string suggestedName;

        if (IsArchiveMode)
        {
            var picked = await PickFilesAsync("Select the files to add");
            if (picked.Count == 0)
                return;

            sources = picked.ToArray();
            directory = Path.GetDirectoryName(_archivePath) ?? string.Empty;
            suggestedName = Path.GetFileName(_archivePath);
        }
        else
        {
            sources = _selectedItems.Count > 0
                ? _selectedItems.Select(i => i.FullPath).ToArray()
                : new[] { _current.DiskPath };

            directory = _settings.Settings.LastAddDirectory;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                directory = _current.DiskPath;

            suggestedName = SuggestArchiveName(sources);
        }

        var viewModel = new AddArchiveViewModel(directory, suggestedName, sources, _settings.Settings);
        viewModel.BrowseSave = () => BrowseSaveAsync("Save archive as", viewModel.ArchivePath, viewModel.Format);
        var dialog = new AddArchiveDialog { DataContext = viewModel };

        if (!await dialog.ShowDialog<bool>(_host))
            return;

        var error = viewModel.Validate();
        if (error is not null)
        {
            await ShowMessageAsync("Add to archive", error, MessageIcon.Warning);
            return;
        }

        var password = viewModel.RequirePassword ? viewModel.Password : null;
        if (!IsArchiveMode)
            password ??= _passwords.GetValueOrDefault(viewModel.ArchivePath);

        if (viewModel.DeleteAfterArchiving
            && sources.Any(s => !string.Equals(s.TrimEnd(Path.DirectorySeparatorChar), _current.DiskPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
        {
            var confirmed = await ConfirmAsync(
                "Add to archive",
                "Delete the original items after they have been added to the archive?",
                MessageButtons.YesNo);

            if (confirmed != true)
                viewModel.DeleteAfterArchiving = false;
        }

        _settings.Settings.LastAddDirectory = Path.GetDirectoryName(viewModel.ArchivePath) ?? directory;
        _settings.Settings.CompressionLevel = viewModel.CompressionLevel;
        _settings.Settings.DefaultFormat = ArchiveFormats.Extension(viewModel.Format).TrimStart('.');
        _settings.Touch();

        var options = viewModel.BuildOptions(password);
        var outcome = await RunOperationAsync(
            "Creating archive",
            (progress, ct) => ArchiveService.CreateAsync(options, progress, ct));

        if (outcome.PasswordRequired)
        {
            await ShowMessageAsync(
                "Add to archive",
                "The archive needs a password before it can be updated.",
                MessageIcon.Warning);
        }

        if (!outcome.Success)
            return;

        if (viewModel.DeleteAfterArchiving)
        {
            foreach (var source in sources)
            {
                try
                {
                    if (File.Exists(source))
                        File.Delete(source);
                    else if (Directory.Exists(source) && !string.Equals(source, _current.DiskPath, StringComparison.OrdinalIgnoreCase))
                        Directory.Delete(source, recursive: true);
                }
                catch
                {
                    // Deleting the originals is best effort.
                }
            }
        }

        StatusText = $"\"{Path.GetFileName(options.ArchivePath)}\" was created.";

        if (IsArchiveMode)
        {
            var internalPath = _current.InternalPath;
            if (await ReloadArchiveAsync())
                ShowArchiveFolder(internalPath);
        }
        else
        {
            ShowDiskFolder(_current.DiskPath);
        }

        if (viewModel.TestAfterCreation)
            await TestArchiveAsync(options.ArchivePath, options.Password);
    }

    private static string SuggestArchiveName(IReadOnlyList<string> sources)
    {
        var first = sources.Count == 1 ? sources[0] : sources.FirstOrDefault() ?? string.Empty;
        var name = Path.GetFileName(first.TrimEnd(Path.DirectorySeparatorChar, '/'));

        if (string.IsNullOrEmpty(name))
            name = Path.GetFileName(Path.GetDirectoryName(first) ?? string.Empty);

        return string.IsNullOrEmpty(name) ? "archive" : name;
    }

    private async Task TestAsync()
    {
        var path = IsArchiveMode
            ? _archivePath
            : _selectedItems.FirstOrDefault(i => i.IsArchive && !i.IsDirectory)?.FullPath;

        if (string.IsNullOrEmpty(path))
            return;

        await TestArchiveAsync(path, _passwords.GetValueOrDefault(path));
    }

    private async Task TestArchiveAsync(string path, string? password)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var outcome = await RunOperationAsync(
                "Testing",
                (progress, ct) => ArchiveService.TestAsync(path, password, progress, ct));

            if (outcome.Success)
            {
                await ShowMessageAsync(
                    "Test",
                    $"\"{Path.GetFileName(path)}\" tested OK.\n\nNo errors were found.",
                    MessageIcon.Info);
                return;
            }

            if (outcome.PasswordRequired)
            {
                var prompted = await PromptPasswordAsync(
                    $"\"{Path.GetFileName(path)}\" is password protected. Enter the password to test it.");
                if (prompted is null)
                    return;

                _passwords[path] = prompted;
                password = prompted;
                continue;
            }

            return;
        }
    }

    private async Task DeleteAsync()
    {
        if (_selectedItems.Count == 0)
            return;

        if (_settings.Settings.ConfirmDelete)
        {
            var message = _selectedItems.Count == 1
                ? $"Delete \"{_selectedItems[0].Name}\"?"
                : $"Delete {_selectedItems.Count} items?";

            var confirmed = await ConfirmAsync("Delete", message, MessageButtons.YesNo);
            if (confirmed != true)
                return;
        }

        if (IsArchiveMode)
        {
            var keys = _selectedItems.Where(i => i.FromArchive).Select(i => i.EntryKey!).Distinct().ToList();
            if (keys.Count == 0)
                return;

            var archivePath = _archivePath;
            var password = _passwords.GetValueOrDefault(archivePath);
            var format = ArchiveFormats.FromExtension(Path.GetExtension(archivePath));

            var outcome = await RunOperationAsync(
                "Deleting",
                (progress, ct) => ArchiveService.EditArchiveAsync(
                    archivePath, format, keys, null, password, UpdateMode.AddAndReplace, 5, progress, ct));

            if (!outcome.Success)
                return;

            var internalPath = _current.InternalPath;
            if (await ReloadArchiveAsync())
                ShowArchiveFolder(internalPath);

            StatusText = $"{Fmt.Plural(keys.Count, "entry", "entries")} removed.";
            return;
        }

        var paths = _selectedItems.Select(i => i.FullPath).ToList();
        var errors = await Task.Run(() => FileSystemService.Delete(paths));

        ShowDiskFolder(_current.DiskPath);
        UpdateStatus();

        if (errors.Count > 0)
            await ShowMessageAsync("Delete", string.Join("\n", errors), MessageIcon.Warning);
        else
            StatusText = $"{Fmt.Plural(paths.Count, "item", "items")} deleted.";
    }

    private async Task RenameAsync()
    {
        if (_selectedItems.Count != 1)
            return;

        var item = _selectedItems[0];
        if (item.FromArchive)
        {
            await ShowMessageAsync(
                "Rename",
                "Entries inside an archive cannot be renamed directly.\nExtract the entry, rename it and add it back to the archive.",
                MessageIcon.Info);
            return;
        }

        var name = await InputAsync("Rename", "New name:", item.Name);
        if (name is null || name.Trim().Length == 0 || name == item.Name)
            return;

        try
        {
            var newPath = await Task.Run(() => FileSystemService.Rename(item.FullPath, name));
            ShowDiskFolder(_current.DiskPath);
            StatusText = $"Renamed to \"{Path.GetFileName(newPath)}\".";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Rename", ex);
        }
    }

    private async Task NewFolderAsync()
    {
        if (IsArchiveMode || string.IsNullOrEmpty(_current.DiskPath))
            return;

        var name = await InputAsync("New folder", "Folder name:", "New folder");
        if (name is null || name.Trim().Length == 0)
            return;

        try
        {
            var path = await Task.Run(() => FileSystemService.CreateFolder(_current.DiskPath, name));
            ShowDiskFolder(_current.DiskPath);
            StatusText = $"\"{Path.GetFileName(path)}\" was created.";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("New folder", ex);
        }
    }

    private async Task CopyPathAsync()
    {
        if (_host?.Clipboard is null)
            return;

        var text = _selectedItems.Count > 0
            ? string.Join("\n", _selectedItems.Select(i => i.FullName))
            : AddressText;

        await _host.Clipboard.SetTextAsync(text);
        StatusText = "Copied to the clipboard.";
    }

    private void OpenContainingFolder()
    {
        try
        {
            if (_selectedItems.Count > 0)
            {
                var item = _selectedItems[0];
                var path = item.FromArchive
                    ? _archivePath
                    : item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath) ?? item.FullPath;

                FileSystemService.OpenFolder(path);
                return;
            }

            if (!IsArchiveMode && Directory.Exists(_current.DiskPath))
                FileSystemService.OpenFolder(_current.DiskPath);
        }
        catch (Exception ex)
        {
            _ = ShowErrorAsync("Open folder", ex);
        }
    }

    // ======================================================= operation runner

    private async Task<OpOutcome> RunOperationAsync(string title, Func<IProgress<ArchiveProgress>, CancellationToken, Task> work)
    {
        if (_host is null)
            return OpOutcome.Ok;

        var cts = new CancellationTokenSource();
        var viewModel = new ProgressDialogViewModel(title, cts.Cancel);
        var dialog = new ProgressDialog { DataContext = viewModel };
        var progress = new Progress<ArchiveProgress>(viewModel.Report);

        _operationCts = cts;
        IsBusy = true;

        dialog.Show(_host);
        try
        {
            await work(progress, cts.Token);
            viewModel.Finished = true;
            SafeClose(dialog);
            return OpOutcome.Ok;
        }
        catch (OperationCanceledException)
        {
            viewModel.Finished = true;
            SafeClose(dialog);
            StatusText = "Operation cancelled.";
            return OpOutcome.WasCancelled;
        }
        catch (ArchivePasswordRequiredException)
        {
            viewModel.Finished = true;
            SafeClose(dialog);
            return OpOutcome.NeedPassword;
        }
        catch (ArchiveConflictException ex)
        {
            viewModel.Finished = true;
            SafeClose(dialog);
            return OpOutcome.ConflictWith(ex);
        }
        catch (Exception ex)
        {
            viewModel.Finished = true;
            SafeClose(dialog);
            await ShowErrorAsync(title, ex);
            return OpOutcome.WasCancelled;
        }
        finally
        {
            _operationCts = null;
            cts.Dispose();
            IsBusy = false;
        }
    }

    private static void SafeClose(Window window)
    {
        try
        {
            if (window.IsVisible)
                window.Close();
        }
        catch
        {
            // Closing twice must never break an operation.
        }
    }

    private void CancelOperation() => _operationCts?.Cancel();

    // ================================================================ dialogs

    private async Task<string?> PromptPasswordAsync(string message, string? detail = null, bool confirmAgain = false)
    {
        if (_host is null)
            return null;

        var text = string.IsNullOrEmpty(detail) ? message : $"{message}\n\n{detail}";
        var viewModel = new PasswordDialogViewModel
        {
            Title = confirmAgain ? "Set password" : "Enter password",
            Message = text,
            RequireConfirmation = confirmAgain,
        };

        var dialog = new PasswordDialog { DataContext = viewModel };
        return await dialog.ShowDialog<bool>(_host) ? viewModel.Password : null;
    }

    private async Task<bool?> ConfirmAsync(string title, string message, MessageButtons buttons)
    {
        if (_host is null)
            return null;

        var viewModel = new MessageDialogViewModel { Title = title, Message = message, Buttons = buttons, Icon = MessageIcon.Question };
        var dialog = new MessageDialog { DataContext = viewModel };
        return await dialog.ShowDialog<bool?>(_host);
    }

    private async Task ShowMessageAsync(string title, string message, MessageIcon icon)
    {
        if (_host is null)
            return;

        var viewModel = new MessageDialogViewModel { Title = title, Message = message, Buttons = MessageButtons.Ok, Icon = icon };
        var dialog = new MessageDialog { DataContext = viewModel };
        await dialog.ShowDialog<bool?>(_host);
    }

    private async Task ShowErrorAsync(string title, Exception exception)
    {
        var message = exception switch
        {
            ArchiveCorruptedException => exception.Message,
            ArchiveUnsupportedException => exception.Message,
            ExternalToolException ex when ex.Output.Length > 0 => $"{exception.Message}\n\n{Truncate(ex.Output, 600)}",
            UnauthorizedAccessException => $"Access denied.\n\n{exception.Message}",
            _ => exception.Message,
        };

        await ShowMessageAsync(title, message, MessageIcon.Error);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + " …";

    private async Task<string?> InputAsync(string title, string label, string initial)
    {
        if (_host is null)
            return null;

        var viewModel = new InputDialogViewModel { Title = title, Label = label, Value = initial };
        var dialog = new InputDialog { DataContext = viewModel };
        return await dialog.ShowDialog<bool>(_host) ? viewModel.Value.Trim() : null;
    }

    private async Task<OverwriteMode?> AskOverwriteAsync(ArchiveConflictException conflict)
    {
        var message = $"{conflict.Message}\n\nOverwrite the existing files?";
        var buttons = MessageButtons.YesNoCancel;
        var viewModel = new MessageDialogViewModel
        {
            Title = "Confirm file replace",
            Message = message,
            Buttons = buttons,
            Icon = MessageIcon.Warning,
        };

        var dialog = new MessageDialog { DataContext = viewModel };
        var result = await dialog.ShowDialog<bool?>(_host!);
        return result switch
        {
            true => OverwriteMode.Overwrite,
            false => OverwriteMode.Skip,
            _ => null,
        };
    }

    private async Task ShowAboutAsync()
    {
        var version = typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var sevenZip = ExternalTools.SevenZipAvailable ? "found" : "not installed";
        var rar = ExternalTools.RarAvailable ? "found" : "not installed";

        await ShowMessageAsync(
            "About G-RAR",
            $"G-RAR {version}\n\n" +
            "A cross platform archive manager with a WinRAR style interface.\n" +
            "ZIP and TAR are written in process; 7z and RAR use the tools installed on this system.\n\n" +
            $"7-Zip: {sevenZip}\nRAR engine: {rar}\n" +
            $"Settings: {_settings.FilePath}",
            MessageIcon.Info);
    }

    private async Task ShowOptionsAsync()
    {
        if (_host is null)
            return;

        var viewModel = new OptionsViewModel(_settings.Settings);
        var dialog = new OptionsDialog { DataContext = viewModel };

        if (!await dialog.ShowDialog<bool>(_host))
            return;

        viewModel.ApplyTo(_settings.Settings);
        _settings.Touch();

        if (viewModel.ShowHiddenFiles != ShowHidden)
        {
            ShowHidden = viewModel.ShowHiddenFiles;
        }
        else
        {
            await RefreshAsync();
        }
    }

    // ================================================================ pickers

    private static readonly FilePickerFileType ArchiveFileTypes = new("Archives")
    {
        Patterns = new[]
        {
            "*.zip", "*.rar", "*.7z", "*.tar", "*.gz", "*.tgz", "*.bz2", "*.xz", "*.zst", "*.cab",
        },
    };

    private async Task OpenArchivePickerAsync()
    {
        if (_host?.StorageProvider is not { CanOpen: true } provider)
            return;

        try
        {
            var result = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open archive",
                AllowMultiple = false,
                FileTypeFilter = new[] { ArchiveFileTypes, FilePickerFileTypes.All },
            });

            var path = result.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                await OpenArchiveAsync(path);
        }
        catch
        {
            // A cancelled picker is not an error.
        }
    }

    private async Task<string?> BrowseFolderAsync(string title, string? initial)
    {
        if (_host?.StorageProvider is not { CanPickFolder: true } provider)
            return null;

        try
        {
            var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
            if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
                options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(initial);

            var result = await provider.OpenFolderPickerAsync(options);
            return result.Count > 0 ? result[0].TryGetLocalPath() : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> BrowseSaveAsync(string title, string initial, ArchiveFormat format)
    {
        if (_host?.StorageProvider is not { CanSave: true } provider)
            return null;

        try
        {
            var extension = ArchiveFormats.Extension(format);
            var options = new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = Path.GetFileNameWithoutExtension(initial),
                DefaultExtension = extension.TrimStart('.'),
                FileTypeChoices = new[]
                {
                    new FilePickerFileType($"{ArchiveFormats.DisplayName(format)} archive")
                    {
                        Patterns = new[] { "*" + extension },
                    },
                    FilePickerFileTypes.All,
                },
            };

            var directory = Path.GetDirectoryName(initial);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(directory);

            var file = await provider.SaveFilePickerAsync(options);
            var path = file?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path) && Path.GetExtension(path).Length == 0)
                path += extension;

            return path;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        if (_host?.StorageProvider is not { CanOpen: true } provider)
            return Array.Empty<string>();

        try
        {
            var options = new FilePickerOpenOptions { Title = title, AllowMultiple = true };
            if (Directory.Exists(_settings.Settings.LastAddDirectory))
                options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(_settings.Settings.LastAddDirectory);

            var result = await provider.OpenFilePickerAsync(options);
            return result.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ================================================================== sort

    private void ApplySortDescriptions()
    {
        var direction = _sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;

        using (_itemsView.DeferRefresh())
        {
            _itemsView.SortDescriptions.Clear();
            _itemsView.SortDescriptions.Add(
                DataGridSortDescription.FromComparer(new FileItemComparer(_sortKey, _sortDescending), ListSortDirection.Ascending));
            _itemsView.SortDescriptions.Add(DataGridSortDescription.FromPath(_sortProperty, direction));
        }
    }
}
