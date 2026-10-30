using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Murmur.App.Services;

/// <summary>Lets the user choose where to save a text file (the export of a recording) and writes it.</summary>
public interface IFileSaver
{
    /// <summary>
    /// Asks for a file name and writes <paramref name="content"/> there as UTF-8 (no byte order mark). Returns the chosen file's
    /// name for the message to the user, or <c>null</c> when the dialog was cancelled. Failures to write throw (<see cref="IOException"/>).
    /// </summary>
    /// <param name="title">The title of the dialog.</param>
    /// <param name="suggestedFileName">The name offered, with its extension.</param>
    /// <param name="fileTypeName">The name of the file type in the dialog's filter ("Markdown").</param>
    /// <param name="extension">The extension with the dot (".md"); the dialog adds it when the user leaves it out.</param>
    Task<string?> SaveTextAsync(string title, string suggestedFileName, string fileTypeName, string extension, string content);
}

/// <summary><see cref="IFileSaver"/> over the storage provider of the window the session is shown in.</summary>
public sealed class TopLevelFileSaver(Func<TopLevel?> topLevel) : IFileSaver
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<string?> SaveTextAsync(string title, string suggestedFileName, string fileTypeName, string extension, string content)
    {
        if (topLevel() is not { } top || !top.StorageProvider.CanSave)
        {
            return null;
        }

        var pattern = "*" + extension;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(fileTypeName) { Patterns = [pattern] }],
        });
        if (file is null)
        {
            return null;
        }

        // The storage item wraps an OS handle: dispose it once written.
        using (file)
        {
            var stream = await file.OpenWriteAsync();
            await using (stream)
            {
                // A file that was there is overwritten, not appended to.
                stream.SetLength(0);
                var bytes = Utf8.GetBytes(content);
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }

            return file.Name;
        }
    }
}
