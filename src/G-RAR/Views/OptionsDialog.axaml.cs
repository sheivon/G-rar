using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GRAR.Views;

public partial class OptionsDialog : Window
{
    public OptionsDialog()
    {
        InitializeComponent();
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);

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
