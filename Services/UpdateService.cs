using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MouseUtil.Services;

/// <summary>
/// Result of a single CheckForUpdateAsync call. DownloadUrl is null both when no update is
/// available and when an update IS available but its release has no .exe asset yet (today's
/// releases only publish a .zip) - callers distinguish those two cases via IsUpdateAvailable, not
/// DownloadUrl, and fall back to opening ReleaseUrl in the browser when DownloadUrl is null.
/// </summary>
public sealed class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; init; }
    public string LatestVersion { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
    public string? DownloadUrl { get; init; }
}

/// <summary>
/// Checks GitHub Releases for a newer MouseUtil build, and downloads/launches the installer asset
/// when one is found. Plain instance class (not static like ConfigService) so MainWindow can own its
/// HttpClient lifetime the same way it owns _hotkeyService/_trayIconService/etc.
/// </summary>
public sealed class UpdateService
{
    private const string ReleasesUrl = "https://api.github.com/repos/MouseUtil/MouseUtil/releases/latest";

    private readonly HttpClient _httpClient = new();

    public UpdateService()
    {
        // GitHub's API rejects requests with no User-Agent header outright.
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MouseUtil-UpdateChecker", null));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// Reads the running exe's file version - this app is unpackaged (no Package.Current to query),
    /// so FileVersionInfo on the entry assembly's own path is the unpackaged equivalent. Shared by
    /// both SettingsPanel's manual "Check for updates" button and MainWindow's launch-time auto-check
    /// (see MainWindow.InitializeAutoUpdateCheck), so there's exactly one place that knows how to
    /// read "the current version" regardless of which UI surface triggered the check.
    /// </summary>
    public static string GetCurrentVersionString()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
        return !string.IsNullOrWhiteSpace(fileVersion) ? fileVersion : assembly.GetName().Version?.ToString() ?? "unknown";
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(string currentVersion, CancellationToken ct)
    {
        var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(ReleasesUrl, ct)
            ?? throw new HttpRequestException("Empty response from GitHub releases API.");

        var latestVersionText = release.TagName.StartsWith('v') ? release.TagName[1..] : release.TagName;
        var latestVersion = Version.Parse(latestVersionText);
        var current = Version.Parse(currentVersion);

        var isUpdateAvailable = latestVersion > current;
        var exeAsset = release.Assets.Find(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        return new UpdateCheckResult
        {
            IsUpdateAvailable = isUpdateAvailable,
            LatestVersion = latestVersionText,
            ReleaseUrl = release.HtmlUrl,
            DownloadUrl = isUpdateAvailable ? exeAsset?.BrowserDownloadUrl : null
        };
    }

    public async Task<string> DownloadInstallerAsync(string url, CancellationToken ct)
    {
        var fileName = url[(url.LastIndexOf('/') + 1)..];
        var localPath = Path.Combine(Path.GetTempPath(), fileName);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var fileStream = File.Create(localPath);
        await response.Content.CopyToAsync(fileStream, ct);

        return localPath;
    }

    /// <summary>
    /// Runs the installer with its normal interactive wizard (no /SILENT) - deliberate, not an
    /// oversight: this app isn't code-signed, so an unattended install has a real (if reduced) chance
    /// of tripping SmartScreen/AV, and the app has already closed itself by the time that could
    /// happen (see the caller) - a visible wizard gives the user context if anything goes wrong,
    /// where a silent install would just leave them looking at nothing. Does not exit the app - the
    /// caller does that immediately after via the _isClosingConfirmed/Close() pattern, letting the
    /// installer overwrite files this process is no longer holding open.
    /// </summary>
    public void LaunchInstaller(string installerPath)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath)
        {
            UseShellExecute = true
        });
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
