using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitForTfs.CodeLens
{
    /// <summary>
    /// TEMP diagnostics: appends to %TEMP%\GitForTfs.CodeLens.log so we can see what the
    /// out-of-process CodeLens host is doing. Remove once the lens is confirmed working.
    /// </summary>
    internal static class Diag
    {
        private static readonly object Gate = new object();
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "GitForTfs.CodeLens.log");

        // Opt-in only: set the GITFORTFS_CODELENS_LOG environment variable to 1 to trace
        // the CodeLens data-point pipeline into %TEMP%\GitForTfs.CodeLens.log. Off by
        // default so a normal install writes nothing.
        internal static bool Enabled =
            Environment.GetEnvironmentVariable("GITFORTFS_CODELENS_LOG") == "1";

        internal static void Log(string message)
        {
            if (!Enabled)
                return;
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + "  " + message + Environment.NewLine);
                }
            }
            catch { /* diagnostics must never throw */ }
        }
    }

    /// <summary>Result of a git blame lookup for a code element's line range.</summary>
    public sealed class BlameInfo
    {
        public string Author { get; set; }
        public string ShortHash { get; set; }
        public string Summary { get; set; }
        public string RelativeDate { get; set; }
    }

    /// <summary>
    /// Self-contained git blame helper for the CodeLens data point. Runs entirely in the
    /// out-of-process CodeLens host, so it must NOT depend on any Visual Studio shell service
    /// — it only shells out to <c>git.exe</c> and reads files from disk.
    /// </summary>
    public static class GitBlameReader
    {
        // Cache repository membership per directory to keep CanCreateDataPointAsync cheap
        // (it is called for every code element in a document).
        private static readonly ConcurrentDictionary<string, bool> RepoMembershipCache =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Fast, process-free check for whether a file lives inside a git working tree, by
        /// walking parent directories looking for a <c>.git</c> entry.
        /// </summary>
        public static bool IsInRepository(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;

            var dir = SafeGetDirectory(filePath);
            if (dir == null)
                return false;

            return RepoMembershipCache.GetOrAdd(dir, d =>
            {
                var current = new DirectoryInfo(d);
                while (current != null)
                {
                    if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                        File.Exists(Path.Combine(current.FullName, ".git")))
                        return true;

                    current = current.Parent;
                }

                return false;
            });
        }

        /// <summary>
        /// Maps a character span (from the editor buffer) to 1-based, clamped line numbers by
        /// reading the file on disk. Unsaved edits may shift the mapping slightly.
        /// </summary>
        public static (int startLine, int endLine) MapSpanToLines(string filePath, int startOffset, int endOffset)
        {
            try
            {
                var text = File.ReadAllText(filePath);
                var totalLines = CountLines(text);

                var startLine = ClampLine(LineOfOffset(text, startOffset), totalLines);
                var endLine = ClampLine(LineOfOffset(text, endOffset), totalLines);

                if (endLine < startLine)
                    endLine = startLine;

                return (startLine, endLine);
            }
            catch
            {
                return (1, 1);
            }
        }

        /// <summary>
        /// Returns the most recent commit that touched the given line range, or <c>null</c>.
        /// </summary>
        public static async Task<BlameInfo> GetLastChangeAsync(string filePath, int startLine, int endLine, CancellationToken cancellationToken)
        {
            var dir = SafeGetDirectory(filePath);
            if (dir == null)
                return null;

            var args = string.Format(
                CultureInfo.InvariantCulture,
                "--no-pager blame --porcelain -L {0},{1} -- \"{2}\"",
                startLine, endLine, filePath);

            Diag.Log($"GetLastChange dir='{dir}' lines={startLine},{endLine} file='{filePath}'");

            var result = await RunGitAsync(args, dir, cancellationToken).ConfigureAwait(false);
            if (result == null)
            {
                Diag.Log("GetLastChange: git returned null (see RunGit log)");
                return null;
            }

            var parsed = ParseNewestCommit(result);
            Diag.Log($"GetLastChange: parsed={(parsed == null ? "null" : parsed.Author + " / " + parsed.ShortHash)}");
            return parsed;
        }

        // ------------------------------------------------------------------
        // Porcelain parsing
        // ------------------------------------------------------------------

        private static BlameInfo ParseNewestCommit(string porcelain)
        {
            var byHash = new Dictionary<string, (string author, long time, string summary)>(StringComparer.OrdinalIgnoreCase);
            string currentHash = null;

            using (var reader = new StringReader(porcelain))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '\t')
                        continue; // code line or blank — skip

                    var spaceIndex = line.IndexOf(' ');
                    var key = spaceIndex < 0 ? line : line.Substring(0, spaceIndex);
                    var value = spaceIndex < 0 ? string.Empty : line.Substring(spaceIndex + 1);

                    if (IsHash(key))
                    {
                        currentHash = key;
                        if (!byHash.ContainsKey(currentHash))
                            byHash[currentHash] = (null, 0, null);
                        continue;
                    }

                    if (currentHash == null || !byHash.TryGetValue(currentHash, out var entry))
                        continue;

                    switch (key)
                    {
                        case "author":
                            entry.author = value;
                            break;
                        case "committer-time":
                            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out entry.time);
                            break;
                        case "summary":
                            entry.summary = value;
                            break;
                    }

                    byHash[currentHash] = entry;
                }
            }

            string bestHash = null;
            long bestTime = long.MinValue;
            foreach (var kvp in byHash)
            {
                if (kvp.Value.time > bestTime)
                {
                    bestTime = kvp.Value.time;
                    bestHash = kvp.Key;
                }
            }

            if (bestHash == null)
                return null;

            var best = byHash[bestHash];

            // A blame line for an uncommitted change reports an all-zero hash.
            var isUncommitted = bestHash.Replace("0", string.Empty).Length == 0;

            return new BlameInfo
            {
                Author = isUncommitted ? "Uncommitted" : (best.author ?? "Unknown"),
                ShortHash = isUncommitted ? "-------" : bestHash.Substring(0, 7),
                Summary = best.summary ?? string.Empty,
                RelativeDate = isUncommitted ? "not committed yet" : ToRelativeDate(best.time),
            };
        }

        private static bool IsHash(string token)
        {
            if (token.Length != 40)
                return false;

            foreach (var c in token)
            {
                var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex)
                    return false;
            }

            return true;
        }

        private static string ToRelativeDate(long unixSeconds)
        {
            if (unixSeconds <= 0)
                return string.Empty;

            var when = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            var delta = DateTimeOffset.UtcNow - when;

            if (delta.TotalSeconds < 60) return "just now";
            if (delta.TotalMinutes < 60) return Plural((int)delta.TotalMinutes, "minute");
            if (delta.TotalHours < 24) return Plural((int)delta.TotalHours, "hour");
            if (delta.TotalDays < 30) return Plural((int)delta.TotalDays, "day");
            if (delta.TotalDays < 365) return Plural((int)(delta.TotalDays / 30), "month");
            return Plural((int)(delta.TotalDays / 365), "year");
        }

        private static string Plural(int value, string unit)
        {
            if (value <= 0)
                value = 1;
            return value + " " + unit + (value == 1 ? "" : "s") + " ago";
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static int LineOfOffset(string text, int offset)
        {
            if (offset < 0)
                offset = 0;
            if (offset > text.Length)
                offset = text.Length;

            var line = 1;
            for (var i = 0; i < offset; i++)
            {
                if (text[i] == '\n')
                    line++;
            }

            return line;
        }

        private static int CountLines(string text)
        {
            var lines = 1;
            foreach (var c in text)
            {
                if (c == '\n')
                    lines++;
            }

            return lines;
        }

        private static int ClampLine(int line, int totalLines)
        {
            if (line < 1) return 1;
            if (line > totalLines) return totalLines;
            return line;
        }

        private static string SafeGetDirectory(string filePath)
        {
            try
            {
                return Path.GetDirectoryName(Path.GetFullPath(filePath));
            }
            catch
            {
                return null;
            }
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
                StandardErrorEncoding = Encoding.UTF8,
            };

            try
            {
                using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
                {
                    process.Start();

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
                    var stderr = await stderrTask.ConfigureAwait(false);
                    Diag.Log($"RunGit exit={process.ExitCode} outLen={stdout?.Length ?? 0} err='{(stderr ?? string.Empty).Trim()}'");
                    return process.ExitCode == 0 ? stdout : null;
                }
            }
            catch (Exception ex)
            {
                Diag.Log($"RunGit EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }
}
