namespace Murmur.Models;

/// <summary>Knobs of <see cref="ModelManager"/>; everything but <see cref="ModelsDirectory"/> has a production default.</summary>
public sealed class ModelManagerOptions
{
    public required string ModelsDirectory { get; init; }

    /// <summary>Items to offer; defaults to <see cref="ModelCatalog.ForCurrentOs"/>.</summary>
    public IReadOnlyList<ModelCatalogItem>? Catalog { get; init; }

    /// <summary>
    /// The client to download with. Default: one shared client with the system proxy and no overall timeout (a stall
    /// timeout applies instead). A supplied client is not disposed by the manager.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Free bytes on the drive of the given directory; defaults to <see cref="DriveInfo"/>.</summary>
    public Func<string, long>? FreeSpaceProbe { get; init; }

    /// <summary>Physical memory in bytes; defaults to <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>.</summary>
    public Func<long>? TotalMemoryProbe { get; init; }

    /// <summary>Waits between retries of a transient failure; its length is the retry count. Default 2 s, 4 s, 8 s.</summary>
    public IReadOnlyList<TimeSpan>? RetryDelays { get; init; }

    /// <summary>A request or a read without any data for this long counts as a transient failure.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Minimum time between two progress events of one item.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Compute the SHA-256 of every downloaded file even when the catalog does not pin it (see <see cref="ModelManager.GetLastInstallReport"/>).</summary>
    public bool ReportHashes { get; init; }

    /// <summary>Test seam: called on the worker thread right before a removal starts deleting.</summary>
    internal Action<string>? BeforeRemove { get; init; }
}

/// <summary>A downloaded file, as recorded by the last install of an item.</summary>
/// <param name="Sha256">Lower-case hex; <c>null</c> unless the catalog pins it or <see cref="ModelManagerOptions.ReportHashes"/> is set.</param>
public sealed record ModelFileReport(string Url, string RelativePath, long Bytes, string? Sha256);

/// <summary>One source that was asked about a file.</summary>
/// <param name="Error">Why it was skipped (e.g. "HTTP 404"), or <c>null</c> when it answered.</param>
public sealed record ModelCandidateProbe(string Url, long? ContentLength, string? Error);

/// <summary>Result of <see cref="ModelManager.ProbeAsync"/>: the source that answered first.</summary>
/// <param name="Url">The source that answered.</param>
/// <param name="ContentLength">The file size the server reports, or <c>null</c> when it does not say.</param>
/// <param name="Tried">Every source asked up to and including the one that answered, in order.</param>
public sealed record ModelFileProbe(string Url, long? ContentLength, IReadOnlyList<ModelCandidateProbe> Tried);
