using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Skippy.Core;

public sealed record UpdateInfo(Version Version, string Tag, string ZipUrl, string? Sha256, long Size, string Notes);

/// <summary>
/// "Check for updates": reads the latest GitHub release of the repo baked into the build
/// (UpdateRepo in Skippy.csproj), downloads its zip, verifies the SHA-256 GitHub publishes for it, and
/// lets the new version copy itself over this install once this process has exited. Nothing runs in the
/// background: it only talks to GitHub when the button is pressed.
/// </summary>
public static class Updater
{
    const string ApplyArg = "--apply-update";
    /// <summary>The new build is started with this first; it only answers <see cref="ProbeExitCode"/> if Windows let it load.</summary>
    public const string ProbeArg = "--probe";
    public const int ProbeExitCode = 42;
    public const string UpdatedArg = "--updated";

    static readonly Lazy<HttpClient> HttpLazy = new(CreateClient);   // lazy: needs Current, and must not exist in apply mode
    static HttpClient Http => HttpLazy.Value;

    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>"owner/repo", or null when this build has no update source configured.</summary>
    public static string? Repo { get; } = Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepo")?.Value is { Length: > 2 } r ? r : null;

    static string WorkDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skippy", "update");

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Skippy/{Current}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Returns the newer release, or null if this is the latest.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (Repo == null) throw new InvalidOperationException("This build has no update source configured.");
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no releases published yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) throw new InvalidOperationException($"Unexpected release tag '{tag}'.");
        v = Normalize(v);
        if (v <= Current) return null;

        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } ds && ds.StartsWith("sha256:") ? ds[7..] : null;
            return new UpdateInfo(v, tag, a.GetProperty("browser_download_url").GetString()!, sha, a.GetProperty("size").GetInt64(),
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
        }
        throw new InvalidOperationException($"Release {tag} has no Windows zip attached.");
    }

    /// <summary>
    /// Downloads and verifies the update, then starts the new version in "apply" mode. The caller must exit
    /// right after this returns; the new version waits for that, copies itself over, and restarts Skippy.
    /// </summary>
    public static async Task DownloadAndStartInstallAsync(UpdateInfo u, IProgress<double> progress, CancellationToken ct = default)
    {
        try { if (Directory.Exists(WorkDir)) Directory.Delete(WorkDir, true); } catch { }
        Directory.CreateDirectory(WorkDir);
        string zip = Path.Combine(WorkDir, $"Skippy-{u.Version}.zip");

        using (var resp = await Http.GetAsync(u.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? u.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }

        if (u.Sha256 != null)
        {
            await using var f = File.OpenRead(zip);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
            if (!actual.Equals(u.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The download is corrupted (checksum mismatch). Try again.");
        }
        else Log.Warn("Release has no published checksum; skipping verification");

        string dir = Path.Combine(WorkDir, $"v{u.Version}");
        ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
        string? exe = Directory.GetFiles(dir, "Skippy.exe", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();
        if (exe == null) throw new InvalidOperationException("The update package doesn't contain Skippy.exe.");

        // Make sure Windows will actually run the new build before we quit. Smart App Control judges every
        // unsigned file by reputation and may block a fresh release; then we stay on the current version.
        const string blocked = "Windows blocked the new version (Smart App Control doesn't know it yet). You're still on the current version; try again in a day or two.";
        Process probe;
        try { probe = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { ProbeArg } })!; }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warn($"Update probe could not start: {ex.Message}"); throw new InvalidOperationException(blocked); }
        using (probe)
        {
            if (!probe.WaitForExit(20_000)) { try { probe.Kill(); } catch { } }
            if (!probe.HasExited || probe.ExitCode != ProbeExitCode) throw new InvalidOperationException(blocked);
        }

        string installDir = AppContext.BaseDirectory.TrimEnd('\\');
        Log.Info($"Update {u.Tag} downloaded and verified; handing over to {exe} to install into {installDir}");
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            ArgumentList = { ApplyArg, installDir, Environment.ProcessId.ToString() },
        });
    }

    public static bool IsApplyMode(string[] args) => args.Length == 3 && args[0] == ApplyArg;

    /// <summary>Runs inside the downloaded version: wait for the old Skippy to exit, copy over it, restart.</summary>
    public static int Apply(string[] args)
    {
        string installDir = args[1];
        string target = Path.Combine(installDir, "Skippy.exe");
        try
        {
            try { Process.GetProcessById(int.Parse(args[2])).WaitForExit(30_000); } catch (ArgumentException) { }
            string from = AppContext.BaseDirectory.TrimEnd('\\');
            Log.Info($"Installing update {Current} from {from} into {installDir}");
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(installDir, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Copy(file, dest, overwrite: true); break; }
                    catch (IOException) when (attempt < 20) { Thread.Sleep(250); }   // old process still releasing files
                }
            }
            Log.Info("Update installed");
        }
        catch (Exception ex)
        {
            Log.Error("Installing the update failed; restarting the previous version", ex);
        }
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = false, ArgumentList = { Autostart.Arg, UpdatedArg }, WorkingDirectory = installDir });
        return 0;
    }

    /// <summary>Removes the downloaded package once the updated app is running from its normal folder.</summary>
    public static void CleanUp()
    {
        if (AppContext.BaseDirectory.StartsWith(WorkDir, StringComparison.OrdinalIgnoreCase)) return;
        try { if (Directory.Exists(WorkDir)) Directory.Delete(WorkDir, true); } catch { }
    }
}
