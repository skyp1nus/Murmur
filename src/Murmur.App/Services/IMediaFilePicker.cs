using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Murmur.App.Services;

/// <summary>Lets the user pick media files to import.</summary>
public interface IMediaFilePicker
{
    /// <summary>The chosen local file paths (empty when cancelled; non-local items are skipped).</summary>
    /// <param name="title">The title of the dialog.</param>
    /// <param name="fileTypeName">The name of the filter ("Audio and video").</param>
    /// <param name="extensions">The extensions the filter lets through, with the dot (".wav").</param>
    Task<IReadOnlyList<string>> PickMediaFilesAsync(string title, string fileTypeName, IReadOnlyCollection<string> extensions);
}

/// <summary><see cref="IMediaFilePicker"/> over the storage provider of the window the import dialog is shown in (several files at once).</summary>
public sealed class TopLevelMediaFilePicker(Func<TopLevel?> topLevel) : IMediaFilePicker
{
    public async Task<IReadOnlyList<string>> PickMediaFilesAsync(string title, string fileTypeName, IReadOnlyCollection<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        if (topLevel() is not { } top || !top.StorageProvider.CanOpen)
        {
            return [];
        }

        IReadOnlyList<IStorageFile>? files = null;
        try
        {
            files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
                FileTypeFilter = extensions.Count == 0 ? null : [new FilePickerFileType(fileTypeName) { Patterns = Patterns(extensions) }],
            });

            var paths = new List<string>(files.Count);
            foreach (var file in files)
            {
                // A file from a portal or a cloud provider without a local path cannot be imported.
                if (file.TryGetLocalPath() is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
            }

            return paths;
        }
        finally
        {
            // The storage items wrap OS handles (COM objects on Windows): dispose every one that was handed out.
            if (files is not null)
            {
                foreach (var file in files)
                {
                    file.Dispose();
                }
            }
        }
    }

    /// <summary>"*.wav" for each extension, and "*.WAV" too: the file dialogs of Linux match the patterns case-sensitively.</summary>
    internal static IReadOnlyList<string> Patterns(IReadOnlyCollection<string> extensions)
    {
        var patterns = new List<string>(extensions.Count * 2);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in extensions)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                continue;
            }

            var dotted = extension.StartsWith('.') ? extension : "." + extension;
            foreach (var pattern in (ReadOnlySpan<string>)["*" + dotted.ToLowerInvariant(), "*" + dotted.ToUpperInvariant()])
            {
                if (seen.Add(pattern))
                {
                    patterns.Add(pattern);
                }
            }
        }

        return patterns;
    }
}
