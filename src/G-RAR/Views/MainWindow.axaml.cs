using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GRAR.Helpers;
using GRAR.Models;
using GRAR.Services;
using GRAR.ViewModels;

namespace GRAR.Views;

public partial class MainWindow : Window
{
    private bool _started;
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();

        var settings = SettingsService.Instance.Settings;
        if (settings.WindowWidth >= 400)
            Width = settings.WindowWidth;
        if (settings.WindowHeight >= 300)
            Height = settings.WindowHeight;

        if (!double.IsNaN(settings.WindowX) && !double.IsNaN(settings.WindowY))
            Position = new PixelPoint((int)settings.WindowX, (int)settings.WindowY);

        if (settings.Maximized)
            WindowState = WindowState.Maximized;

        Opened += OnOpened;
        Closing += OnClosing;
        KeyDown += OnWindowKeyDown;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    // ================================================================ startup

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_started)
            return;

        _started = true;

        if (ViewModel is not { } viewModel)
            return;

        viewModel.ItemsChanged += OnItemsChanged;
        viewModel.SelectAllRequested += OnSelectAllRequested;
        viewModel.InvertSelectionRequested += OnInvertSelectionRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        EnsureVisibleOnScreen();

        await viewModel.InitializeAsync();
        SyncTreeSelection();
    }

    private void EnsureVisibleOnScreen()
    {
        if (Screens is null)
            return;

        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        if (screen is null)
            return;

        var bounds = screen.WorkingArea;
        var right = Position.X + (int)Width;
        var bottom = Position.Y + (int)Height;

        if (Position.X + 40 >= bounds.Right || Position.Y + 40 >= bounds.Bottom
            || right - 40 <= bounds.X || bottom - 40 <= bounds.Y)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Position = new PixelPoint(bounds.X + Math.Max(0, (bounds.Width - (int)Width) / 2),
                bounds.Y + Math.Max(0, (bounds.Height - (int)Height) / 2));
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedTreeNode))
            SyncTreeSelection();
    }

    // ================================================================= closing

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose)
        {
            SaveWindowSettings();
            return;
        }

        if (ViewModel is { } viewModel && SettingsService.Instance.Settings.ConfirmExit)
        {
            e.Cancel = true;
            _ = ConfirmExitAsync(viewModel);
            return;
        }

        SaveWindowSettings();
    }

    private async Task ConfirmExitAsync(MainWindowViewModel viewModel)
    {
        var dialog = new MessageDialog
        {
            DataContext = new MessageDialogViewModel
            {
                Title = "Exit G-RAR",
                Message = "Close G-RAR?",
                Buttons = MessageButtons.YesNo,
                Icon = MessageIcon.Question,
            },
        };

        var result = await dialog.ShowDialog<bool?>(this);
        if (result != true)
            return;

        _forceClose = true;
        Close();
    }

    private void SaveWindowSettings()
    {
        var settings = SettingsService.Instance;
        var data = settings.Settings;

        if (WindowState == WindowState.Normal)
        {
            data.WindowWidth = Width;
            data.WindowHeight = Height;
            data.WindowX = Position.X;
            data.WindowY = Position.Y;
        }

        data.Maximized = WindowState == WindowState.Maximized;

        if (Tree.Bounds.Width >= 120)
            data.TreeWidth = Tree.Bounds.Width;

        settings.Save();
    }

    // ================================================================== tree

    private void OnTreeContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not TreeViewItem item || item.DataContext is not FolderNode node)
            return;

        item.Bind(TreeViewItem.IsExpandedProperty,
            new Binding(nameof(FolderNode.IsExpanded)) { Source = node, Mode = BindingMode.TwoWay });
        item.Bind(TreeViewItem.IsSelectedProperty,
            new Binding(nameof(FolderNode.IsSelected)) { Source = node, Mode = BindingMode.TwoWay });
    }

    private async void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } viewModel || e.AddedItems.Count == 0)
            return;

        if (e.AddedItems[0] is not FolderNode node)
            return;

        // Selections triggered by the view model itself are already applied.
        if (ReferenceEquals(node, viewModel.SelectedTreeNode))
            return;

        await viewModel.OnTreeNodeSelectedAsync(node);
    }

    private void SyncTreeSelection()
    {
        if (ViewModel is not { } viewModel)
            return;

        var node = viewModel.SelectedTreeNode;
        if (node is null)
            return;

        foreach (var ancestor in viewModel.GetAncestorChain(node))
            ancestor.IsExpanded = true;

        if (ReferenceEquals(Tree.SelectedItem, node))
            return;

        // Containers are created during the next layout pass, so select once that has happened.
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(Tree.SelectedItem, node))
                Tree.SelectedItem = node;
        }, DispatcherPriority.Background);
    }

    // ============================================================ file list

    private void OnItemsChanged(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        var wanted = viewModel.SelectionToRestore;
        if (wanted.Count == 0)
            return;

        try
        {
            FileGrid.SelectedItems.Clear();
            foreach (var item in FileGrid.ItemsSource.OfType<FileItem>())
            {
                if (wanted.Contains(item.FullPath))
                    FileGrid.SelectedItems.Add(item);
            }
        }
        catch
        {
            // Restoring the selection is a convenience, never a hard failure.
        }
    }

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        var selected = FileGrid.SelectedItems.OfType<FileItem>().ToList();
        viewModel.OnItemsSelectionChanged(selected);
    }

    private async void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        if (FindAncestor<DataGridColumnHeader>(e.Source as Control) is not null)
            return;

        if (FileGrid.SelectedItem is FileItem item)
            await viewModel.OpenItemAsync(item);
    }

    private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source)
            return;

        if (FindAncestor<DataGridRow>(source) is not { DataContext: FileItem item })
            return;

        if (FileGrid.SelectedItems.OfType<FileItem>().Contains(item))
            return;

        try
        {
            FileGrid.SelectedItems.Clear();
            FileGrid.SelectedItems.Add(item);
        }
        catch
        {
            // Selection is best effort.
        }
    }

    private async void OnPropertiesClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || viewModel.SelectedItem is not { } item)
            return;

        var lines = new List<string>
        {
            $"Name: {item.Name}",
            $"Location: {item.FullName}",
            $"Type: {item.TypeName}",
        };

        if (!item.IsDirectory)
        {
            lines.Add($"Size: {Fmt.Size(item.Size)}");
            if (item.CompressedSize >= 0)
                lines.Add($"Packed: {Fmt.Size(item.CompressedSize)}");
            if (item.Crc > 0)
                lines.Add($"CRC-32: {item.Crc:X8}");
            if (item.Method.Length > 0)
                lines.Add($"Method: {item.Method}");
        }

        if (item.Modified is { } modified)
            lines.Add($"Modified: {Fmt.Date(modified)}");

        if (item.Attributes.Length > 0)
            lines.Add($"Attributes: {item.Attributes}");

        if (item.IsEncrypted)
            lines.Add("Encrypted: yes");

        var dialog = new MessageDialog
        {
            DataContext = new MessageDialogViewModel
            {
                Title = "Properties",
                Message = string.Join("\n", lines),
                Buttons = MessageButtons.Ok,
                Icon = MessageIcon.Info,
            },
        };

        await dialog.ShowDialog<bool?>(this);
    }

    private void OnSelectAllRequested(object? sender, EventArgs e)
    {
        try
        {
            FileGrid.SelectAll();
        }
        catch
        {
            // Ignore, e.g. an empty list.
        }
    }

    private void OnInvertSelectionRequested(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        var current = FileGrid.SelectedItems.OfType<FileItem>().ToHashSet();
        try
        {
            FileGrid.SelectedItems.Clear();
            foreach (var item in viewModel.Items)
            {
                if (!current.Contains(item))
                    FileGrid.SelectedItems.Add(item);
            }
        }
        catch
        {
            // Ignore selection failures.
        }
    }

    // ================================================================ sorting

    private void OnColumnSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        var sortKey = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(sortKey))
            return;

        var descending = string.Equals(viewModel.SortKey, sortKey, StringComparison.Ordinal)
                         && !viewModel.SortDescending;

        // The view model owns the ordering so that folders always stay on top.
        e.Handled = true;
        viewModel.SortBy(sortKey, PropertyPathFor(sortKey), descending);
    }

    private static string PropertyPathFor(string sortKey) => sortKey switch
    {
        "Type" => nameof(FileItem.TypeName),
        "Packed" => nameof(FileItem.CompressedSize),
        "Ratio" => nameof(FileItem.RatioValue),
        _ => sortKey,
    };

    // ============================================================== shortcuts

    private async void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || ViewModel is not { } viewModel)
            return;

        e.Handled = true;
        viewModel.GoCommand.Execute(null);
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        var control = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        switch (e.Key)
        {
            case Key.F5:
                e.Handled = true;
                await RunAsync(viewModel.RefreshCommand);
                break;

            case Key.Delete when !control:
                e.Handled = true;
                await RunAsync(viewModel.DeleteCommand);
                break;

            case Key.F2:
                e.Handled = true;
                await RunAsync(viewModel.RenameCommand);
                break;

            case Key.O when control:
                e.Handled = true;
                await RunAsync(viewModel.OpenArchiveCommand);
                break;

            case Key.W when control:
                e.Handled = true;
                await RunAsync(viewModel.CloseArchiveCommand);
                break;

            case Key.A when control:
                e.Handled = true;
                viewModel.SelectAllCommand.Execute(null);
                break;

            case Key.C when control:
                e.Handled = true;
                await RunAsync(viewModel.CopyPathCommand);
                break;

            case Key.N when control && shift:
                e.Handled = true;
                await RunAsync(viewModel.NewFolderCommand);
                break;

            case Key.E when control:
                e.Handled = true;
                await RunAsync(viewModel.ExtractCommand);
                break;

            case Key.Escape when viewModel.IsBusy:
                e.Handled = true;
                viewModel.CancelOperationCommand.Execute(null);
                break;
        }
    }

    private static async Task RunAsync(ICommand command)
    {
        if (command.CanExecute(null))
            command.Execute(null);

        // Async commands start work in the background; give the UI a chance to breathe.
        await Task.Yield();
    }

    // ============================================================ menu actions

    private async void OnExitClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && SettingsService.Instance.Settings.ConfirmExit)
        {
            var dialog = new MessageDialog
            {
                DataContext = new MessageDialogViewModel
                {
                    Title = "Exit G-RAR",
                    Message = "Close G-RAR?",
                    Buttons = MessageButtons.YesNo,
                    Icon = MessageIcon.Question,
                },
            };

            var result = await dialog.ShowDialog<bool?>(this);
            if (result != true)
                return;
        }

        _forceClose = true;
        Close();
    }

    private static T? FindAncestor<T>(Control? start) where T : Control
    {
        Control? current = start;
        while (current is not null)
        {
            if (current is T match)
                return match;

            current = current.Parent as Control;
        }

        return null;
    }
}
