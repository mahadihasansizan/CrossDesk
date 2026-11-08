using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DeskRelay;

sealed record CrossDeskUpdate(Version Version, string DownloadUrl)
{
    const string Repository = "mahadihasansizan/CrossDesk";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";

    public static async Task<CrossDeskUpdate?> CheckAsync(Version current, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var info = new ProcessStartInfo("gh.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        foreach (string argument in new[] { "api", "--hostname", "github.com", $"repos/{Repository}/releases/latest" })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not open GitHub CLI.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(timeout.Token);
        await error;
        if (process.ExitCode != 0)
            throw new IOException("GitHub is unreachable or the signed-in GitHub CLI account cannot read the private release.");

        using var doc = JsonDocument.Parse(await output);
        var release = doc.RootElement;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0)
            throw new IOException("The GitHub release has an unsupported version tag.");
        if (version <= new Version(current.Major, current.Minor, current.Build)) return null;

        string installer = $"CrossDesk-Setup-{version}-x64.exe";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("name").GetString() == installer)
                return new CrossDeskUpdate(version, $"{ReleasesUrl}/download/v{version}/{installer}");

        throw new IOException("The latest release is missing its Windows installer.");
    }

    public static void Open(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
