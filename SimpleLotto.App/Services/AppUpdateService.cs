using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleLotto.App.Services;

public sealed class AppUpdateService : IDisposable
{
    private const string DefaultManifestUrl = "https://files.k2patel.in/u/SimpleLotto-Windows-Latest.json";
    private const string ManifestSettingKey = "windows_update_manifest_url";

    private readonly LocalStore _store;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private AppUpdateState _state;

    public AppUpdateService(LocalStore store)
    {
        _store = store;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleLotto-Windows-Updater/1.0");
        _state = AppUpdateState.Idle(GetInstalledVersion());
    }

    public AppUpdateState Current => _state;

    public async Task<AppUpdateState> CheckAndDownloadAsync(CancellationToken ct = default)
    {
        SetState(_state with
        {
            Status = AppUpdateStatus.Checking,
            Message = "Checking for upgrade...",
            Error = null
        });

        AppUpdatePackage? package;
        try
        {
            package = await FetchManifestPackageAsync(ct);
        }
        catch (Exception ex)
        {
            return SetState(_state with
            {
                Status = AppUpdateStatus.Failed,
                Message = "Upgrade check failed.",
                Error = ex.Message
            });
        }

        var installed = GetInstalledVersion();
        if (package is null)
        {
            return SetState(AppUpdateState.NotAvailable(installed, "No SimpleLotto installer was found in the update manifest."));
        }

        if (IsSameVersion(installed, package.Version))
        {
            return SetState(AppUpdateState.UpToDate(installed, package));
        }

        var cached = await TryUseCachedInstallerAsync(installed, package, ct);
        if (cached is not null)
            return cached;

        return await DownloadAsync(installed, package, ct);
    }

    private async Task<AppUpdateState?> TryUseCachedInstallerAsync(string installed, AppUpdatePackage package, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdateDownloadDirectory);
        var target = Path.Combine(UpdateDownloadDirectory, SafeInstallerFileName(package));
        if (!File.Exists(target))
            return null;

        if (package.Sha256 is { Length: > 0 })
        {
            var actual = await ComputeSha256Async(target, ct);
            if (!string.Equals(actual, NormalizeSha256(package.Sha256), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                return null;
            }
        }

        return SetState(new AppUpdateState(
            AppUpdateStatus.Downloaded,
            installed,
            package,
            target,
            $"Upgrade {package.Version} already downloaded.",
            null));
    }

    private async Task<AppUpdateState> DownloadAsync(string installed, AppUpdatePackage package, CancellationToken ct)
    {
        SetState(new AppUpdateState(
            AppUpdateStatus.Downloading,
            installed,
            package,
            null,
            $"Downloading upgrade {package.Version}...",
            null));

        try
        {
            Directory.CreateDirectory(UpdateDownloadDirectory);
            var target = Path.Combine(UpdateDownloadDirectory, SafeInstallerFileName(package));
            var temp = target + ".tmp";

            using (var response = await _http.GetAsync(package.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                await source.CopyToAsync(destination, ct);
            }

            if (package.Sha256 is { Length: > 0 })
            {
                var actual = await ComputeSha256Async(temp, ct);
                if (!string.Equals(actual, NormalizeSha256(package.Sha256), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Installer checksum mismatch. Expected {package.Sha256}, got {actual}.");
            }

            if (File.Exists(target))
                File.Delete(target);
            File.Move(temp, target);
            CleanupDownloadDirectory(target);

            return SetState(new AppUpdateState(
                AppUpdateStatus.Downloaded,
                installed,
                package,
                target,
                $"Upgrade {package.Version} downloaded.",
                null));
        }
        catch (Exception ex)
        {
            return SetState(_state with
            {
                Status = AppUpdateStatus.Failed,
                Message = "Upgrade download failed.",
                Error = ex.Message
            });
        }
    }

    public AppUpgradeLaunchResult LaunchDownloadedInstaller()
    {
        if (_state.Status != AppUpdateStatus.Downloaded ||
            string.IsNullOrWhiteSpace(_state.InstallerPath))
        {
            return new AppUpgradeLaunchResult(false, "No downloaded installer is ready to apply.");
        }

        if (!File.Exists(_state.InstallerPath))
            return new AppUpgradeLaunchResult(false, $"Installer file is missing: {_state.InstallerPath}");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = QuoteProcessArgument(_state.InstallerPath),
                UseShellExecute = true
            });
            return new AppUpgradeLaunchResult(true, null);
        }
        catch (Exception ex)
        {
            return new AppUpgradeLaunchResult(false, ex.Message);
        }
    }

    private async Task<AppUpdatePackage?> FetchManifestPackageAsync(CancellationToken ct)
    {
        var manifestUrl = ReadSetting(ManifestSettingKey);
        if (string.IsNullOrWhiteSpace(manifestUrl))
            manifestUrl = DefaultManifestUrl;

        using var response = await _http.GetAsync(manifestUrl, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = GetString(root, "version");
        var installerUrl = GetString(root, "installer_url");
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(installerUrl))
            return null;

        return new AppUpdatePackage(
            version,
            installerUrl,
            NormalizeSha256(GetString(root, "sha256")),
            GetString(root, "file_name"),
            GetString(root, "release_notes_url"),
            GetString(root, "published_at"),
            manifestUrl);
    }

    private string ReadSetting(string key)
    {
        try
        {
            var state = _store.Load();
            return state.Settings.TryGetValue(key, out var value) ? value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SafeInstallerFileName(AppUpdatePackage package)
    {
        var name = string.IsNullOrWhiteSpace(package.FileName)
            ? $"SimpleLotto-{package.Version}.exe"
            : package.FileName;
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    private static void CleanupDownloadDirectory(string keepPath)
    {
        var keepFullPath = Path.GetFullPath(keepPath);
        foreach (var path in Directory.EnumerateFiles(UpdateDownloadDirectory, "SimpleLotto-*.exe"))
        {
            if (!string.Equals(Path.GetFullPath(path), keepFullPath, StringComparison.OrdinalIgnoreCase))
                File.Delete(path);
        }

        foreach (var path in Directory.EnumerateFiles(UpdateDownloadDirectory, "*.tmp"))
            File.Delete(path);
    }

    public static string GetInstalledVersion()
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty;
        var versionFile = Path.Combine(appDir, "version.txt");
        return File.Exists(versionFile)
            ? File.ReadAllText(versionFile).Trim()
            : "dev";
    }

    public static string UpdateDownloadDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleLotto", "updates");

    private static bool IsSameVersion(string installed, string candidate) =>
        !string.Equals(installed, "dev", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(installed.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeSha256(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var value = raw.Trim();
        return value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? value["sha256:".Length..]
            : value;
    }

    private static string QuoteProcessArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    private AppUpdateState SetState(AppUpdateState state)
    {
        _state = state;
        return state;
    }

    public void Dispose() => _http.Dispose();
}

public enum AppUpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Downloading,
    Downloaded,
    NotAvailable,
    Failed
}

public sealed record AppUpdatePackage(
    string Version,
    string InstallerUrl,
    string? Sha256,
    string? FileName,
    string? ReleaseNotesUrl,
    string? PublishedAt,
    string Source);

public sealed record AppUpdateState(
    AppUpdateStatus Status,
    string InstalledVersion,
    AppUpdatePackage? Package,
    string? InstallerPath,
    string Message,
    string? Error)
{
    public static AppUpdateState Idle(string installed) =>
        new(AppUpdateStatus.Idle, installed, null, null, "No upgrade check has run.", null);

    public static AppUpdateState NotAvailable(string installed, string message) =>
        new(AppUpdateStatus.NotAvailable, installed, null, null, message, null);

    public static AppUpdateState UpToDate(string installed, AppUpdatePackage package) =>
        new(AppUpdateStatus.UpToDate, installed, package, null, $"SimpleLotto is current ({installed}).", null);
}

public sealed record AppUpgradeLaunchResult(bool IsSuccess, string? ErrorMessage);
