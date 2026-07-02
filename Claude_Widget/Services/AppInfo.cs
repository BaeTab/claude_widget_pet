using System;
using System.IO;
using System.Reflection;

namespace Claude_Widget.Services
{
    /// <summary>
    /// Central app identity/constants shared by the services.
    /// </summary>
    public static class AppInfo
    {
        public const string GitHubOwner = "BaeTab";
        public const string GitHubRepo = "claude_widget_pet";

        public const string ProductName = "Claude Widget";

        /// <summary>Registry value name used for the "start with Windows" entry.</summary>
        public const string StartupValueName = "ClaudeWidget";

        /// <summary>Running assembly version (e.g. 1.1.0). Patch component trimmed to 3 parts.</summary>
        public static Version CurrentVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
            }
        }

        public static string CurrentVersionString => CurrentVersion.ToString();

        /// <summary>Per-user data folder: %APPDATA%\ClaudeWidget</summary>
        public static string DataDirectory
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ClaudeWidget");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ReleasesUrl =>
            $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases";
    }
}
