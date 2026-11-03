using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GRAR.ViewModels;

namespace GRAR.Views;

public partial class PasswordDialog : Window
{
    public PasswordDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            Password.Focus();
            Password.SelectAll();
        };
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PasswordDialogViewModel { ConfirmEnabled: true })
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
}
