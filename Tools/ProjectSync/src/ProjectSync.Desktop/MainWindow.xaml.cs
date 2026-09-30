using System.Windows;

namespace ProjectSync.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(Environment.CurrentDirectory);
    }
}
