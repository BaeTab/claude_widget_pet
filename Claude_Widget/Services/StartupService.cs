using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Claude_Widget.Services
{
    /// <summary>
    /// Toggles "start with Windows" via the per-user Run key
    /// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run).
    /// Per-user avoids the elevation the installer intentionally does not request.
    /// </summary>
    public static class StartupService
    {
        private const string RunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";

        private static string ExecutablePath =>
            Process.GetCurrentProcess().MainModule?.FileName
            ?? Environment.ProcessPath
            ?? "";

        public static bool IsEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                var value = key?.GetValue(AppInfo.StartupValueName) as string;
                return !string.IsNullOrEmpty(value);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Enables or disables autostart. Returns true on success.</summary>
        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
                    ?? throw new InvalidOperationException("Run key unavailable");

                if (enabled)
                {
                    string exe = ExecutablePath;
                    if (string.IsNullOrEmpty(exe))
                        return false;
                    key.SetValue(AppInfo.StartupValueName, $"\"{exe}\"");
                }
                else
                {
                    if (key.GetValue(AppInfo.StartupValueName) != null)
                        key.DeleteValue(AppInfo.StartupValueName, false);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
