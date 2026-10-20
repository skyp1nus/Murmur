using System.Diagnostics;
using System.Text;
using Murmur.Core.Models;
using Murmur.Models;

namespace Murmur.ModelFetch;

/// <summary>Installs and probes catalog items for CI and prints what the catalog needs to pin (size, SHA-256).</summary>
internal static class FetchRunner
{
    /// <returns>0 when everything worked, 1 when an item failed, 2 for a usage error.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        ModelCatalogSources sources,
        CancellationToken cancellationToken,
        PinEndpoints? endpoints = null,
        HttpClient? http = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        if (args.Length > 0 && args[0] is "--help" or "-h" or "-?")
        {
            await output.WriteLineAsync(FetchOptions.Usage);
            return 0;
        }

        var options = FetchOptions.Parse(args, out var problem);
        if (options is null)
        {
            await error.WriteLineAsync($"{problem}\n{FetchOptions.Usage}");
            return 2;
        }

        var variable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var summary = new StringBuilder();
        var failures = 0;

        if (options.ResolvePins)
        {
            failures += await ResolvePinsAsync(sources, endpoints ?? PinEndpoints.Default, http, variable, output, summary, cancellationToken);
        }

        if (options.Install.Count > 0 || options.HeadOnly.Count > 0)
        {
            var code = await InstallAndProbeAsync(options, sources, output, error, summary, cancellationToken);
            if (code < 0)
            {
                return 2;
            }

            failures += code;
        }

        if ((options.SummaryFile ?? (options.ResolvePins ? variable("GITHUB_STEP_SUMMARY") : null)) is { Length: > 0 } summaryFile)
        {
            await File.AppendAllTextAsync(summaryFile, summary.ToString(), cancellationToken);
        }

        await output.WriteLineAsync(failures == 0 ? "All items OK." : $"{failures} item(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    /// <returns>The number of failures that <c>--resolve-pins</c> found (sources that could not be resolved); drift is reported, not counted.</returns>
    private static async Task<int> ResolvePinsAsync(
        ModelCatalogSources sources,
        PinEndpoints endpoints,
        HttpClient? http,
        Func<string, string?> variable,
        TextWriter output,
        StringBuilder summary,
        CancellationToken cancellationToken)
    {
        // Both platforms: the Windows runtime archive is not pinned yet, the build agent is Linux.
        var items = ModelCatalog.Create(sources, ModelPlatform.Linux)
            .Concat(ModelCatalog.Create(sources, ModelPlatform.Windows).Where(i => i.Info.Kind == ModelKind.LlmRuntime))
            .ToList();

        using var ownedClient = http is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(2) } : null;
        var resolver = new PinResolver(http ?? ownedClient!, sources, endpoints, variable("GITHUB_TOKEN"), variable("HF_TOKEN"));
        var rows = await resolver.ResolveAsync(items, cancellationToken);

        var report = PinReport.Render(rows);
        await output.WriteLineAsync(report);
        summary.AppendLine(report);

        foreach (var drift in rows.Where(r => r.Status == "DRIFT"))
        {
            await output.WriteLineAsync($"::warning::pin drift in {drift.Item} ({drift.Source}, {drift.File}): {drift.Note}");
        }

        foreach (var broken in rows.Where(r => r.Status == "ERROR"))
        {
            await output.WriteLineAsync($"[{broken.Item}] {broken.Source} {broken.File}: could not resolve ({broken.Note})");
        }

        return rows.Count(r => r.Status == "ERROR");
    }

    /// <returns>The number of failed items, or -1 for a usage error (already reported).</returns>
    private static async Task<int> InstallAndProbeAsync(FetchOptions options, ModelCatalogSources sources, TextWriter output, TextWriter error, StringBuilder summary, CancellationToken cancellationToken)
    {
        using var manager = new ModelManager(new ModelManagerOptions
        {
            ModelsDirectory = options.Directory!,
            Catalog = ModelCatalog.Create(sources, ModelCatalog.CurrentPlatform()),
            ReportHashes = true,
        });

        var known = manager.Items.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = options.Install.Concat(options.HeadOnly).Where(id => !known.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            await error.WriteLineAsync($"Unknown id(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", known)}");
            return -1;
        }

        var failures = 0;

        if (options.Install.Count > 0)
        {
            summary.AppendLine("### Installed items").AppendLine();
            summary.AppendLine("| Item | File | Size (bytes) | SHA-256 | Elapsed |").AppendLine("|---|---|---:|---|---:|");
            foreach (var id in options.Install)
            {
                failures += await InstallAsync(manager, id, output, summary, cancellationToken) ? 0 : 1;
            }

            summary.AppendLine();
        }

        if (options.HeadOnly.Count > 0)
        {
            summary.AppendLine("### Reachability (no download)").AppendLine();
            summary.AppendLine("| Item | URL | Content-Length |").AppendLine("|---|---|---:|");
            foreach (var id in options.HeadOnly)
            {
                failures += await ProbeAsync(manager, id, output, summary, cancellationToken) ? 0 : 1;
            }

            summary.AppendLine();
        }

        return failures;
    }

    private static async Task<bool> InstallAsync(ModelManager manager, string id, TextWriter output, StringBuilder summary, CancellationToken cancellationToken)
    {
        var lastPrinted = Stopwatch.GetTimestamp();
        manager.StatusChanged += OnStatus;
        var started = Stopwatch.StartNew();
        try
        {
            await output.WriteLineAsync($"[{id}] installing");
            await manager.InstallAsync(id, cancellationToken);
            started.Stop();

            var report = manager.GetLastInstallReport(id);
            if (report.Count == 0)
            {
                await output.WriteLineAsync($"[{id}] already installed, nothing downloaded");
                summary.AppendLine($"| `{id}` | (already installed) | | | |");
                return true;
            }

            foreach (var file in report)
            {
                await output.WriteLineAsync($"[{id}] {file.RelativePath}: {file.Bytes} bytes, sha256 {file.Sha256}, {started.Elapsed.TotalSeconds:0.0} s");
                summary.AppendLine($"| `{id}` | `{file.RelativePath}` | {file.Bytes} | `{file.Sha256}` | {started.Elapsed.TotalSeconds:0.0} s |");
            }

            return true;
        }
        catch (ModelInstallException ex)
        {
            await output.WriteLineAsync($"[{id}] FAILED: {ex.Message}");
            summary.AppendLine($"| `{id}` | FAILED: {ex.Message.Replace("|", "\\|", StringComparison.Ordinal)} | | | {started.Elapsed.TotalSeconds:0.0} s |");
            return false;
        }
        finally
        {
            manager.StatusChanged -= OnStatus;
        }

        void OnStatus(ModelStatus status)
        {
            if (status.State != ModelInstallState.Downloading || Stopwatch.GetElapsedTime(lastPrinted) < TimeSpan.FromSeconds(10))
            {
                return;
            }

            lastPrinted = Stopwatch.GetTimestamp();
            lock (output)
            {
                output.WriteLine($"[{id}] {status.BytesDone / 1_000_000} / {(status.BytesTotal is { } total ? (total / 1_000_000).ToString() : "?")} MB");
            }
        }
    }

    private static async Task<bool> ProbeAsync(ModelManager manager, string id, TextWriter output, StringBuilder summary, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var probe in await manager.ProbeAsync(id, cancellationToken))
            {
                foreach (var skipped in probe.Tried.Where(t => t.Error is not null))
                {
                    await output.WriteLineAsync($"[{id}] skipped {skipped.Url}: {skipped.Error}");
                    summary.AppendLine($"| `{id}` | {skipped.Url} (skipped: {skipped.Error!.Replace("|", "\\|", StringComparison.Ordinal)}) | |");
                }

                var length = probe.ContentLength?.ToString() ?? "unknown";
                await output.WriteLineAsync($"[{id}] {probe.Url}: Content-Length {length}");
                summary.AppendLine($"| `{id}` | {probe.Url} | {length} |");
            }

            return true;
        }
        catch (ModelInstallException ex)
        {
            await output.WriteLineAsync($"[{id}] UNREACHABLE: {ex.Message}");
            summary.AppendLine($"| `{id}` | UNREACHABLE: {ex.Message.Replace("|", "\\|", StringComparison.Ordinal)} | |");
            return false;
        }
    }
}
