using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using ProjectSync.Infrastructure;

namespace ProjectSync.Desktop;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private string _projectPath = string.Empty;
    private string _statusText = string.Empty;
    private string _diagnosticText = string.Empty;
    private Brush _statusBrush = Brushes.DarkRed;

    public MainWindowViewModel(string projectPath)
    {
        RefreshCommand = new RelayCommand(Refresh);
        SelectProject(projectPath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProjectPath
    {
        get => _projectPath;
        private set
        {
            SetField(ref _projectPath, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProjectPathDisplay)));
        }
    }

    public string ProjectPathDisplay => string.IsNullOrEmpty(ProjectPath) ? "未選択" : ProjectPath;

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

    public void SelectProject(string? projectPath)
    {
        try
        {
            ProjectPath = string.IsNullOrWhiteSpace(projectPath)
                ? string.Empty
                : Path.GetFullPath(projectPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ProjectPath = string.Empty;
        }

        Refresh();
    }

    private void Refresh()
    {
        var hasProjectPath = !string.IsNullOrEmpty(ProjectPath);
        var hasUnityShape = hasProjectPath &&
                            Directory.Exists(Path.Combine(ProjectPath, "Assets")) &&
                            Directory.Exists(Path.Combine(ProjectPath, "Packages")) &&
                            Directory.Exists(Path.Combine(ProjectPath, "ProjectSettings"));
        var gitPath = hasProjectPath ? Path.Combine(ProjectPath, ".git") : string.Empty;
        var hasGit = hasProjectPath && (Directory.Exists(gitPath) || File.Exists(gitPath));
        var pipeName = hasProjectPath ? UnityBridgeProtocol.CreatePipeName(ProjectPath) : "未選択";

        if (!hasProjectPath)
        {
            StatusText = "Unityプロジェクトを選択してください。作業操作はまだ無効です。";
            StatusBrush = Brushes.DarkOrange;
        }
        else if (!hasUnityShape)
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
