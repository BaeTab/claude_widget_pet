using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Claude_Widget.Services
{
    /// <summary>Describes a newer release found on GitHub.</summary>
    public sealed class UpdateInfo
    {
        public required Version Version { get; init; }
        public required string TagName { get; init; }
        public required string DownloadUrl { get; init; }
        public required string AssetName { get; init; }
        public string ReleaseNotes { get; init; } = "";
        public long AssetSize { get; init; }
    }

    /// <summary>
    /// Release-based updater. Queries the public GitHub Releases API, compares the
    /// latest release tag (vX.Y.Z) against the running assembly version, and — when
    /// newer — downloads the "ClaudeWidget_Setup_*.exe" asset and launches it silently.
    ///
    /// The installer (Inno Setup, per-user) reinstalls over the running app; we hand off
    /// to it with /SILENT and shut ourselves down so no files are locked.
    /// </summary>
    public static class UpdateService
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // GitHub requires a User-Agent on every API request.
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("ClaudeWidget", AppInfo.CurrentVersionString));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        /// <summary>
        /// Returns update info if a newer release exists, otherwise null.
        /// Never throws — network/parse failures resolve to null.
        /// </summary>
        public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
        {
            try
            {
                string url = $"https://api.github.com/repos/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepo}/releases/latest";
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return null;

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var root = doc.RootElement;

                // Ignore drafts / prereleases.
                if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                    return null;
                if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                    return null;

                if (!root.TryGetProperty("tag_name", out var tagEl))
                    return null;
                string tag = tagEl.GetString() ?? "";
                if (!TryParseVersion(tag, out Version? latest) || latest == null)
                    return null;

                if (latest <= AppInfo.CurrentVersion)
                    return null;

                // Find the Inno Setup installer asset.
                if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.StartsWith("ClaudeWidget_Setup_", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        string dl = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(dl))
                            continue;
                        long size = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

                        return new UpdateInfo
                        {
                            Version = latest,
                            TagName = tag,
                            DownloadUrl = dl,
                            AssetName = name,
                            AssetSize = size,
                            ReleaseNotes = notes,
                        };
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Downloads the installer to a temp file, reporting 0..1 progress.
        /// Returns the local path, or null on failure.
        /// </summary>
        public static async Task<string?> DownloadInstallerAsync(
            UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "ClaudeWidgetUpdate");
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, info.AssetName);

                using var resp = await Http.GetAsync(
                    info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                long total = resp.Content.Headers.ContentLength ?? info.AssetSize;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0)
                        progress?.Report(Math.Clamp((double)read / total, 0, 1));
                }
                progress?.Report(1);
                return dest;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Launches the downloaded installer silently and requests app shutdown.
        /// The installer relaunches the app when finished (setup.iss postinstall Run entry).
        /// </summary>
        public static bool LaunchInstaller(string installerPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    // /SILENT shows only a progress bar; installer restarts the app afterwards.
                    Arguments = "/SILENT /NOCANCEL /SP-",
                    UseShellExecute = true,
                };
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Parses "v1.2.3" / "1.2.3" / "v1.2" into a 3-part Version. Returns false if unparseable.
        /// </summary>
        public static bool TryParseVersion(string tag, out Version? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag))
                return false;

            string t = tag.Trim();
            if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(1);

            // Keep only the leading numeric-dotted portion (drop any -beta suffix etc.)
            int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0)
                t = t.Substring(0, cut);

            var parts = t.Split('.').Where(p => int.TryParse(p, out _)).Select(int.Parse).ToArray();
            if (parts.Length == 0)
                return false;

            int major = parts.Length > 0 ? parts[0] : 0;
            int minor = parts.Length > 1 ? parts[1] : 0;
            int build = parts.Length > 2 ? parts[2] : 0;
            version = new Version(major, minor, build);
            return true;
        }
    }
}
