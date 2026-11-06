namespace Murmur.Core.Import;

/// <summary>
/// Several decoders behind one: a file goes to the first decoder (in constructor order) whose
/// <see cref="IMediaDecoder.Extensions"/> contain its extension (compared case-insensitively). <see cref="Extensions"/> is
/// the union of them all.
/// </summary>
public sealed class CompositeMediaDecoder : IMediaDecoder
{
    private readonly IMediaDecoder[] _decoders;

    public CompositeMediaDecoder(params IReadOnlyList<IMediaDecoder> decoders)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        _decoders = [.. decoders];
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var decoder in _decoders)
        {
            ArgumentNullException.ThrowIfNull(decoder);
            extensions.UnionWith(decoder.Extensions);
        }

        Extensions = extensions;
    }

    public IReadOnlySet<string> Extensions { get; }

    /// <exception cref="NotSupportedException">No decoder takes the file's extension.</exception>
    public ValueTask<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken) =>
        For(path).ProbeAsync(path, cancellationToken);

    /// <exception cref="NotSupportedException">No decoder takes the file's extension.</exception>
    public ValueTask DecodeAudioAsync(string path, string destinationWav, IProgress<double>? progress, CancellationToken cancellationToken) =>
        For(path).DecodeAudioAsync(path, destinationWav, progress, cancellationToken);

    /// <summary>The lower-case extension of <paramref name="path"/> with its dot, or an empty string.</summary>
    public static string ExtensionOf(string path) => Path.GetExtension(path).ToLowerInvariant();

    private IMediaDecoder For(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var extension = ExtensionOf(path);
        foreach (var decoder in _decoders)
        {
            if (decoder.Extensions.Contains(extension))
            {
                return decoder;
            }
        }

        throw new NotSupportedException($"No decoder for '{extension}' files.");
    }
}
