namespace Murmur.App.Services;

/// <summary>Free space of the drive a folder lives on.</summary>
internal static class DiskSpace
{
    /// <summary>
    /// Free bytes on the drive of <paramref name="directory"/>, which may not exist yet (the nearest existing parent is asked);
    /// <c>null</c> when it cannot be told.
    /// </summary>
    public static long? TryGetFree(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            var path = Path.GetFullPath(directory);
            while (!Directory.Exists(path))
            {
                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent))
                {
                    break;
                }

                path = parent;
            }

            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
