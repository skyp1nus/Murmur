using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Murmur.App.Services;

/// <summary>Lets the user choose a folder.</summary>
public interface IFolderPicker
{
    /// <summary>The chosen local folder, or <c>null</c> when the dialog was cancelled or the folder is not a local path.</summary>
    Task<string?> PickFolderAsync(string title, string? startPath);
}

/// <summary><see cref="IFolderPicker"/> over the storage provider of the window the settings are shown in.</summary>
public sealed class TopLevelFolderPicker(Func<TopLevel?> topLevel) : IFolderPicker
{
    public async Task<string?> PickFolderAsync(string title, string? startPath)
    {
        if (topLevel() is not { } top)
        {
            return null;
        }

        var provider = top.StorageProvider;
        if (!provider.CanPickFolder)
        {
            return null;
        }

        // The storage items wrap OS handles (COM objects on Windows): dispose every one that was handed out.
        IStorageFolder? start = null;
        IReadOnlyList<IStorageFolder>? result = null;
        try
        {
            if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
            {
                start = await provider.TryGetFolderFromPathAsync(startPath);
            }

            result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });

            return result.Count > 0 ? result[0].TryGetLocalPath() : null;
        }
        finally
        {
            start?.Dispose();
            if (result is not null)
            {
                foreach (var folder in result)
                {
                    folder.Dispose();
                }
            }
        }
    }
}
