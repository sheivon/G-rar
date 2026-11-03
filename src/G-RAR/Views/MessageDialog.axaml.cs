using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GRAR.ViewModels;

namespace GRAR.Views;

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        if (DataContext is not MessageDialogViewModel viewModel)
        {
            Close(null);
            return;
        }

        if (viewModel.ShowCancel)
            Close(null);
        else if (viewModel.ShowNo)
            Close(false);
        else
            Close(true);
    }
}
