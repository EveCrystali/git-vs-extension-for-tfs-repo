using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using GitForTfs.Mvvm;
using GitForTfs.Services;

namespace GitForTfs.ViewModels
{
    /// <summary>
    /// Backing view model for the Git tool window. Owns a <see cref="GitCliService"/> and
    /// exposes observable collections plus commands for every supported git operation.
    /// </summary>
    public sealed class GitToolWindowViewModel : ViewModelBase
    {
        private readonly GitCliService _git;
        private readonly Func<Task<string>> _solutionDirectoryProvider;
        private readonly Func<DiffRequest, Task> _openDiff;
        private readonly Action<string> _log;

        private string _repositoryPath;
        private string _currentBranch;
        private string _commitMessage;
        private string _newBranchName;
        private string _statusMessage;
        private string _fileHistoryTitle;
        private string _historyFileRelativePath;
        private int _selectedTabIndex;
        private bool _isBusy;
        private bool _hasRepository;

        public GitToolWindowViewModel(Func<Task<string>> solutionDirectoryProvider, Func<DiffRequest, Task> openDiff, Action<string> log)
        {
            _solutionDirectoryProvider = solutionDirectoryProvider;
            _openDiff = openDiff;
            _log = log;
            _git = new GitCliService(log);

            StagedChanges = new ObservableCollection<ChangeItemViewModel>();
            UnstagedChanges = new ObservableCollection<ChangeItemViewModel>();
            Branches = new ObservableCollection<BranchItemViewModel>();
            Commits = new ObservableCollection<CommitItemViewModel>();
            FileHistoryCommits = new ObservableCollection<CommitItemViewModel>();

            RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => HasRepository && !IsBusy);
            AutoDetectCommand = new AsyncRelayCommand(_ => AutoDetectRepositoryAsync(), _ => !IsBusy);
            SetRepositoryCommand = new AsyncRelayCommand(_ => SetRepositoryAsync(RepositoryPath), _ => !IsBusy);

            StageCommand = new AsyncRelayCommand(p => StageAsync(p as ChangeItemViewModel), _ => HasRepository && !IsBusy);
            UnstageCommand = new AsyncRelayCommand(p => UnstageAsync(p as ChangeItemViewModel), _ => HasRepository && !IsBusy);
            DiscardCommand = new AsyncRelayCommand(p => DiscardAsync(p as ChangeItemViewModel), _ => HasRepository && !IsBusy);
            StageAllCommand = new AsyncRelayCommand(_ => RunAndRefreshAsync("Stage all", () => _git.StageAllAsync()), _ => HasRepository && !IsBusy);
            UnstageAllCommand = new AsyncRelayCommand(_ => RunAndRefreshAsync("Unstage all", () => _git.UnstageAllAsync()), _ => HasRepository && !IsBusy);

            CommitCommand = new AsyncRelayCommand(_ => CommitAsync(push: false), _ => CanCommit);
            CommitAndPushCommand = new AsyncRelayCommand(_ => CommitAsync(push: true), _ => CanCommit);

            FetchCommand = new AsyncRelayCommand(_ => RunAndRefreshAsync("Fetch", () => _git.FetchAsync()), _ => HasRepository && !IsBusy);
            PullCommand = new AsyncRelayCommand(_ => RunAndRefreshAsync("Pull", () => _git.PullAsync()), _ => HasRepository && !IsBusy);
            PushCommand = new AsyncRelayCommand(_ => PushAsync(), _ => HasRepository && !IsBusy);

            CreateBranchCommand = new AsyncRelayCommand(_ => CreateBranchAsync(), _ => HasRepository && !IsBusy && !string.IsNullOrWhiteSpace(NewBranchName));
            CheckoutCommand = new AsyncRelayCommand(p => CheckoutAsync(p as BranchItemViewModel), _ => HasRepository && !IsBusy);

            OpenDiffCommand = new AsyncRelayCommand(p => OpenDiffAsync(p as ChangeItemViewModel), _ => HasRepository && !IsBusy);
            ShowFileHistoryCommand = new AsyncRelayCommand(p => ShowFileHistoryAsync(p as ChangeItemViewModel), _ => HasRepository && !IsBusy);
            OpenCommitDiffCommand = new AsyncRelayCommand(p => OpenCommitDiffAsync(p as CommitItemViewModel), _ => HasRepository && !IsBusy);
        }

        // -----------------------------------------------------------------
        // Collections
        // -----------------------------------------------------------------

        public ObservableCollection<ChangeItemViewModel> StagedChanges { get; }
        public ObservableCollection<ChangeItemViewModel> UnstagedChanges { get; }
        public ObservableCollection<BranchItemViewModel> Branches { get; }
        public ObservableCollection<CommitItemViewModel> Commits { get; }
        public ObservableCollection<CommitItemViewModel> FileHistoryCommits { get; }

        // -----------------------------------------------------------------
        // Bindable state
        // -----------------------------------------------------------------

        public string RepositoryPath
        {
            get => _repositoryPath;
            set => SetProperty(ref _repositoryPath, value);
        }

        public string CurrentBranch
        {
            get => _currentBranch;
            set => SetProperty(ref _currentBranch, value);
        }

        public string CommitMessage
        {
            get => _commitMessage;
            set => SetProperty(ref _commitMessage, value);
        }

        public string NewBranchName
        {
            get => _newBranchName;
            set => SetProperty(ref _newBranchName, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                    RaisePropertyChanged(nameof(IsNotBusy));
            }
        }

        public bool IsNotBusy => !_isBusy;

        public bool HasRepository
        {
            get => _hasRepository;
            set => SetProperty(ref _hasRepository, value);
        }

        public bool CanCommit =>
            HasRepository && !IsBusy && StagedChanges.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);

        public string FileHistoryTitle
        {
            get => _fileHistoryTitle;
            set => SetProperty(ref _fileHistoryTitle, value);
        }

        public bool HasFileHistory => FileHistoryCommits.Count > 0;

        /// <summary>Bound to the tab control so code can bring a tab to the front programmatically.</summary>
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => SetProperty(ref _selectedTabIndex, value);
        }

        // -----------------------------------------------------------------
        // Commands
        // -----------------------------------------------------------------

        public ICommand RefreshCommand { get; }
        public ICommand AutoDetectCommand { get; }
        public ICommand SetRepositoryCommand { get; }
        public ICommand StageCommand { get; }
        public ICommand UnstageCommand { get; }
        public ICommand DiscardCommand { get; }
        public ICommand StageAllCommand { get; }
        public ICommand UnstageAllCommand { get; }
        public ICommand CommitCommand { get; }
        public ICommand CommitAndPushCommand { get; }
        public ICommand FetchCommand { get; }
        public ICommand PullCommand { get; }
        public ICommand PushCommand { get; }
        public ICommand CreateBranchCommand { get; }
        public ICommand CheckoutCommand { get; }
        public ICommand OpenDiffCommand { get; }
        public ICommand ShowFileHistoryCommand { get; }
        public ICommand OpenCommitDiffCommand { get; }

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        /// <summary>
        /// Called once when the tool window is shown. Restores the saved repository or, failing
        /// that, tries to locate one from the active solution.
        /// </summary>
        public async Task InitializeAsync()
        {
            var saved = SettingsStore.LoadRepositoryPath();
            if (!string.IsNullOrEmpty(saved))
            {
                await SetRepositoryAsync(saved).ConfigureAwait(true);
                if (HasRepository)
                    return;
            }

            await AutoDetectRepositoryAsync().ConfigureAwait(true);
        }

        private async Task AutoDetectRepositoryAsync()
        {
            IsBusy = true;
            try
            {
                string hint = null;
                if (_solutionDirectoryProvider != null)
                    hint = await _solutionDirectoryProvider().ConfigureAwait(true);

                if (string.IsNullOrEmpty(hint))
                {
                    StatusMessage = "No solution is open. Paste the path to your git folder and click Set.";
                    return;
                }

                var root = await _git.GetRepositoryRootAsync(hint).ConfigureAwait(true);
                if (string.IsNullOrEmpty(root))
                {
                    StatusMessage = $"No git repository found at or above '{hint}'. Paste the git folder path and click Set.";
                    HasRepository = false;
                    return;
                }

                ApplyRepository(root);
                await RefreshAsync().ConfigureAwait(true);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task SetRepositoryAsync(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                StatusMessage = "Enter a folder path first.";
                return;
            }

            IsBusy = true;
            try
            {
                var root = await _git.GetRepositoryRootAsync(candidate.Trim()).ConfigureAwait(true);
                if (string.IsNullOrEmpty(root))
                {
                    StatusMessage = $"'{candidate}' is not inside a git repository.";
                    HasRepository = false;
                    return;
                }

                ApplyRepository(root);
                await RefreshAsync().ConfigureAwait(true);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ApplyRepository(string root)
        {
            _git.WorkingDirectory = root;
            RepositoryPath = root;
            HasRepository = true;
            SettingsStore.SaveRepositoryPath(root);
            _log?.Invoke($"Repository set to: {root}");
        }

        // -----------------------------------------------------------------
        // Refresh
        // -----------------------------------------------------------------

        public async Task RefreshAsync()
        {
            if (!_git.HasWorkingDirectory)
                return;

            IsBusy = true;
            try
            {
                CurrentBranch = await _git.GetCurrentBranchAsync().ConfigureAwait(true) ?? "(unknown)";

                var changes = await _git.GetStatusAsync().ConfigureAwait(true);
                StagedChanges.Clear();
                UnstagedChanges.Clear();
                foreach (var change in changes)
                {
                    var vm = new ChangeItemViewModel(change);
                    if (change.Stage == GitChangeStage.Staged)
                        StagedChanges.Add(vm);
                    else
                        UnstagedChanges.Add(vm);
                }

                var branches = await _git.GetBranchesAsync().ConfigureAwait(true);
                Branches.Clear();
                foreach (var branch in branches)
                    Branches.Add(new BranchItemViewModel(branch));

                var commits = await _git.GetLogAsync().ConfigureAwait(true);
                Commits.Clear();
                foreach (var commit in commits)
                    Commits.Add(new CommitItemViewModel(commit));

                RaisePropertyChanged(nameof(CanCommit));
                StatusMessage = $"{StagedChanges.Count} staged, {UnstagedChanges.Count} unstaged — on {CurrentBranch}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // -----------------------------------------------------------------
        // Per-file operations
        // -----------------------------------------------------------------

        private Task StageAsync(ChangeItemViewModel item) =>
            item == null ? Task.CompletedTask : RunAndRefreshAsync($"Stage {item.FileName}", () => _git.StageAsync(item.Path));

        private Task UnstageAsync(ChangeItemViewModel item) =>
            item == null ? Task.CompletedTask : RunAndRefreshAsync($"Unstage {item.FileName}", () => _git.UnstageAsync(item.Path));

        private Task DiscardAsync(ChangeItemViewModel item) =>
            item == null ? Task.CompletedTask : RunAndRefreshAsync($"Discard changes in {item.FileName}", () => _git.DiscardAsync(item.Path));

        // -----------------------------------------------------------------
        // Commit / sync
        // -----------------------------------------------------------------

        private async Task CommitAsync(bool push)
        {
            if (!CanCommit)
                return;

            IsBusy = true;
            try
            {
                var result = await _git.CommitAsync(CommitMessage).ConfigureAwait(true);
                StatusMessage = result.Success ? "Commit created." : "Commit failed — see the Git output window.";
                if (!result.Success)
                    return;

                CommitMessage = string.Empty;

                if (push)
                {
                    var pushResult = await _git.PushAsync(CurrentBranch, setUpstream: false).ConfigureAwait(true);
                    if (!pushResult.Success && LooksLikeMissingUpstream(pushResult))
                        pushResult = await _git.PushAsync(CurrentBranch, setUpstream: true).ConfigureAwait(true);

                    StatusMessage = pushResult.Success ? "Committed and pushed." : "Committed, but push failed — see the Git output window.";
                }
            }
            finally
            {
                IsBusy = false;
                await RefreshAsync().ConfigureAwait(true);
            }
        }

        private async Task PushAsync()
        {
            IsBusy = true;
            try
            {
                var result = await _git.PushAsync(CurrentBranch, setUpstream: false).ConfigureAwait(true);
                if (!result.Success && LooksLikeMissingUpstream(result))
                    result = await _git.PushAsync(CurrentBranch, setUpstream: true).ConfigureAwait(true);

                StatusMessage = result.Success ? "Pushed." : "Push failed — see the Git output window.";
            }
            finally
            {
                IsBusy = false;
                await RefreshAsync().ConfigureAwait(true);
            }
        }

        private static bool LooksLikeMissingUpstream(GitResult result) =>
            result.StandardError.IndexOf("no upstream", StringComparison.OrdinalIgnoreCase) >= 0 ||
            result.StandardError.IndexOf("set-upstream", StringComparison.OrdinalIgnoreCase) >= 0 ||
            result.StandardError.IndexOf("has no upstream branch", StringComparison.OrdinalIgnoreCase) >= 0;

        // -----------------------------------------------------------------
        // Branches
        // -----------------------------------------------------------------

        private async Task CreateBranchAsync()
        {
            var name = NewBranchName?.Trim();
            if (string.IsNullOrEmpty(name))
                return;

            await RunAndRefreshAsync($"Create branch {name}", () => _git.CreateBranchAsync(name, checkout: true)).ConfigureAwait(true);
            NewBranchName = string.Empty;
        }

        private Task CheckoutAsync(BranchItemViewModel item) =>
            item == null || item.IsCurrent
                ? Task.CompletedTask
                : RunAndRefreshAsync($"Checkout {item.Name}", () => _git.CheckoutAsync(item.Name));

        // -----------------------------------------------------------------
        // Diff viewer
        // -----------------------------------------------------------------

        private async Task OpenDiffAsync(ChangeItemViewModel item)
        {
            if (item == null || _openDiff == null)
                return;

            IsBusy = true;
            try
            {
                var relPath = item.Path;
                var oldRelPath = item.Change.OriginalPath ?? relPath;
                string leftFile, rightFile, leftLabel, rightLabel;
                bool rightIsTemporary;

                if (item.Change.Stage == GitChangeStage.Untracked)
                {
                    // Nothing to compare against yet: empty vs the new file.
                    leftFile = await WriteTempBlobAsync(string.Empty, relPath, "empty").ConfigureAwait(true);
                    rightFile = GetWorkingFilePath(relPath);
                    rightIsTemporary = false; // real working-tree file — must NOT be deleted
                    leftLabel = "(new file)";
                    rightLabel = relPath + " (working tree)";
                }
                else if (item.Change.Stage == GitChangeStage.Staged)
                {
                    // Staged: HEAD vs index.
                    leftFile = await WriteTempBlobAsync("HEAD:" + oldRelPath, relPath, "HEAD").ConfigureAwait(true);
                    rightFile = await WriteTempBlobAsync(":" + relPath, relPath, "index").ConfigureAwait(true);
                    rightIsTemporary = true;
                    leftLabel = relPath + " (HEAD)";
                    rightLabel = relPath + " (staged)";
                }
                else
                {
                    // Unstaged: index vs working tree.
                    var isDeleted = item.Change.StatusCode == 'D';
                    leftFile = await WriteTempBlobAsync(":" + oldRelPath, relPath, "index").ConfigureAwait(true);
                    rightFile = isDeleted
                        ? await WriteTempBlobAsync(string.Empty, relPath, "deleted").ConfigureAwait(true)
                        : GetWorkingFilePath(relPath);
                    rightIsTemporary = isDeleted; // only the empty placeholder is temporary
                    leftLabel = relPath + " (index)";
                    rightLabel = isDeleted ? "(deleted)" : relPath + " (working tree)";
                }

                await _openDiff(new DiffRequest(leftFile, rightFile, "Diff: " + item.FileName, leftLabel, rightLabel,
                    leftIsTemporary: true, rightIsTemporary: rightIsTemporary)).ConfigureAwait(true);
                StatusMessage = "Opened diff for " + item.FileName;
            }
            catch (Exception ex)
            {
                StatusMessage = "Could not open diff: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task OpenCommitDiffAsync(CommitItemViewModel commit)
        {
            if (commit == null || _openDiff == null || string.IsNullOrEmpty(_historyFileRelativePath))
                return;

            IsBusy = true;
            try
            {
                var relPath = _historyFileRelativePath;
                var fileName = Path.GetFileName(relPath);
                var hash = commit.Commit.FullHash;

                var leftFile = await WriteTempBlobAsync($"{hash}~1:{relPath}", relPath, commit.ShortHash + "~1").ConfigureAwait(true);
                var rightFile = await WriteTempBlobAsync($"{hash}:{relPath}", relPath, commit.ShortHash).ConfigureAwait(true);

                await _openDiff(new DiffRequest(
                    leftFile, rightFile,
                    $"Diff: {fileName} @ {commit.ShortHash}",
                    $"{fileName} ({commit.ShortHash}~1)",
                    $"{fileName} ({commit.ShortHash})")).ConfigureAwait(true);

                StatusMessage = $"Opened diff for {fileName} at {commit.ShortHash}";
            }
            catch (Exception ex)
            {
                StatusMessage = "Could not open diff: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private string GetWorkingFilePath(string relativePath)
        {
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(_git.WorkingDirectory ?? string.Empty, normalized);
        }

        private async Task<string> WriteTempBlobAsync(string revisionSpec, string relativePathForExtension, string tag)
        {
            var content = string.IsNullOrEmpty(revisionSpec)
                ? string.Empty
                : await _git.GetBlobContentAsync(revisionSpec).ConfigureAwait(true);

            var ext = Path.GetExtension(relativePathForExtension);
            var baseName = Path.GetFileNameWithoutExtension(relativePathForExtension);
            var safeTag = tag.Replace(':', '_').Replace('~', '_').Replace('/', '_').Replace('\\', '_');
            var fileName = $"{baseName}.{safeTag}{ext}";

            var dir = Path.Combine(Path.GetTempPath(), "GitForTfs", "diff");
            Directory.CreateDirectory(dir);
            var fullPath = Path.Combine(dir, fileName);

            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
            return fullPath;
        }

        // -----------------------------------------------------------------
        // Per-file history
        // -----------------------------------------------------------------

        private Task ShowFileHistoryAsync(ChangeItemViewModel item) =>
            item == null ? Task.CompletedTask : ShowFileHistoryForPathAsync(item.Path);

        /// <summary>
        /// Loads the commit history for a file and brings the "File History" tab forward.
        /// <paramref name="path"/> may be absolute (e.g. from Solution Explorer) or already
        /// relative to the repository root.
        /// </summary>
        public async Task ShowFileHistoryForPathAsync(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            if (!_git.HasWorkingDirectory)
            {
                StatusMessage = "Set a git repository first.";
                return;
            }

            var relPath = ToRepositoryRelativePath(path);

            IsBusy = true;
            try
            {
                _historyFileRelativePath = relPath;
                FileHistoryTitle = "History: " + relPath;

                var commits = await _git.GetFileLogAsync(relPath).ConfigureAwait(true);
                FileHistoryCommits.Clear();
                foreach (var commit in commits)
                    FileHistoryCommits.Add(new CommitItemViewModel(commit));

                RaisePropertyChanged(nameof(HasFileHistory));
                SelectedTabIndex = 3; // File History tab
                StatusMessage = $"{FileHistoryCommits.Count} commit(s) touched {relPath}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private string ToRepositoryRelativePath(string path)
        {
            var root = _git.WorkingDirectory;
            if (string.IsNullOrEmpty(root) || !Path.IsPathRooted(path))
                return path.Replace('\\', '/');

            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(fullRoot.Length).Replace('\\', '/');

            return path.Replace('\\', '/');
        }

        // -----------------------------------------------------------------
        // Shared runner
        // -----------------------------------------------------------------

        private async Task RunAndRefreshAsync(string label, Func<Task<GitResult>> action)
        {
            IsBusy = true;
            try
            {
                var result = await action().ConfigureAwait(true);
                StatusMessage = result.Success
                    ? $"{label}: done."
                    : $"{label} failed — see the Git output window.";
            }
            finally
            {
                IsBusy = false;
                await RefreshAsync().ConfigureAwait(true);
            }
        }
    }
}
