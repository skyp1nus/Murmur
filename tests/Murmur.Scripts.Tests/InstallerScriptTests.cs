using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Murmur.Scripts.Tests;

/// <summary>
/// The installer sources (<c>installer/Murmur.iss</c>) must agree with the rest of the repository, and
/// <c>scripts/build-installer.ps1 -DryRun</c> (PowerShell 7; skipped when <c>pwsh</c> is not on PATH, set
/// <c>MURMUR_PWSH</c>) resolves the version, checks the publish folder and describes the payload without Inno Setup or
/// Windows. The installer itself is only proven by the Windows CI job <c>installer</c>.
/// </summary>
public sealed partial class InstallerScriptTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "murmur-installer-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [GeneratedRegex(@"^AppId=\{\{([0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12})\}\s*$", RegexOptions.Multiline)]
    private static partial Regex AppIdLine();

    [GeneratedRegex(@"#define\s+MinWindowsVersion\s+""([^""]+)""")]
    private static partial Regex MinVersionDefine();

    [GeneratedRegex(@"<MurmurWindowsMinVersion>\s*(\d+\.\d+\.\d+)")]
    private static partial Regex MinVersionProperty();

    [GeneratedRegex(@"#define\s+AppVersion\s+""([^""]+)""")]
    private static partial Regex AppVersionDefine();

    [GeneratedRegex(@"<Version>([^<]+)</Version>")]
    private static partial Regex VersionProperty();

    [Fact]
    public void The_iss_has_a_fixed_app_id_and_the_per_user_settings()
    {
        var iss = File.ReadAllText(RepoPath("installer", "Murmur.iss"));

        Assert.Matches(AppIdLine(), iss);
        Assert.Contains("PrivilegesRequired=lowest", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivilegesRequiredOverridesAllowed", string.Join('\n', iss.Split('\n').Where(l => !l.TrimStart().StartsWith(';'))), StringComparison.Ordinal);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\Murmur", iss, StringComparison.Ordinal);
        Assert.Contains("ArchitecturesAllowed=x64compatible", iss, StringComparison.Ordinal);
        Assert.Contains("SolidCompression=yes", iss, StringComparison.Ordinal);
        Assert.Contains("Compression=lzma2", iss, StringComparison.Ordinal);
        Assert.Contains("Ukrainian.isl", iss, StringComparison.Ordinal);
    }

    [Fact]
    public void The_iss_is_utf8_with_a_bom_because_it_holds_ukrainian_text()
    {
        var bytes = File.ReadAllBytes(RepoPath("installer", "Murmur.iss"));

        Assert.True(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
    }

    [Fact]
    public void The_iss_defaults_match_the_repository()
    {
        var iss = File.ReadAllText(RepoPath("installer", "Murmur.iss"));
        var props = File.ReadAllText(RepoPath("Directory.Build.props"));
        var csproj = File.ReadAllText(RepoPath("src", "Murmur.Desktop", "Murmur.Desktop.csproj"));

        Assert.Equal(MinVersionProperty().Match(props).Groups[1].Value, MinVersionDefine().Match(iss).Groups[1].Value);
        Assert.Equal(VersionProperty().Match(csproj).Groups[1].Value, AppVersionDefine().Match(iss).Groups[1].Value);
    }

    [Fact]
    public void The_icon_holds_every_size_of_the_orb()
    {
        var ico = File.ReadAllBytes(RepoPath("installer", "murmur.ico"));

        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));
        var count = BitConverter.ToUInt16(ico, 4);
        var sizes = Enumerable.Range(0, count).Select(i => ico[6 + (16 * i)] == 0 ? 256 : ico[6 + (16 * i)]).ToArray();
        Assert.Equal([16, 24, 32, 48, 64, 128, 256], sizes);
    }

    [Fact]
    public void The_dry_run_takes_the_version_from_the_csproj_and_leaves_debug_files_out()
    {
        var publish = Publish("Murmur.exe", "Murmur.Transcription.exe", "onnxruntime.dll", "sherpa-onnx-c-api.dll", "Murmur.pdb", "Murmur.Transcription.dbg", "libSkiaSharp.dll", "libSkiaSharp.pdb");

        var run = Run(publish);

        Assert.Equal(0, run.ExitCode);
        var csproj = File.ReadAllText(RepoPath("src", "Murmur.Desktop", "Murmur.Desktop.csproj"));
        var version = VersionProperty().Match(csproj).Groups[1].Value;
        Assert.Contains($"version={version} numeric={version}.0 ", run.Output, StringComparison.Ordinal);
        Assert.Contains("included=5 excluded=3 ", run.Output, StringComparison.Ordinal);
        Assert.Contains($"Murmur-Setup-{version}.exe", run.Output, StringComparison.Ordinal);
        var payload = File.ReadAllText(Path.Combine(_dir, "out", "payload.md"));
        Assert.Contains("| Murmur.pdb | 0.00 | no |", payload, StringComparison.Ordinal);
        Assert.Contains("| Murmur.Transcription.exe | 0.00 | yes |", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prerelease_version_gets_a_numeric_file_version()
    {
        var run = Run(Publish("Murmur.exe", "Murmur.Transcription.exe", "onnxruntime.dll", "sherpa-onnx-c-api.dll"), "-Version", "1.2.3-rc.1");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("version=1.2.3-rc.1 numeric=1.2.3.0 ", run.Output, StringComparison.Ordinal);
        Assert.Contains("Murmur-Setup-1.2.3-rc.1.exe", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_minimum_comes_from_directory_build_props()
    {
        var run = Run(Publish("Murmur.exe", "Murmur.Transcription.exe", "onnxruntime.dll", "sherpa-onnx-c-api.dll"));

        var props = File.ReadAllText(RepoPath("Directory.Build.props"));
        Assert.Contains($"minWindows={MinVersionProperty().Match(props).Groups[1].Value} ", run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Murmur.Transcription.exe")]
    [InlineData("onnxruntime.dll")]
    [InlineData("sherpa-onnx-c-api.dll")]
    [InlineData("Murmur.exe")]
    public void A_publish_folder_without_the_worker_or_its_native_libraries_is_refused(string missing)
    {
        string[] all = ["Murmur.exe", "Murmur.Transcription.exe", "onnxruntime.dll", "sherpa-onnx-c-api.dll"];

        var run = Run(Publish([.. all.Where(f => f != missing)]));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("missing in the publish folder: " + missing, run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3.4")]
    public void A_version_that_is_not_x_y_z_is_refused(string version)
    {
        var run = Run(Publish("Murmur.exe"), "-Version", version);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("is not x.y.z", run.Output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ plumbing

    private sealed record ScriptRun(int ExitCode, string Output);

    private string Publish(params string[] files)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "publish")).FullName;
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(dir, file), "x");
        }

        return dir;
    }

    private ScriptRun Run(string publishDir, params string[] extra)
    {
        var pwsh = FindPwsh();
        Assert.SkipUnless(pwsh is not null, "pwsh (PowerShell 7) is not available; set MURMUR_PWSH to run the script tests.");

        var info = new ProcessStartInfo(pwsh!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string[] arguments = ["-NoProfile", "-NonInteractive", "-File", RepoPath("scripts", "build-installer.ps1"), "-DryRun", "-PublishDir", publishDir, "-OutDir", Path.Combine(_dir, "out"), .. extra];
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("build-installer.ps1 did not finish within 120 s.");
        }

        return new ScriptRun(process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    private static string RepoPath(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Murmur.sln")))
            {
                return Path.Combine([dir.FullName, .. parts]);
            }
        }

        throw new FileNotFoundException("Murmur.sln not found above " + AppContext.BaseDirectory);
    }

    private static string? FindPwsh()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MURMUR_PWSH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe" } : new[] { "pwsh" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
