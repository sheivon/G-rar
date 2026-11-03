using System.Collections.ObjectModel;
using Avalonia.Media;
using GRAR.Helpers;

namespace GRAR.Models;

public enum NodeKind
{
    QuickAccess,
    ThisPc,
    Drive,
    DiskFolder,
    Favorite,
    ArchiveRoot,
    ArchiveFolder,
    Placeholder,
}

/// <summary>A node in the left hand navigation tree.</summary>
public sealed class FolderNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _childrenLoaded;
    private Task? _loadTask;

    public FolderNode(
        string name,
        NodeKind kind,
        string key,
        string? diskPath = null,
        string? archivePath = null,
        string internalPath = "",
        bool hasChildren = true)
    {
        Name = name;
        Kind = kind;
        Key = key;
        DiskPath = diskPath;
        ArchivePath = archivePath;
        InternalPath = internalPath;
        HasChildren = hasChildren;

        if (hasChildren)
        {
            // A placeholder keeps the expander visible until the real children are loaded.
            Children.Add(CreatePlaceholder());
        }
    }

    public string Name { get; }
    public NodeKind Kind { get; }
    public string Key { get; }

    /// <summary>Disk folder this node points at (null for archive nodes).</summary>
    public string? DiskPath { get; }

    /// <summary>Archive that owns this node (null for disk nodes).</summary>
    public string? ArchivePath { get; }

    /// <summary>Folder path inside the archive ("" = archive root).</summary>
    public string InternalPath { get; }

    public bool HasChildren { get; }

    public bool IsArchiveNode => Kind is NodeKind.ArchiveRoot or NodeKind.ArchiveFolder;

    public ObservableCollection<FolderNode> Children { get; } = new();

    /// <summary>Async loader invoked the first time the node is expanded.</summary>
    public Func<FolderNode, Task<IReadOnlyList<FolderNode>>>? Loader { get; set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value) || !value)
                return;

            _ = LoadChildrenAsync();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public IImage? Icon => Icons.Get(IconKey);

    public string IconKey => Kind switch
    {
        NodeKind.QuickAccess => "icon.home",
        NodeKind.ThisPc => "icon.pc",
        NodeKind.Drive => "icon.drive",
        NodeKind.Favorite => "icon.favorite",
        NodeKind.ArchiveRoot or NodeKind.ArchiveFolder => "icon.archive",
        _ => "icon.folder",
    };

    public static FolderNode CreatePlaceholder()
        => new("…", NodeKind.Placeholder, "placeholder", hasChildren: false);

    /// <summary>Expands the node and every ancestor that has already been loaded.</summary>
    public void ExpandSelf()
    {
        IsExpanded = true;
        IsSelected = true;
    }

    public FolderNode? FindChild(Func<FolderNode, bool> predicate)
        => Children.FirstOrDefault(c => c.Kind != NodeKind.Placeholder && predicate(c));

    public FolderNode FindOrAddChild(FolderNode child)
    {
        var existing = Children.FirstOrDefault(c => c.Kind != NodeKind.Placeholder && c.Key == child.Key);
        if (existing is not null)
            return existing;

        // Replace the placeholder once real children arrive.
        if (Children.Count == 1 && Children[0].Kind == NodeKind.Placeholder)
            Children.Clear();

        Children.Add(child);
        return child;
    }

    public void SetChildren(IReadOnlyList<FolderNode> children)
    {
        Children.Clear();
        foreach (var child in children)
            Children.Add(child);
    }

    private async Task LoadChildrenAsync()
    {
        if (_childrenLoaded || Loader is null)
            return;

        _childrenLoaded = true;
        try
        {
            var children = await Loader(this).ConfigureAwait(true);
            if (children.Count == 0 && Children.Count == 1 && Children[0].Kind == NodeKind.Placeholder)
                Children.Clear();
            else
                SetChildren(children);
        }
        catch
        {
            // A folder that cannot be read simply shows no children.
            if (Children.Count == 1 && Children[0].Kind == NodeKind.Placeholder)
                Children.Clear();
        }
    }

    /// <summary>Loads the children if they have not been loaded yet and completes when they are available.</summary>
    public Task EnsureLoadedAsync()
    {
        if (_childrenLoaded || Loader is null)
            return Task.CompletedTask;

        return _loadTask ??= LoadChildrenAsync();
    }

    public override string ToString() => $"{Kind}:{Name}";
}
