using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitForTfs.Services
{
    /// <summary>The blame of a single line: who last touched it and when.</summary>
    internal sealed class LineBlame
    {
        public string Author { get; set; }
        public string ShortHash { get; set; }
        public string Summary { get; set; }
        public string RelativeDate { get; set; }
        public bool IsUncommitted { get; set; }

        /// <summary>The one-line label shown at the end of the current editor line.</summary>
        public string InlineText =>
            string.IsNullOrEmpty(Summary)
                ? $"{Author}, {RelativeDate}"
                : $"{Author}, {RelativeDate} • {Summary}";
    }

    /// <summary>
    /// Runs <c>git blame</c> for a single line, entirely off the UI thread. Used by the
    /// current-line blame adornment (an in-process editor component), so it must not depend
    /// on any Visual Studio service — it only shells out to <c>git.exe</c>.
    /// </summary>
    internal static class GitLineBlame
    {
        private static readonly ConcurrentDictionary<string, bool> RepoMembershipCache =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cheap, process-free check for whether a file lives in a git working tree.</summary>
        public static bool IsInRepository(string filePath)
        {
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

        public static async Task<LineBlame> GetLineBlameAsync(string filePath, int line, CancellationToken cancellationToken)
        {
            var dir = SafeGetDirectory(filePath);
            if (dir == null || line < 1)
                return null;

            var args = string.Format(
                CultureInfo.InvariantCulture,
                "--no-pager blame --porcelain -L {0},{0} -- \"{1}\"",
                line, filePath);

            var output = await RunGitAsync(args, dir, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(output))
                return null;

            return Parse(output);
        }

        // ------------------------------------------------------------------

        private static LineBlame Parse(string porcelain)
        {
            string hash = null, author = null, summary = null;
            long time = 0;

            using (var reader = new StringReader(porcelain))
            {
                string line;
                var first = true;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '\t')
                        continue;

                    var space = line.IndexOf(' ');
                    var key = space < 0 ? line : line.Substring(0, space);
                    var value = space < 0 ? string.Empty : line.Substring(space + 1);

                    if (first)
                    {
                        hash = key; // the first porcelain line starts with the commit hash
                        first = false;
                        continue;
                    }

                    switch (key)
                    {
                        case "author": author = value; break;
                        case "committer-time":
                            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out time);
                            break;
                        case "summary": summary = value; break;
                    }
                }
            }

            if (string.IsNullOrEmpty(hash))
                return null;

            var uncommitted = hash.Replace("0", string.Empty).Length == 0;

            return new LineBlame
            {
                Author = uncommitted ? "You" : (author ?? "Unknown"),
                ShortHash = uncommitted ? "0000000" : hash.Substring(0, Math.Min(7, hash.Length)),
                Summary = uncommitted ? string.Empty : (summary ?? string.Empty),
                RelativeDate = uncommitted ? "uncommitted" : ToRelativeDate(time),
                IsUncommitted = uncommitted,
            };
        }

        private static string ToRelativeDate(long unixSeconds)
        {
            if (unixSeconds <= 0)
                return string.Empty;

            var delta = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            if (delta.TotalSeconds < 60) return "just now";
            if (delta.TotalMinutes < 60) return Plural((int)delta.TotalMinutes, "minute");
            if (delta.TotalHours < 24) return Plural((int)delta.TotalHours, "hour");
            if (delta.TotalDays < 30) return Plural((int)delta.TotalDays, "day");
            if (delta.TotalDays < 365) return Plural((int)(delta.TotalDays / 30), "month");
            return Plural((int)(delta.TotalDays / 365), "year");
        }

        private static string Plural(int value, string unit)
        {
            if (value <= 0) value = 1;
            return value + " " + unit + (value == 1 ? "" : "s") + " ago";
        }

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
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();

                    var exitTcs = new TaskCompletionSource<object>();
                    process.Exited += (s, e) => exitTcs.TrySetResult(null);
                    if (process.HasExited)
                        exitTcs.TrySetResult(null);

                    using (cancellationToken.Register(() => exitTcs.TrySetCanceled()))
                    {
                        await exitTcs.Task.ConfigureAwait(false);
                    }

                    var stdout = await stdoutTask.ConfigureAwait(false);
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
