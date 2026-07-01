using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitForTfs.Services
{
    /// <summary>
    /// Thin asynchronous wrapper around the <c>git</c> command line executable.
    ///
    /// <para>
    /// This extension deliberately shells out to <c>git.exe</c> instead of registering a
    /// Visual Studio source-control provider. A solution that lives inside a TFVC (TFS)
    /// workspace already has TFVC bound as the active provider, and Visual Studio only
    /// allows a single active provider at a time. Driving the git CLI from a standalone
    /// tool window sidesteps that limitation entirely: TFVC keeps doing server check-ins
    /// while this window handles all local git work.
    /// </para>
    /// </summary>
    public sealed class GitCliService
    {
        private readonly Action<string> _log;

        public GitCliService(Action<string> log = null)
        {
            _log = log;
        }

        /// <summary>
        /// Absolute path of the git working tree (repository root) that commands run against.
        /// </summary>
        public string WorkingDirectory { get; set; }

        /// <summary>Path to the git executable. Defaults to whatever is on PATH.</summary>
        public string GitExecutable { get; set; } = "git";

        public bool HasWorkingDirectory =>
            !string.IsNullOrEmpty(WorkingDirectory) && Directory.Exists(WorkingDirectory);

        // ---------------------------------------------------------------------
        // Core process runner
        // ---------------------------------------------------------------------

        /// <summary>
        /// Runs an arbitrary git command in <see cref="WorkingDirectory"/> and returns the result.
        /// Never throws for non-zero exit codes; inspect <see cref="GitResult.Success"/> instead.
        /// </summary>
        public async Task<GitResult> RunAsync(string arguments, string workingDirectoryOverride = null, CancellationToken cancellationToken = default)
        {
            var workingDir = workingDirectoryOverride ?? WorkingDirectory;

            // `-c core.quotepath=false` keeps non-ASCII paths readable in the output.
            var fullArgs = "-c core.quotepath=false " + arguments;

            var startInfo = new ProcessStartInfo
            {
                FileName = GitExecutable,
                Arguments = fullArgs,
                WorkingDirectory = string.IsNullOrEmpty(workingDir) ? Environment.CurrentDirectory : workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            _log?.Invoke("> git " + arguments);

            try
            {
                using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
                {
                    process.Start();

                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();

                    await WaitForExitAsync(process, cancellationToken).ConfigureAwait(false);

                    var stdout = await stdoutTask.ConfigureAwait(false);
                    var stderr = await stderrTask.ConfigureAwait(false);

                    var result = new GitResult(arguments, process.ExitCode, stdout, stderr);

                    if (!result.Success)
                        _log?.Invoke($"  git exited with code {result.ExitCode}: {result.StandardError.Trim()}");

                    return result;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke("  Failed to launch git: " + ex.Message);
                return new GitResult(arguments, -1, string.Empty,
                    "Could not start git. Make sure Git is installed and on your PATH." + Environment.NewLine + ex.Message);
            }
        }

        private static Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<object>();

            process.Exited += (s, e) => tcs.TrySetResult(null);

            if (process.HasExited)
                tcs.TrySetResult(null);

            if (cancellationToken.CanBeCanceled)
                cancellationToken.Register(() => tcs.TrySetCanceled());

            return tcs.Task;
        }

        // ---------------------------------------------------------------------
        // Repository discovery
        // ---------------------------------------------------------------------

        /// <summary>
        /// Returns the absolute repository root for <paramref name="startingDirectory"/>, or
        /// <c>null</c> if the directory is not inside a git working tree.
        /// </summary>
        public async Task<string> GetRepositoryRootAsync(string startingDirectory, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(startingDirectory) || !Directory.Exists(startingDirectory))
                return null;

            var result = await RunAsync("rev-parse --show-toplevel", startingDirectory, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return null;

            var path = result.StandardOutput.Trim();
            if (path.Length == 0)
                return null;

            // git returns forward slashes on Windows; normalise for display/use.
            return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
        }

        // ---------------------------------------------------------------------
        // Status
        // ---------------------------------------------------------------------

        public async Task<IReadOnlyList<GitChange>> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            var changes = new List<GitChange>();

            var result = await RunAsync("status --porcelain=v1 --untracked-files=all", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return changes;

            using (var reader = new StringReader(result.StandardOutput))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length < 3)
                        continue;

                    var indexStatus = line[0];
                    var workTreeStatus = line[1];
                    var rest = line.Substring(3);

                    string path = rest;
                    string originalPath = null;

                    // Rename/copy entries are written as "old -> new".
                    var arrowIndex = rest.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrowIndex >= 0)
                    {
                        originalPath = rest.Substring(0, arrowIndex).Trim('"');
                        path = rest.Substring(arrowIndex + 4).Trim('"');
                    }
                    else
                    {
                        path = rest.Trim('"');
                    }

                    if (indexStatus == '?' && workTreeStatus == '?')
                    {
                        changes.Add(new GitChange(path, null, '?', GitChangeStage.Untracked));
                        continue;
                    }

                    // A file can be both staged and unstaged (e.g. "MM"); surface both rows.
                    if (indexStatus != ' ' && indexStatus != '?')
                        changes.Add(new GitChange(path, originalPath, indexStatus, GitChangeStage.Staged));

                    if (workTreeStatus != ' ' && workTreeStatus != '?')
                        changes.Add(new GitChange(path, originalPath, workTreeStatus, GitChangeStage.Unstaged));
                }
            }

            return changes;
        }

        // ---------------------------------------------------------------------
        // Branches
        // ---------------------------------------------------------------------

        public async Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default)
        {
            var result = await RunAsync("rev-parse --abbrev-ref HEAD", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return null;

            var branch = result.StandardOutput.Trim();
            return branch == "HEAD" ? "(detached HEAD)" : branch;
        }

        public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default)
        {
            var branches = new List<GitBranch>();

            // Unit separator (0x1F) is a safe field delimiter that never appears in refs.
            const string format = "%(HEAD)\u001f%(refname:short)\u001f%(upstream:short)\u001f%(upstream:track)";
            var result = await RunAsync("for-each-ref --format=\"" + format + "\" refs/heads", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return branches;

            using (var reader = new StringReader(result.StandardOutput))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var parts = line.Split('\u001f');
                    if (parts.Length < 4)
                        continue;

                    var isCurrent = parts[0].Trim() == "*";
                    var name = parts[1];
                    var upstream = string.IsNullOrWhiteSpace(parts[2]) ? null : parts[2];
                    ParseAheadBehind(parts[3], out var ahead, out var behind);

                    branches.Add(new GitBranch(name, isCurrent, upstream, ahead, behind));
                }
            }

            return branches;
        }

        private static void ParseAheadBehind(string track, out int ahead, out int behind)
        {
            ahead = 0;
            behind = 0;
            if (string.IsNullOrEmpty(track))
                return;

            // Format looks like "[ahead 2, behind 1]".
            foreach (var token in track.Trim('[', ']').Split(','))
            {
                var t = token.Trim();
                if (t.StartsWith("ahead", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(new string(t.ToCharArray(), 5, t.Length - 5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ahead);
                else if (t.StartsWith("behind", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(new string(t.ToCharArray(), 6, t.Length - 6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out behind);
            }
        }

        // ---------------------------------------------------------------------
        // History
        // ---------------------------------------------------------------------

        public async Task<IReadOnlyList<GitCommit>> GetLogAsync(int maxCount = 100, CancellationToken cancellationToken = default)
        {
            var commits = new List<GitCommit>();

            // Fields delimited by unit separator; records terminated by a literal marker.
            const string format = "%H%x1f%h%x1f%an%x1f%ar%x1f%s";
            var args = $"log --max-count={maxCount} --pretty=format:\"{format}\"";
            var result = await RunAsync(args, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return commits;

            using (var reader = new StringReader(result.StandardOutput))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0)
                        continue;

                    var parts = line.Split('\u001f');
                    if (parts.Length < 5)
                        continue;

                    commits.Add(new GitCommit(parts[0], parts[1], parts[2], parts[3], parts[4]));
                }
            }

            return commits;
        }

        // ---------------------------------------------------------------------
        // Mutating operations
        // ---------------------------------------------------------------------

        public Task<GitResult> StageAsync(string path, CancellationToken cancellationToken = default) =>
            RunAsync($"add -- \"{path}\"", cancellationToken: cancellationToken);

        public Task<GitResult> StageAllAsync(CancellationToken cancellationToken = default) =>
            RunAsync("add --all", cancellationToken: cancellationToken);

        public Task<GitResult> UnstageAsync(string path, CancellationToken cancellationToken = default) =>
            RunAsync($"restore --staged -- \"{path}\"", cancellationToken: cancellationToken);

        public Task<GitResult> UnstageAllAsync(CancellationToken cancellationToken = default) =>
            RunAsync("reset", cancellationToken: cancellationToken);

        public async Task<GitResult> CommitAsync(string message, bool amend = false, CancellationToken cancellationToken = default)
        {
            // Write the message to a temp file so multi-line messages and quotes survive intact.
            var tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, message, new UTF8Encoding(false));
                var amendArg = amend ? "--amend " : string.Empty;
                return await RunAsync($"commit {amendArg}--file=\"{tempFile}\"", cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
        }

        public Task<GitResult> FetchAsync(CancellationToken cancellationToken = default) =>
            RunAsync("fetch --all --prune", cancellationToken: cancellationToken);

        public Task<GitResult> PullAsync(CancellationToken cancellationToken = default) =>
            RunAsync("pull --ff-only", cancellationToken: cancellationToken);

        public async Task<GitResult> PushAsync(string currentBranch, bool setUpstream, CancellationToken cancellationToken = default)
        {
            if (setUpstream && !string.IsNullOrEmpty(currentBranch))
                return await RunAsync($"push --set-upstream origin \"{currentBranch}\"", cancellationToken: cancellationToken).ConfigureAwait(false);

            return await RunAsync("push", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public Task<GitResult> CheckoutAsync(string branchName, CancellationToken cancellationToken = default) =>
            RunAsync($"checkout \"{branchName}\"", cancellationToken: cancellationToken);

        public Task<GitResult> CreateBranchAsync(string branchName, bool checkout, CancellationToken cancellationToken = default) =>
            RunAsync($"{(checkout ? "checkout -b" : "branch")} \"{branchName}\"", cancellationToken: cancellationToken);

        public Task<GitResult> DiscardAsync(string path, CancellationToken cancellationToken = default) =>
            RunAsync($"checkout -- \"{path}\"", cancellationToken: cancellationToken);

        /// <summary>Returns the unified diff for a single file (staged or working tree).</summary>
        public Task<GitResult> GetDiffAsync(string path, bool staged, CancellationToken cancellationToken = default) =>
            RunAsync($"diff {(staged ? "--staged " : string.Empty)}-- \"{path}\"", cancellationToken: cancellationToken);
    }
}
