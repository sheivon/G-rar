using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GRAR.ViewModels;

namespace GRAR.Views;

public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InputDialogViewModel { ConfirmEnabled: true })
            Close(true);
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

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return))
            return;

        e.Handled = true;
        if (DataContext is InputDialogViewModel { ConfirmEnabled: true })
            Close(true);
    }
}
