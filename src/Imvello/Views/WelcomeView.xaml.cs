using System.Windows;
using System.Windows.Controls;
using Imvello.ViewModels;

namespace Imvello.Views;

public partial class WelcomeView : UserControl
{
    public WelcomeView() => InitializeComponent();

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    private void AddAccount_Click(object sender, RoutedEventArgs e) => Main?.AddAccount();

    private void Import_Click(object sender, RoutedEventArgs e) => Main?.ImportThunderbird();

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.HasAccounts = true;
    }
}
