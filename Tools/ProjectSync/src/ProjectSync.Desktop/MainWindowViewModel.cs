using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using ProjectSync.Core;
using ProjectSync.Infrastructure;

namespace ProjectSync.Desktop;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private string _projectPath = string.Empty;
    private string _taskName = string.Empty;
    private string _resumeBranch = string.Empty;
    private string _currentBranch = string.Empty;
    private string _statusText = string.Empty;
    private string _diagnosticText = string.Empty;
    private Brush _statusBrush = Brushes.DarkOrange;
    private bool _hasUnityShape;
    private bool _hasGit;
    private bool _hasLocalChanges;
    private bool _busy;

    public MainWindowViewModel(string projectPath)
    {
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => !_busy);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStartTask);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => CanResumeTask);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSaveTask);
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, () => CanSubmitTask);
        SelectProject(projectPath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand RefreshCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SubmitCommand { get; }

    public string ProjectPath
    {
        get => _projectPath;
        private set
        {
            SetField(ref _projectPath, value);
            Notify(nameof(ProjectPathDisplay));
        }
    }

    public string ProjectPathDisplay => string.IsNullOrEmpty(ProjectPath) ? "未選択" : ProjectPath;

    public string TaskName
    {
        get => _taskName;
        set => SetField(ref _taskName, value);
    }

    public string ResumeBranch
    {
        get => _resumeBranch;
        set => SetField(ref _resumeBranch, value);
    }

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

    public bool CanStartTask => _hasUnityShape && _hasGit && !_busy;
    public bool CanResumeTask => CanStartTask;
    public bool CanSaveTask => CanStartTask && _currentBranch.StartsWith("task/", StringComparison.Ordinal);
    public bool CanSubmitTask => CanSaveTask;

    public void SelectProject(string? projectPath)
    {
        try
        {
            ProjectPath = string.IsNullOrWhiteSpace(projectPath) ? string.Empty : Path.GetFullPath(projectPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ProjectPath = string.Empty;
        }

        _hasUnityShape = !string.IsNullOrEmpty(ProjectPath) &&
                         Directory.Exists(Path.Combine(ProjectPath, "Assets")) &&
                         Directory.Exists(Path.Combine(ProjectPath, "Packages")) &&
                         Directory.Exists(Path.Combine(ProjectPath, "ProjectSettings"));
        var gitPath = string.IsNullOrEmpty(ProjectPath) ? string.Empty : Path.Combine(ProjectPath, ".git");
        _hasGit = _hasUnityShape && (Directory.Exists(gitPath) || File.Exists(gitPath));
        _currentBranch = string.Empty;
        _hasLocalChanges = false;
        NotifyAvailability();

        if (!_hasUnityShape)
        {
            ShowStatus(string.IsNullOrEmpty(ProjectPath)
                ? "Unityプロジェクトを選択してください。"
                : "選択したフォルダーはUnityプロジェクトではありません。", false);
        }
        else if (!_hasGit)
        {
            ShowStatus("Gitリポジトリがありません。CloneしたUnityプロジェクトを選択してください。", false);
        }
        else
        {
            ShowStatus("Gitの状態を確認しています。", true);
            RefreshCommand.Execute(null);
        }

        UpdateDiagnostics();
    }

    private async Task RefreshAsync(bool preserveStatus = false)
    {
        if (!_hasGit || _busy)
        {
            return;
        }

        var result = await new LocalTaskWorkspace(ProjectPath).InspectAsync();
        if (!result.IsSuccess)
        {
            _currentBranch = string.Empty;
            if (!preserveStatus)
            {
                ShowProblem(result.Problem!);
            }
        }
        else
        {
            _currentBranch = result.Value!.Branch;
            _hasLocalChanges = result.Value.HasLocalChanges;
            if (_currentBranch.StartsWith("task/", StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(ResumeBranch))
            {
                ResumeBranch = _currentBranch;
            }

            if (!preserveStatus)
            {
                ShowStatus(_hasLocalChanges
                    ? "ローカル変更があります。作業の保存はできますが、Branch切替はできません。"
                    : "状態を確認しました。", true);
            }
        }

        NotifyAvailability();
        UpdateDiagnostics();
    }

    private Task StartAsync() => ExecuteAsync(
        workspace => workspace.StartAsync(TaskName),
        branch =>
        {
            ResumeBranch = branch;
            ShowStatus($"新しい作業を開始しました: {branch}", true);
        });

    private Task ResumeAsync() => ExecuteAsync(
        workspace => workspace.ResumeAsync(ResumeBranch.Trim()),
        branch => ShowStatus($"作業を再開しました: {branch}", true));

    private Task SaveAsync() => ExecuteAsync(
        workspace => workspace.SaveAsync(),
        result => ShowStatus($"GitHubへの保存を確認しました: {result.CommitSha}", true));

    private Task SubmitAsync() => ExecuteAsync(
        workspace => workspace.SubmitAsync(TaskName),
        result => ShowStatus($"提出しました。PR: {result.Url} / SHA: {result.SubmittedSha}", true));

    private async Task ExecuteAsync<T>(
        Func<LocalTaskWorkspace, Task<Outcome<T>>> operation,
        Action<T> onSuccess)
    {
        if (!_hasGit || _busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await operation(new LocalTaskWorkspace(ProjectPath));
            if (result.IsSuccess)
            {
                onSuccess(result.Value!);
            }
            else
            {
                ShowProblem(result.Problem!);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowStatus("操作を確認できません。ローカル変更を残して停止しました。詳細: " + exception.Message, false);
        }
        finally
        {
            SetBusy(false);
            await RefreshAsync(preserveStatus: true);
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        NotifyAvailability();
        if (value)
        {
            ShowStatus("処理中です。完了まで操作を繰り返さないでください。", true);
        }
    }

    private void ShowProblem(Problem problem) =>
        ShowStatus($"{problem.ErrorCode} ({problem.Phase}): {problem.Message}", false);

    private void ShowStatus(string message, bool healthy)
    {
        StatusText = message;
        StatusBrush = healthy ? Brushes.DarkGreen : Brushes.DarkRed;
        UpdateDiagnostics();
    }

    private void UpdateDiagnostics()
    {
        DiagnosticText = string.Join(Environment.NewLine,
            $"Unity project : {_hasUnityShape}",
            $"Git repository: {_hasGit}",
            $"Current branch: {(_currentBranch.Length == 0 ? "未確認" : _currentBranch)}",
            $"Local changes : {_hasLocalChanges}",
            $"Unity pipe    : {(string.IsNullOrEmpty(ProjectPath) ? "未選択" : UnityBridgeProtocol.CreatePipeName(ProjectPath))}",
            "",
            "ProjectSyncはmainへCommit・Push・Mergeしません。",
            "Sceneは通常Git、大きな素材はGit LFSで共有します。",
            "管理者がPRのSubmitted SHAを確認して統合してください。",
            "Private + GitHub FreeではGitHub側の強制品質Gateはありません。");
    }

    private void NotifyAvailability()
    {
        foreach (var name in new[] { nameof(CanStartTask), nameof(CanResumeTask), nameof(CanSaveTask), nameof(CanSubmitTask) })
        {
            Notify(name);
        }

        foreach (var command in new[] { RefreshCommand, StartCommand, ResumeCommand, SaveCommand, SubmitCommand })
        {
            (command as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    private void Notify(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        if (propertyName is not null)
        {
            Notify(propertyName);
        }
    }

    private sealed class AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();

        public async void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                await execute();
            }
        }

        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
