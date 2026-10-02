using Microsoft.Win32;
using System.Windows;

namespace ProjectSync.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var args = Environment.GetCommandLineArgs();
        var projectArgument = args.Length == 3 &&
                              string.Equals(args[1], "--project", StringComparison.Ordinal)
            ? args[2]
            : string.Empty;
        DataContext = new MainWindowViewModel(projectArgument);
    }

    private void SelectProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Unityプロジェクトのフォルダーを選択してください"
        };
        if (dialog.ShowDialog(this) == true && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SelectProject(dialog.FolderName);
        }
    }
}
