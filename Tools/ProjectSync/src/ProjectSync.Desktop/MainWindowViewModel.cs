using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using ProjectSync.Infrastructure;

namespace ProjectSync.Desktop;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private string _statusText = string.Empty;
    private string _diagnosticText = string.Empty;
    private Brush _statusBrush = Brushes.DarkRed;

    public MainWindowViewModel(string projectPath)
    {
        ProjectPath = Path.GetFullPath(projectPath);
        RefreshCommand = new RelayCommand(Refresh);
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProjectPath { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string DiagnosticText
    {
        get => _diagnosticText;
        private set => SetField(ref _diagnosticText, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => SetField(ref _statusBrush, value);
    }

    public bool CanStartTask => false;
    public bool CanResumeTask => false;
    public bool CanSaveTask => false;
    public bool CanSubmitTask => false;
    public ICommand RefreshCommand { get; }

    private void Refresh()
    {
        var hasUnityShape = Directory.Exists(Path.Combine(ProjectPath, "Assets")) &&
                            Directory.Exists(Path.Combine(ProjectPath, "Packages")) &&
                            Directory.Exists(Path.Combine(ProjectPath, "ProjectSettings"));
        var hasGit = Directory.Exists(Path.Combine(ProjectPath, ".git"));
        var pipeName = UnityBridgeProtocol.CreatePipeName(ProjectPath);

        if (!hasUnityShape)
        {
            StatusText = "このパスはUnityプロジェクトとして認識できません。";
            StatusBrush = Brushes.DarkRed;
        }
        else if (!hasGit)
        {
            StatusText = "安全停止: Gitリポジトリとremoteが未設定です。作業操作は無効です。";
            StatusBrush = Brushes.DarkOrange;
        }
        else
        {
            StatusText = "設定検証が必要です。実GitHub操作はまだ有効化されていません。";
            StatusBrush = Brushes.DarkOrange;
        }

        DiagnosticText = string.Join(
            Environment.NewLine,
            $"Unity project : {hasUnityShape}",
            $"Git repository: {hasGit}",
            $"Unity pipe    : {pipeName}",
            "GitHub service : not configured",
            "External writes: disabled",
            "",
            "ProjectSyncは未設定状態で安全停止しています。",
            "Private + GitHub Free: main保護はGitHub側で強制できません。",
            "信頼済み協調サービスと復旧検証が接続されるまで、",
            "新しい作業・保存・提出は実行されません。");
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;

        public RelayCommand(Action execute)
        {
            _execute = execute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();
    }
}
