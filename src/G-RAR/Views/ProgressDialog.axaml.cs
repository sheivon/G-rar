using Avalonia.Controls;
using GRAR.ViewModels;

namespace GRAR.Views;

/// <summary>
/// Non modal progress window. Closing it while the operation runs is treated as a
/// cancellation request; the view model closes it once the work has stopped.
/// </summary>
public partial class ProgressDialog : Window
{
    public ProgressDialog()
    {
        InitializeComponent();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not ProgressDialogViewModel viewModel || viewModel.Finished)
            return;

        e.Cancel = true;
        viewModel.CancelCommand.Execute(null);
    }
}
