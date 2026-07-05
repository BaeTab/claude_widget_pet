using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Claude_Widget.Services
{
    /// <summary>
    /// Detects running Claude Code CLI processes. Extracted from the window so it can be reused
    /// by both the liveness poll (fast, first-match) and the dashboard's resource sampler
    /// (full pid list). Detection handles the shapes Claude Code ships as:
    ///   • native "claude.exe" launcher (confirmed via image path so the Anthropic desktop
    ///     chat app — also Claude.exe — is not a false positive);
    ///   • raw node/bun/deno running cli.js (confirmed by peeking the command line via the PEB).
    /// </summary>
    public sealed class ClaudeProcessScanner
    {
        // Direct-name matches (kept for any future renamed launcher).
        private static readonly string[] ProcessNames = { "claude-cli" };

        // Substrings in a "claude.exe" image path that confirm it is the Claude Code CLI
        // rather than the Anthropic desktop chat app (installed under %LOCALAPPDATA%\AnthropicClaude).
        private static readonly string[] ImagePathHints =
        {
            "claude-code\\bin", "claude-code/bin",
            "@anthropic-ai\\claude-code", "@anthropic-ai/claude-code",
        };

        // Substrings that, in a process command line, indicate the Claude CLI is running.
        private static readonly string[] CommandLineHints =
        {
            "claude-code", "@anthropic-ai/claude-code",
            "\\claude\\cli.js", "/claude/cli.js",
            "\\.claude\\", "/.claude/", "claude.js",
        };

        private static bool IsScriptRunnerName(string baseName) =>
            baseName is "node" or "bun" or "deno";

        /// <summary>
        /// Returns the pids of matching Claude Code processes. When <paramref name="collectAll"/>
        /// is false, stops at the first match (cheap — for the 2-second liveness poll); when true,
        /// returns every match (for aggregate resource sampling).
        /// </summary>
        public List<int> Scan(bool collectAll, int excludePid)
        {
            var pids = new List<int>();
            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    bool hit = false;
                    try
                    {
                        if (proc.Id == excludePid)
                            continue;

                        string baseName = proc.ProcessName.ToLowerInvariant();

                        if (ProcessNames.Contains(baseName))
                        {
                            hit = true;
                        }
                        else if (baseName == "claude")
                        {
                            string? imagePath = TryGetProcessImagePath(proc.Id);
                            if (imagePath == null)
                            {
                                DebugLog($"  claude.exe pid={proc.Id} image-read failed (win32err={Marshal.GetLastWin32Error()})");
                                continue;
                            }
                            DebugLog($"  claude.exe pid={proc.Id} image={imagePath}");
                            foreach (var hint in ImagePathHints)
                            {
                                if (imagePath.Contains(hint, StringComparison.OrdinalIgnoreCase))
                                {
                                    hit = true;
                                    break;
                                }
                            }
                            if (!hit)
                                continue;
                        }
                        else if (IsScriptRunnerName(baseName))
                        {
                            string? cmd = TryReadCommandLineFromPeb(proc.Id, out string pebReason);
                            if (cmd == null)
                            {
                                DebugLog($"  peb-read failed pid={proc.Id} name={proc.ProcessName} reason={pebReason}");
                                continue;
                            }
                            DebugLog($"  candidate pid={proc.Id} name={proc.ProcessName} cmd={cmd}");
                            foreach (var hint in CommandLineHints)
                            {
                                if (cmd.Contains(hint, StringComparison.OrdinalIgnoreCase))
                                {
                                    hit = true;
                                    break;
                                }
                            }
                            if (!hit)
                                continue;
                        }
                        else
                        {
                            continue;
                        }

                        pids.Add(proc.Id);
                        if (!collectAll)
                            break;
                    }
                    catch { /* skip inaccessible processes */ }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"ENUM ERROR: {ex.GetType().Name}: {ex.Message}");
            }

            DebugLog($"scan: collectAll={collectAll} matches={pids.Count}");
            return pids;
        }

        // --- Debug tracing (off by default; would balloon %TEMP% if left on since it runs
        //     on every poll tick). Bounded rotation guards the file if flipped on. ---
        private const bool EnableDebugLog = false;
        private const long MaxDebugLogBytes = 1 * 1024 * 1024;
        private static readonly string DebugLogPath =
            Path.Combine(Path.GetTempPath(), "ClaudeWidget.log");

        private static void DebugLog(string message)
        {
            if (!EnableDebugLog)
                return;
            try
            {
                try
                {
                    var info = new FileInfo(DebugLogPath);
                    if (info.Exists && info.Length > MaxDebugLogBytes)
                        File.Delete(DebugLogPath);
                }
                catch { /* ignore rotation errors */ }

                File.AppendAllText(DebugLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { /* never let logging break the app */ }
        }

        // --- Win32 P/Invoke for reading a process's command line / image path.
        // We read the PEB directly instead of WMI's Win32_Process.CommandLine, which returns
        // empty under some Windows 11 / .NET System.Management configurations. ---
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_VM_READ = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle, int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(
            IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        /// <summary>
        /// Full image path (.exe) for a process using only PROCESS_QUERY_LIMITED_INFORMATION,
        /// which avoids the ACCESS_DENIED problems that PROCESS_VM_READ-based walks hit.
        /// </summary>
        private static string? TryGetProcessImagePath(int pid)
        {
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
                return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint capacity = (uint)sb.Capacity;
                if (!QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
                    return null;
                return sb.ToString();
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// Reads the CommandLine of another process by walking its PEB. Returns null on failure;
        /// <paramref name="reason"/> carries a short diagnostic tag for logging.
        /// </summary>
        private static string? TryReadCommandLineFromPeb(int pid, out string reason)
        {
            reason = string.Empty;
            IntPtr hProcess = OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                reason = $"OpenProcess(win32err={Marshal.GetLastWin32Error()})";
                return null;
            }

            try
            {
                var pbi = new PROCESS_BASIC_INFORMATION();
                int status = NtQueryInformationProcess(
                    hProcess, 0, ref pbi,
                    Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
                if (status != 0)
                {
                    reason = $"NtQueryInfo(ntstatus=0x{status:X8})";
                    return null;
                }
                if (pbi.PebBaseAddress == IntPtr.Zero)
                {
                    reason = "PebBaseAddress=null (likely WOW64 mismatch)";
                    return null;
                }

                // PEB layout (x64): ProcessParameters pointer lives at offset 0x20.
                if (!TryReadPointer(hProcess, pbi.PebBaseAddress + 0x20, out IntPtr pp))
                {
                    reason = $"RPM(PEB+0x20 err={Marshal.GetLastWin32Error()})";
                    return null;
                }
                if (pp == IntPtr.Zero)
                {
                    reason = "ProcessParameters=null";
                    return null;
                }

                // RTL_USER_PROCESS_PARAMETERS (x64): CommandLine UNICODE_STRING at offset 0x70.
                // UNICODE_STRING { USHORT Length; USHORT MaxLength; pad; PWSTR Buffer; } = 16 bytes on x64.
                byte[] usBuf = new byte[16];
                if (!ReadProcessMemory(hProcess, pp + 0x70, usBuf, (IntPtr)usBuf.Length, out _))
                {
                    reason = $"RPM(PP+0x70 err={Marshal.GetLastWin32Error()})";
                    return null;
                }

                ushort length = BitConverter.ToUInt16(usBuf, 0);
                if (length == 0)
                    return string.Empty;
                IntPtr bufPtr = new IntPtr(BitConverter.ToInt64(usBuf, 8));
                if (bufPtr == IntPtr.Zero)
                {
                    reason = "CmdLine.Buffer=null";
                    return null;
                }

                byte[] strBuf = new byte[length];
                if (!ReadProcessMemory(hProcess, bufPtr, strBuf, (IntPtr)length, out _))
                {
                    reason = $"RPM(CmdLine.Buffer err={Marshal.GetLastWin32Error()})";
                    return null;
                }

                return Encoding.Unicode.GetString(strBuf);
            }
            catch (Exception ex)
            {
                reason = $"ex:{ex.GetType().Name}:{ex.Message}";
                return null;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        private static bool TryReadPointer(IntPtr hProcess, IntPtr addr, out IntPtr value)
        {
            byte[] buf = new byte[IntPtr.Size];
            if (!ReadProcessMemory(hProcess, addr, buf, (IntPtr)buf.Length, out _))
            {
                value = IntPtr.Zero;
                return false;
            }
            value = IntPtr.Size == 8
                ? new IntPtr(BitConverter.ToInt64(buf, 0))
                : new IntPtr(BitConverter.ToInt32(buf, 0));
            return true;
        }
    }
}
