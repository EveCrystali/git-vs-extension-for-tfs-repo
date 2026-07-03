using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GitForTfs.Services
{
    /// <summary>Per-line change status of a file relative to the last commit (HEAD).</summary>
    internal sealed class GitDiffResult
    {
        public static readonly GitDiffResult Empty = new GitDiffResult();

        /// <summary>1-based working-file line numbers that are new since HEAD.</summary>
        public HashSet<int> Added { get; } = new HashSet<int>();

        /// <summary>1-based working-file line numbers that changed since HEAD.</summary>
        public HashSet<int> Modified { get; } = new HashSet<int>();

        /// <summary>1-based line numbers below which content was deleted since HEAD.</summary>
        public HashSet<int> Deletions { get; } = new HashSet<int>();

        public bool IsEmpty => Added.Count == 0 && Modified.Count == 0 && Deletions.Count == 0;
    }

    /// <summary>
    /// Computes the git diff of a file against HEAD, purely from <c>git.exe</c>, off the UI
    /// thread. Used by the in-process left-margin change bars — it is independent of the tool
    /// window's selected repository and works for any file that lives in a git working tree.
    /// </summary>
    internal static class GitLineDiff
    {
        // @@ -oldStart[,oldCount] +newStart[,newCount] @@
        private static readonly Regex HunkHeader =
            new Regex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

        public static async Task<GitDiffResult> GetDiffAsync(string filePath, CancellationToken cancellationToken)
        {
            var dir = SafeGetDirectory(filePath);
            if (dir == null)
                return GitDiffResult.Empty;

            var args = string.Format(
                CultureInfo.InvariantCulture,
                "--no-pager diff --unified=0 --no-color HEAD -- \"{0}\"",
                filePath);

            var output = await RunGitAsync(args, dir, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(output))
                return GitDiffResult.Empty;

            var result = new GitDiffResult();
            using (var reader = new StringReader(output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length < 2 || line[0] != '@')
                        continue;

                    var m = HunkHeader.Match(line);
                    if (!m.Success)
                        continue;

                    var oldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
                    var newStart = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    var newCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 1;

                    if (newCount == 0)
                    {
                        // Pure deletion: content removed after line newStart in the new file.
                        result.Deletions.Add(Math.Max(1, newStart));
                    }
                    else if (oldCount == 0)
                    {
                        for (var i = 0; i < newCount; i++)
                            result.Added.Add(newStart + i);
                    }
                    else
                    {
                        // Replacement: mark the new lines as modified.
                        for (var i = 0; i < newCount; i++)
                            result.Modified.Add(newStart + i);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Writes the HEAD version of the file to a temp file for the side-by-side diff.
        /// Returns the temp path and the repo-relative path, or <c>null</c> if the file is not
        /// tracked in HEAD.
        /// </summary>
        public static async Task<HeadBlob> GetHeadBlobTempAsync(string filePath, CancellationToken cancellationToken)
        {
            var dir = SafeGetDirectory(filePath);
            if (dir == null)
                return null;

            var root = (await RunGitAsync("rev-parse --show-toplevel", dir, cancellationToken).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(root))
                return null;

            var full = Path.GetFullPath(filePath);
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return null;

            var rel = full.Substring(rootFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');

            var content = await RunGitAsync($"--no-pager show \"HEAD:{rel}\"", dir, cancellationToken).ConfigureAwait(false);
            if (content == null)
                return null; // not in HEAD (e.g. a brand-new file)

            var tmpDir = Path.Combine(Path.GetTempPath(), "GitForTfs", "diff");
            Directory.CreateDirectory(tmpDir);
            var tmp = Path.Combine(tmpDir, Path.GetFileNameWithoutExtension(rel) + ".HEAD" + Path.GetExtension(rel));
            File.WriteAllText(tmp, content, new UTF8Encoding(false));

            return new HeadBlob { TempPath = tmp, RelativePath = rel };
        }

        public sealed class HeadBlob
        {
            public string TempPath { get; set; }
            public string RelativePath { get; set; }
        }

        // ------------------------------------------------------------------

        private static string SafeGetDirectory(string filePath)
        {
            try
            {
                return string.IsNullOrEmpty(filePath) ? null : Path.GetDirectoryName(Path.GetFullPath(filePath));
            }
            catch { return null; }
        }

        private static async Task<string> RunGitAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            try
            {
                using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
                {
                    process.Start();
                    // Drain both pipes: if stderr fills while unread, git blocks and we deadlock.
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();

                    var exitTcs = new TaskCompletionSource<object>();
                    process.Exited += (s, e) => exitTcs.TrySetResult(null);
                    if (process.HasExited)
                        exitTcs.TrySetResult(null);

                    using (cancellationToken.Register(() => exitTcs.TrySetCanceled()))
                    {
                        await exitTcs.Task.ConfigureAwait(false);
                    }

                    var stdout = await stdoutTask.ConfigureAwait(false);
                    await stderrTask.ConfigureAwait(false);
                    return process.ExitCode == 0 ? stdout : null;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
