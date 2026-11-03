using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GRAR.Models;
using GRAR.ViewModels;

namespace GRAR.Views;

/// <summary>"Add to archive" window; the browse button is wired up by the view model.</summary>
public partial class AddArchiveDialog : Window
{
    public AddArchiveDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AddArchiveViewModel viewModel)
                viewModel.BrowseSave = BrowseAsync;
        };
    }

    private async Task<string?> BrowseAsync()
    {
        if (DataContext is not AddArchiveViewModel viewModel)
            return null;

        var provider = StorageProvider;
        if (provider is not { CanSave: true })
            return null;

        try
        {
            var extension = Models.ArchiveFormats.Extension(viewModel.Format);
            var initial = viewModel.ArchivePath;
            var options = new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Save archive as",
                SuggestedFileName = Path.GetFileNameWithoutExtension(initial),
                DefaultExtension = extension.TrimStart('.'),
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType($"{Models.ArchiveFormats.DisplayName(viewModel.Format)} archive")
                    {
                        Patterns = new[] { "*" + extension },
                    },
                    Avalonia.Platform.Storage.FilePickerFileTypes.All,
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

    private void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddArchiveViewModel viewModel)
            return;

        _ = ApplyBrowseAsync(viewModel);
    }

    private async Task ApplyBrowseAsync(AddArchiveViewModel viewModel)
    {
        var path = await BrowseAsync();
        if (!string.IsNullOrEmpty(path))
            viewModel.ArchivePath = path;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddArchiveViewModel viewModel)
            return;

        var error = viewModel.Validate();
        if (error is null)
        {
            Close(true);
            return;
        }

        viewModel.ShowValidationError(error);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(false);
        }
    }
}
