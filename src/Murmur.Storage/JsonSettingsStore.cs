using System.Text.Json;
using Murmur.Core.Settings;

namespace Murmur.Storage;

/// <summary>
/// <see cref="ISettingsStore"/> backed by <c>settings.json</c> in the data folder (<see cref="StoragePaths"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The file is read once, in the constructor. Properties missing from it take the defaults of
/// <see cref="AppSettings"/>, unknown ones are ignored, so the file can grow and shrink between versions.</item>
/// <item>A file that is not valid JSON (or does not hold a settings object) is renamed to <c>settings.json.bad</c>, and
/// the defaults are used until the next save. A file that cannot be read at all (locked, no permission) is left
/// untouched and the defaults are used.</item>
/// <item>A save writes a temporary file next to the target and replaces the target with it, so a crash never leaves a
/// half-written <c>settings.json</c>.</item>
/// <item>Thread-safe: saves are serialized, <see cref="Current"/> can be read from any thread. <see cref="Changed"/> is
/// raised on the thread that called <see cref="SaveAsync"/> (or a pool thread after its awaits), after the file is
/// written and <see cref="Current"/> is updated, and outside of the lock; an exception from a handler reaches the caller
/// of <see cref="SaveAsync"/>.</item>
/// </list>
/// </remarks>
public sealed class JsonSettingsStore : ISettingsStore
{
    private const string BadSuffix = ".bad";
    private const string TempSuffix = ".tmp";

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    // Same options as the [JsonSourceGenerationOptions] of the context, plus relaxed escaping so "Ctrl+Shift+R" stays readable.
    private static readonly SettingsJsonContext Context = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<AppSettings> Json => Context.AppSettings;

    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile AppSettings _current;

    public JsonSettingsStore(StoragePaths paths)
        : this(ArgumentNullGuard(paths).SettingsPath)
    {
    }

    /// <param name="filePath">The settings file; its folder is created on the first save.</param>
    public JsonSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _path = Path.GetFullPath(filePath);
        _current = Load(_path);
    }

    /// <inheritdoc />
    public event Action<AppSettings>? Changed;

    /// <inheritdoc />
    public AppSettings Current => _current;

    /// <summary>Full path of the settings file.</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public async ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, Json);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);

            // Unique temp name: a leftover of a crashed save must not clash with this one.
            var temp = _path + "." + Guid.NewGuid().ToString("N") + TempSuffix;
            try
            {
                await File.WriteAllBytesAsync(temp, json, cancellationToken).ConfigureAwait(false);
                if (File.Exists(_path))
                {
                    File.Replace(temp, _path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temp, _path);
                }
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            _current = settings;
        }
        finally
        {
            _writeLock.Release();
        }

        Changed?.Invoke(settings);
    }

    private static AppSettings Load(string path)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }

        try
        {
            // Notepad and friends add a UTF-8 byte order mark; Utf8JsonReader rejects it.
            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith(Utf8Bom))
            {
                json = json[Utf8Bom.Length..];
            }

            return JsonSerializer.Deserialize(json, Json)
                ?? throw new JsonException("settings.json holds null instead of an object.");
        }
        catch (JsonException)
        {
            MoveAside(path);
            return new AppSettings();
        }
    }

    private static void MoveAside(string path)
    {
        try
        {
            File.Move(path, path + BadSuffix, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Could not keep the broken file; the next save replaces it anyway.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray temp file is harmless.
        }
    }

    private static StoragePaths ArgumentNullGuard(StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths;
    }
}
