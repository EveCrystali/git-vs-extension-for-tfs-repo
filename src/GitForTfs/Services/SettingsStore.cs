using System;
using System.IO;

namespace GitForTfs.Services
{
    /// <summary>
    /// Tiny file-based persistence for the last repository the user pointed the tool window at,
    /// keyed by solution so that switching solutions restores the right git root.
    /// Stored under <c>%LOCALAPPDATA%\GitForTfs</c>.
    /// </summary>
    public static class SettingsStore
    {
        private static string SettingsDirectory
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GitForTfs");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string RepositoryPathFile => Path.Combine(SettingsDirectory, "last-repository.txt");

        public static string LoadRepositoryPath()
        {
            try
            {
                if (!File.Exists(RepositoryPathFile))
                    return null;

                var path = File.ReadAllText(RepositoryPathFile).Trim();
                return Directory.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        public static void SaveRepositoryPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                    return;

                File.WriteAllText(RepositoryPathFile, path);
            }
            catch
            {
                // Persistence is a convenience, never fatal.
            }
        }
    }
}
