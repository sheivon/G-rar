using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GRAR.ViewModels;

namespace GRAR.Views;

public partial class ExtractDialog : Window
{
    public ExtractDialog()
    {
        InitializeComponent();
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExtractViewModel viewModel)
            return;

        if (StorageProvider is not { CanPickFolder: true } provider)
            return;

        try
        {
            var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select destination folder",
                AllowMultiple = false,
            };

            if (Directory.Exists(viewModel.Destination))
                options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(viewModel.Destination);

            var result = await provider.OpenFolderPickerAsync(options);
            var path = result.Count > 0 ? result[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(path))
                viewModel.Destination = path;
        }
        catch
        {
            // A cancelled picker is not an error.
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExtractViewModel viewModel)
            return;

        if (viewModel.Validate() is null)
        {
            Close(true);
            return;
        }

        viewModel.ShowValidationError(viewModel.Validate()!);
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
