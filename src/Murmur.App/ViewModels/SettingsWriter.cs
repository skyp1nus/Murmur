using Murmur.App.Resources;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>
/// The one way the settings screen changes <see cref="AppSettings"/>: every change reads the current settings, applies one
/// edit and saves at once (there is no Save button). Edits run one after another, so two quick changes never overwrite
/// each other, whatever thread the store raises its events on.
/// </summary>
internal sealed class SettingsWriter
{
    private readonly ISettingsStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISettingsStore, SettingsWriter> Shared = new();

    /// <summary>
    /// The one writer of a settings store: every screen that changes the settings goes through it, so two edits made by different
    /// screens at nearly the same time are applied one after the other and never overwrite each other.
    /// </summary>
    public static SettingsWriter For(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Shared.GetValue(store, static s => new SettingsWriter(s));
    }

    public SettingsWriter(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public AppSettings Current => _store.Current;

    /// <summary>The last change started (for tests and for callers that must wait for the file).</summary>
    public Task LastSave { get; private set; } = Task.CompletedTask;

    /// <summary>Applies <paramref name="edit"/> to the current settings and saves; resolves to the inline error text, or <c>null</c> when saved.</summary>
    public Task<string?> UpdateAsync(Func<AppSettings, AppSettings> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var save = UpdateCoreAsync(edit);
        LastSave = save;
        return save;
    }

    private async Task<string?> UpdateCoreAsync(Func<AppSettings, AppSettings> edit)
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            var next = edit(_store.Current);
            if (next != _store.Current)
            {
                await _store.SaveAsync(next, CancellationToken.None).ConfigureAwait(true);
            }

            return null;
        }
        catch (Exception ex)
        {
            return UiText.Format(Strings.Settings_SaveFailed, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }
}
