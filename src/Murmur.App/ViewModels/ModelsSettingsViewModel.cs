using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Resources;
using Murmur.Core.Models;

namespace Murmur.App.ViewModels;

/// <summary>
/// The "Моделі" section: one row per item of <see cref="IModelManager.Catalog"/> with install, progress, cancel and remove.
/// </summary>
/// <remarks>
/// <para>The AI items are useful only together, so installing a chat model first installs the llama.cpp runtime and the
/// embedding model when they are missing, one after another; every row shows its own progress.</para>
/// <para>Every install runs with its own token (a child of the lifetime of this view model, i.e. of the app) and its
/// returned task is observed: a failure puts a short text on the row (the details stay in
/// <see cref="ModelStatus.Error"/> and the trace log, never the raw exception text). Manager events arrive on any thread
/// and are marshalled with the injected <c>post</c>.</para>
/// <para>Only one AI bundle runs at a time: a second chat install is refused (its row says to wait) so two bundles never
/// share the tokens of the runtime and embedding rows. Rows of a running bundle cannot be removed.</para>
/// </remarks>
public sealed partial class ModelsSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IModelManager _manager;
    private readonly SettingsWriter _writer;
    private readonly Func<ModelKind, string?>? _activeModel;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, ModelRowViewModel> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bundle> _bundleOf = new(StringComparer.Ordinal);
    private bool _disposed;

    internal ModelsSettingsViewModel(IModelManager manager, SettingsWriter writer, Func<ModelKind, string?>? activeModel, Action<Action> post)
    {
        _manager = manager;
        _writer = writer;
        _activeModel = activeModel;
        _post = post;

        var catalog = manager.Catalog;
        var largestChat = catalog.Where(m => m.Kind == ModelKind.Chat).OrderByDescending(m => m.SizeBytes).Select(m => m.Id).FirstOrDefault();
        var chatCount = catalog.Count(m => m.Kind == ModelKind.Chat);
        var rows = new List<ModelRowViewModel>(catalog.Count);
        foreach (var info in catalog)
        {
            var row = new ModelRowViewModel(info, DescriptionOf(info, info.Id == largestChat && chatCount > 1), this);
            rows.Add(row);
            _byId[info.Id] = row;
        }

        Rows = rows;
        HasAiNote = catalog.Any(m => m.Kind is ModelKind.Chat or ModelKind.LlmRuntime or ModelKind.Embedding);
        FolderText = UiText.Format(Strings.Models_Folder, manager.ModelsDirectory);
        manager.StatusChanged += OnStatusChanged;
        Refresh();
    }

    public IReadOnlyList<ModelRowViewModel> Rows { get; }

    /// <summary>Explains that the AI items are installed together.</summary>
    public bool HasAiNote { get; }

    public string FolderText { get; }

    /// <summary>Free space of the models drive; empty when unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFreeSpace))]
    public partial string FreeSpaceText { get; set; } = string.Empty;

    public bool HasFreeSpace => FreeSpaceText.Length > 0;

    /// <summary>The last install or removal started (for tests); completes when it has finished, failed or was cancelled.</summary>
    internal Task LastOperation { get; private set; } = Task.CompletedTask;

    /// <summary>Re-reads the state of every row and the free space (the screen was opened, or something changed on disk).</summary>
    internal void Refresh()
    {
        foreach (var row in Rows)
        {
            row.Apply(_manager.GetStatus(row.Id));
        }

        RefreshDefaults();
        RefreshFreeSpace();
    }

    /// <summary>The saved chat model changed elsewhere: re-marks the default row.</summary>
    internal void RefreshActive() => RefreshDefaults();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.StatusChanged -= OnStatusChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    internal void Install(ModelRowViewModel row)
    {
        if (_disposed || row.IsBusy || row.IsInstalled)
        {
            return;
        }

        var chain = new List<ModelRowViewModel>();
        if (row.Kind == ModelKind.Chat)
        {
            AddMissing(chain, ModelKind.LlmRuntime);
            AddMissing(chain, ModelKind.Embedding);
            if (AnotherAiInstallRuns(chain))
            {
                // Two bundles would fight over the shared runtime/embedding rows (state, token, bundle map).
                row.Error = Strings.Models_WaitForOther;
                return;
            }
        }

        row.Error = null;
        chain.Add(row);
        var bundle = new Bundle(chain);
        foreach (var item in chain)
        {
            item.MarkQueued();
            _bundleOf[item.Id] = bundle;
        }

        UpdateRemoveLocks();
        LastOperation = RunAsync(bundle);
    }

    internal void Cancel(ModelRowViewModel row)
    {
        if (_bundleOf.TryGetValue(row.Id, out var bundle))
        {
            bundle.Cancelled = true;
            foreach (var item in bundle.Chain)
            {
                if (_tokens.TryGetValue(item.Id, out var cts))
                {
                    cts.Cancel();
                }
            }
        }
        else if (_tokens.TryGetValue(row.Id, out var own))
        {
            own.Cancel();
        }
    }

    internal void Remove(ModelRowViewModel row)
    {
        row.IsConfirmingRemove = false;
        if (_disposed || !row.IsInstalled || IsRemoveLocked(row))
        {
            return;
        }

        LastOperation = RemoveAsync(row);
    }

    internal void MakeDefault(ModelRowViewModel row)
    {
        if (_disposed || !row.IsInstalled || row.Kind != ModelKind.Chat)
        {
            return;
        }

        LastOperation = MakeDefaultAsync(row);
    }

    /// <summary>A bundle with a chat model is running, or an item this chat install would need is already downloading.</summary>
    private bool AnotherAiInstallRuns(List<ModelRowViewModel> missing)
    {
        foreach (var bundle in _bundleOf.Values)
        {
            if (bundle.Chain.Any(r => r.Kind == ModelKind.Chat))
            {
                return true;
            }
        }

        return missing.Any(r => r.IsBusy);
    }

    /// <summary>
    /// A row cannot be removed while it belongs to a running bundle, nor can the shared runtime and embedding model
    /// (installed or not) while a bundle that needs them runs.
    /// </summary>
    private bool IsRemoveLocked(ModelRowViewModel row)
    {
        if (_bundleOf.ContainsKey(row.Id))
        {
            return true;
        }

        return row.Kind is ModelKind.LlmRuntime or ModelKind.Embedding && _bundleOf.Count > 0;
    }

    private void UpdateRemoveLocks()
    {
        foreach (var row in Rows)
        {
            row.IsRemoveLocked = IsRemoveLocked(row);
        }
    }

    private void AddMissing(List<ModelRowViewModel> chain, ModelKind kind)
    {
        foreach (var candidate in Rows)
        {
            if (candidate.Kind == kind && !candidate.IsInstalled)
            {
                chain.Add(candidate);
                return;
            }
        }
    }

    private async Task RunAsync(Bundle bundle)
    {
        ModelRowViewModel? current = null;
        try
        {
            foreach (var item in bundle.Chain)
            {
                current = item;
                if (bundle.Cancelled || _disposed)
                {
                    break;
                }

                var cts = TokenFor(item.Id);
                try
                {
                    await _manager.InstallAsync(item.Id, cts.Token).ConfigureAwait(true);
                }
                finally
                {
                    ReleaseToken(item.Id, cts);
                }

                item.Apply(_manager.GetStatus(item.Id));
                if (item.Kind == ModelKind.Chat)
                {
                    // Installing a chat model makes it the active one on disk; keep the settings in step.
                    if (await _writer.UpdateAsync(s => s with { ChatModelId = item.Id }).ConfigureAwait(true) is { } saveError)
                    {
                        item.Error = saveError;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!bundle.Cancelled && ex is not OperationCanceledException)
            {
                Trace.TraceError("Installing model '{0}' failed: {1}", current?.Id, ex);
                current?.MarkFailed(ex);
            }
        }
        finally
        {
            foreach (var item in bundle.Chain)
            {
                if (_bundleOf.TryGetValue(item.Id, out var registered) && ReferenceEquals(registered, bundle))
                {
                    _bundleOf.Remove(item.Id);
                }

                // Rows that never started (cancelled or an earlier item failed) fall back to what the manager reports.
                if (item.State == ModelRowState.Queued)
                {
                    item.State = ModelRowState.NotInstalled;
                    item.Apply(_manager.GetStatus(item.Id));
                }
            }

            UpdateRemoveLocks();
            RefreshDefaults();
            RefreshFreeSpace();
        }
    }

    private async Task RemoveAsync(ModelRowViewModel row)
    {
        try
        {
            row.Error = null;
            await _manager.RemoveAsync(row.Id, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Removing model '{0}' failed: {1}", row.Id, ex);
            row.Error = Strings.Models_RemoveFailed;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var failed = row.Error;
        row.Apply(_manager.GetStatus(row.Id));
        row.Error ??= failed;
        if (row.Kind == ModelKind.Chat && _writer.Current.ChatModelId == row.Id && !row.IsInstalled)
        {
            if (await _writer.UpdateAsync(s => s with { ChatModelId = null }).ConfigureAwait(true) is { } saveError)
            {
                row.Error = saveError;
            }
        }

        RefreshDefaults();
        RefreshFreeSpace();
    }

    private async Task MakeDefaultAsync(ModelRowViewModel row)
    {
        try
        {
            // An installed item is only (re)activated: no download, no network.
            row.Error = null;
            await _manager.InstallAsync(row.Id, _lifetime.Token).ConfigureAwait(true);
            row.Error = await _writer.UpdateAsync(s => s with { ChatModelId = row.Id }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Activating model '{0}' failed: {1}", row.Id, ex);
            row.Error = Strings.Models_ActionFailed;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var failed = row.Error;
        row.Apply(_manager.GetStatus(row.Id));
        row.Error ??= failed;
        RefreshDefaults();
    }

    private CancellationTokenSource TokenFor(string id)
    {
        if (!_tokens.TryGetValue(id, out var cts))
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _tokens[id] = cts;
        }

        return cts;
    }

    private void ReleaseToken(string id, CancellationTokenSource used)
    {
        if (_tokens.TryGetValue(id, out var registered) && ReferenceEquals(registered, used))
        {
            _tokens.Remove(id);
        }

        // Another bundle may still be waiting on this token (a shared runtime): disposing is safe, a cancelled or
        // finished source only has to stay reachable until the waiters have seen their result.
        if (!_disposed)
        {
            used.Dispose();
        }
    }

    private void OnStatusChanged(ModelStatus status)
    {
        if (_disposed)
        {
            return;
        }

        _post(() =>
        {
            if (_disposed || !_byId.TryGetValue(status.Model.Id, out var row))
            {
                return;
            }

            row.Apply(status);
            if (status.State != ModelInstallState.Downloading)
            {
                RefreshDefaults();
                RefreshFreeSpace();
            }
        });
    }

    /// <summary>Marks the active chat model, or recommends one while none is installed.</summary>
    private void RefreshDefaults()
    {
        var chats = Rows.Where(r => r.Kind == ModelKind.Chat).ToArray();
        string? active = null;
        if (_activeModel?.Invoke(ModelKind.Chat) is { } onDisk && IsInstalledChat(chats, onDisk))
        {
            active = onDisk;
        }
        else if (_writer.Current.ChatModelId is { } saved && IsInstalledChat(chats, saved))
        {
            active = saved;
        }
        else
        {
            active = chats.FirstOrDefault(r => r.IsInstalled)?.Id;
        }

        var anyInstalled = chats.Any(r => r.IsInstalled);
        var recommended = _manager.RecommendedChatModelId;
        foreach (var chat in chats)
        {
            chat.IsDefault = chat.Id == active;
            chat.IsRecommended = !anyInstalled && chat.Id == recommended;
        }
    }

    private static bool IsInstalledChat(ModelRowViewModel[] chats, string id) =>
        chats.Any(r => r.Id == id && r.IsInstalled);

    private void RefreshFreeSpace()
    {
        var free = _manager.AvailableFreeSpaceBytes;
        FreeSpaceText = free is < 0 or long.MaxValue ? string.Empty : UiText.Format(Strings.Models_FreeSpace, UiFormat.Size(free));
    }

    private static string DescriptionOf(ModelInfo info, bool large) => info.Kind switch
    {
        ModelKind.Asr => Strings.Models_DescAsr,
        ModelKind.Vad => Strings.Models_DescVad,
        ModelKind.LlmRuntime => Strings.Models_DescRuntime,
        ModelKind.Embedding => Strings.Models_DescEmbedding,
        _ => large ? Strings.Models_DescChatLarge : Strings.Models_DescChat,
    };

    private sealed class Bundle(IReadOnlyList<ModelRowViewModel> chain)
    {
        public IReadOnlyList<ModelRowViewModel> Chain { get; } = chain;

        public bool Cancelled { get; set; }
    }
}
