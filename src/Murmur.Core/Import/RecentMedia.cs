namespace Murmur.Core.Import;

/// <summary>A media file found by <see cref="RecentMedia.Find"/>: its full path, size and last write time.</summary>
public sealed record RecentMediaFile(string Path, long SizeBytes, DateTimeOffset LastWriteTime);

/// <summary>The newest media files of a folder (the "Recent in Downloads" list of the import dialog).</summary>
public static class RecentMedia
{
    /// <summary>
    /// The newest files directly in <paramref name="folder"/> (no recursion) whose lower-case extension is in <paramref name="extensions"/>,
    /// written within <paramref name="window"/> before <paramref name="now"/>, newest first, at most <paramref name="max"/>.
    /// Missing or unreadable folder or files → empty / skipped, never throws (except for cancellation).
    /// </summary>
    /// <remarks>
    /// Files the system hides (hidden and system attributes, dot files on Unix) are left out, as Explorer does; a file written after
    /// <paramref name="now"/> (a clock that is ahead) is outside the window. Files written at the same time are ordered by path, so the
    /// result does not depend on the order the folder lists them in. Blocks on the file system: call it off the UI thread.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<RecentMediaFile> Find(string folder, IReadOnlySet<string> extensions, DateTimeOffset now,
        TimeSpan window, int max, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(extensions);
        cancellationToken.ThrowIfCancellationRequested();
        if (max <= 0 || window < TimeSpan.Zero || extensions.Count == 0 || string.IsNullOrWhiteSpace(folder))
        {
            return [];
        }

        var since = now - window;
        var found = new List<RecentMediaFile>();
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!extensions.Contains(CompositeMediaDecoder.ExtensionOf(file.Name)) || Read(file) is not { } entry)
                {
                    continue;
                }

                if (entry.LastWriteTime >= since && entry.LastWriteTime <= now)
                {
                    found.Add(entry);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            // The folder is missing, is not a folder or cannot be listed (a DirectoryNotFoundException is an IOException): what was found
            // before the error is kept.
        }

        found.Sort(static (a, b) =>
        {
            var byTime = b.LastWriteTime.CompareTo(a.LastWriteTime);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Path, b.Path);
        });
        if (found.Count > max)
        {
            found.RemoveRange(max, found.Count - max);
        }

        return found;
    }

    /// <summary>The size and last write time of <paramref name="file"/>, or <c>null</c> when they cannot be read (gone, a broken link, no access).</summary>
    private static RecentMediaFile? Read(FileInfo file)
    {
        try
        {
            // A symbolic link is listed under its own name with the size and time of its target: a link whose target is gone is no
            // file to import.
            var details = file;
            if (file.LinkTarget is not null)
            {
                if (file.ResolveLinkTarget(returnFinalTarget: true) is not FileInfo { Exists: true } target)
                {
                    return null;
                }

                details = target;
            }

            if (!details.Exists)
            {
                return null;
            }

            var written = new DateTimeOffset(details.LastWriteTimeUtc, TimeSpan.Zero);
            return new RecentMediaFile(file.FullName, details.Length, written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
