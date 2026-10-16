using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fader;

/// <summary>A newer release found on GitHub. <see cref="DownloadUrl"/> is null when it has no exe for this build.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? AssetName, string? DownloadUrl, long Size, string? Sha256);

/// <summary>
/// Updates from the public GitHub releases of LerroUssu/fader. No account, no token: one anonymous
/// API call per check. Install = download the matching exe next to the running one, verify size and
/// SHA-256, rename the running exe aside (Windows allows that), move the new one in, restart.
/// </summary>
public static class Updater
{
    public const string Repo = "LerroUssu/fader";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";

    /// <summary>The portable build carries its own .NET runtime; the small one needs .NET 8 installed. Each updates to its own kind.</summary>
#if SELF_CONTAINED
    public const bool IsSelfContained = true;
#else
    public const bool IsSelfContained = false;
#endif
    public static string AssetSuffix => IsSelfContained ? "-win-x64-portable.exe" : "-win-x64-needs-dotnet8.exe";

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Fader.exe");
    private static string OldPath => ExePath + ".old";
    private static string StagingPath => Path.Combine(Path.GetDirectoryName(ExePath)!, "Fader.update.exe");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Fader/{Current.ToString(3)}");
        return c;
    }

    private static Version Normalize(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOf('-');
        if (dash >= 0) t = t[..dash]; // "1.2.0-beta" → 1.2.0
        if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var v)) return false;
        version = Normalize(v);
        return true;
    }

    /// <summary>Asks GitHub for the latest release. Returns it when it is newer than this build, else null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default, Version? newerThan = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release yet
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return Parse(doc.RootElement, newerThan);
    }

    public static UpdateInfo? Parse(JsonElement release, Version? newerThan = null)
    {
        string? tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!TryParseTag(tag, out var version) || version <= (newerThan ?? Current)) return null;
        if (release.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        string page = release.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;

        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                string? digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                return new UpdateInfo(version, tag!, page, name, a.GetProperty("browser_download_url").GetString(), a.GetProperty("size").GetInt64(), sha);
            }
        }
        return new UpdateInfo(version, tag!, page, null, null, 0, null);
    }

    /// <summary>Downloads the new exe next to the running one and checks it. Returns the staged path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default, string? destination = null)
    {
        if (info.DownloadUrl == null) throw new InvalidOperationException("This release has no download for this build.");
        string dest = destination ?? StagingPath;
        string part = dest + ".part";
        try
        {
            using (var res = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                res.EnsureSuccessStatusCode();
                long total = res.Content.Headers.ContentLength ?? info.Size;
                await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(1.0, done / (double)total));
                }
            }

            var length = new FileInfo(part).Length;
            if (info.Size > 0 && length != info.Size) throw new InvalidDataException($"Download is {length} bytes, expected {info.Size}.");
            if (info.Sha256 != null)
            {
                string actual;
                await using (var f = File.OpenRead(part)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false)).ToLowerInvariant();
                if (actual != info.Sha256) throw new InvalidDataException("Checksum mismatch: the download is damaged or not the published file.");
            }
            File.Move(part, dest, overwrite: true);
            return dest;
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }

    /// <summary>Puts the staged exe in place of <paramref name="exe"/>; the old one is kept as .old until the next start. Rolls back on failure.</summary>
    public static void Swap(string exe, string staged)
    {
        string old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(staged, exe); }
        catch
        {
            try { File.Move(old, exe); } catch { }
            throw;
        }
    }

    /// <summary>Swaps the exe and starts the new one, which waits for this process to exit. The caller then shuts down.</summary>
    public static void InstallAndRestart(string staged, bool quiet)
    {
        string exe = ExePath;
        Swap(exe, staged);
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            psi.ArgumentList.Add("--updated");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            if (quiet) psi.ArgumentList.Add("--quiet");
            Process.Start(psi);
        }
        catch
        {
            // New exe would not start: put the old one back so the next launch still works.
            try { File.Move(exe, staged, overwrite: true); File.Move(OldPath, exe); } catch { }
            throw;
        }
        Log.Write($"[update] installed {staged} → {exe}, restarting");
    }

    /// <summary>On the first start after an update: wait for the previous process, then remove its leftovers.</summary>
    public static void AfterUpdate(int previousPid)
    {
        try { using var p = Process.GetProcessById(previousPid); p.WaitForExit(15000); } catch { } // already gone
    }

    public static void CleanupLeftovers()
    {
        Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (File.Exists(OldPath)) File.Delete(OldPath);
                    if (File.Exists(StagingPath)) File.Delete(StagingPath);
                    return;
                }
                catch { await Task.Delay(1000).ConfigureAwait(false); }
            }
        });
    }
}
