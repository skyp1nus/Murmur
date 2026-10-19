using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Intelligence;
using Murmur.Core.Processing;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

public enum LibraryFilter
{
    Today,
    Week,
    All,
}

/// <summary>The Library screen: sessions from <see cref="ISessionStore"/>, a date filter and the pill search.</summary>
/// <remarks>
/// <para><b>Search.</b> Without a transcript search the pill filters the list by title. With one (an
/// <see cref="IAssistant"/> and/or an <see cref="ISearchIndex"/>) the list is replaced by search results: title matches
/// first, then transcript hits (title, time chip, snippet), restricted to the date filter. The query runs
/// <see cref="SearchDebounce"/> after the last keystroke; a newer query cancels the older one. Hybrid search goes
/// through <see cref="IAssistant.SearchAsync"/> (which itself drops to BM25 when the embedding model is missing or AI
/// is paused); when the assistant is missing or throws, <see cref="ISearchIndex.SearchKeywordAsync"/> answers.</para>
/// </remarks>
public sealed partial class LibraryViewModel : ObservableObject, IDisposable
{
    /// <summary>Quiet time after the last keystroke before the transcript search runs.</summary>
    public static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>How often rows still shown as "recording" are re-read from the store while the library is open.</summary>
    public static readonly TimeSpan StaleRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>Transcript hits per query.</summary>
    public const int SearchLimit = 30;

    private readonly ISessionStore _store;
    private readonly TimeProvider _time;
    private readonly ISessionProcessing? _processing;
    private readonly ISearchIndex? _keywordSearch;
    private readonly IAssistant? _assistant;
    private readonly Action<Action> _post;
    private readonly IRecorder? _recorder;
    private readonly Dictionary<SessionId, SessionItemViewModel> _rows = [];
    private IReadOnlyList<SessionRecord> _loaded = [];
    private int _version;
    private CancellationTokenSource? _search;
    private bool _searchCompleted;
    private ITimer? _staleTimer;
    private int _staleRunning;
    private bool _disposed;

    /// <param name="processing">Pipeline status for the rows' badges; <c>null</c> = badges come from the stored state only.</param>
    /// <param name="post">Marshals a callback to the UI thread; pipeline events arrive on background threads.</param>
    /// <param name="keywordSearch">BM25 transcript search (the fallback of <paramref name="assistant"/>).</param>
    /// <param name="assistant">Hybrid transcript search.</param>
    /// <param name="recorder">
    /// Tells which session is being recorded right now; a stored session still in the "recording" state that is not that one
    /// was left behind (a crash) and may be fixed by a recovery running in the background, so it is re-read now and then.
    /// </param>
    public LibraryViewModel(
        ISessionStore store,
        TimeProvider time,
        ISessionProcessing? processing = null,
        Action<Action>? post = null,
        ISearchIndex? keywordSearch = null,
        IAssistant? assistant = null,
        IRecorder? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _time = time;
        _processing = processing;
        _keywordSearch = keywordSearch;
        _assistant = assistant;
        _recorder = recorder;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        if (processing is not null)
        {
            processing.ProgressChanged += OnProcessingChanged;
        }
    }

    /// <summary>Rows currently shown. Replaced wholesale on every change (one notification, cheap for a virtualised list).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SessionItemViewModel> Sessions { get; set; } = [];

    [ObservableProperty]
    public partial LibraryFilter Filter { get; set; } = LibraryFilter.All;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>True until the first load has finished, so the empty state does not flash on start.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The row the user clicked. Setting it opens the session and clears the selection again, so a row can be clicked twice.</summary>
    [ObservableProperty]
    public partial SessionItemViewModel? SelectedSession { get; set; }

    /// <summary>Transcript search results while <see cref="ShowSearchResults"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchResults))]
    [NotifyPropertyChangedFor(nameof(ShowNoSearchResults))]
    public partial IReadOnlyList<SearchResultViewModel> SearchResults { get; private set; } = [];

    /// <summary>A transcript search is waiting for its debounce or running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSearchResults))]
    [NotifyPropertyChangedFor(nameof(SearchStatusText))]
    [NotifyPropertyChangedFor(nameof(HasSearchStatus))]
    public partial bool IsSearching { get; private set; }

    /// <summary>Why the last search failed, or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatusText))]
    [NotifyPropertyChangedFor(nameof(HasSearchStatus))]
    public partial string? SearchError { get; private set; }

    /// <summary>The result the user clicked; setting it opens the session and clears the selection again.</summary>
    [ObservableProperty]
    public partial SearchResultViewModel? SelectedSearchResult { get; set; }

    /// <summary>Shows "Запитати AI по всіх записах" (the shell sets it when it can open a library-wide chat).</summary>
    [ObservableProperty]
    public partial bool CanAskAll { get; set; }

    /// <summary>Raised when the user asks for a new recording.</summary>
    public event Action? NewRecordingRequested;

    /// <summary>Raised when the user opens a session row.</summary>
    public event Action<SessionRecord>? SessionOpened;

    /// <summary>Raised when the user opens a search result: the session, and where to start (<c>null</c> = a title match).</summary>
    public event Action<SessionRecord, TimeSpan?>? SearchResultOpened;

    /// <summary>Raised by "Запитати AI по всіх записах".</summary>
    public event Action? AskAllRequested;

    /// <summary>The last started load (for tests that need to await a filter change).</summary>
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The last started transcript search, debounce included (for tests).</summary>
    internal Task SearchTask { get; private set; } = Task.CompletedTask;

    public bool ShowEmptyState => !IsLoading && !ShowSearchResults && Sessions.Count == 0;

    public bool HasSessions => Sessions.Count > 0;

    /// <summary>The session list is on screen (there are rows and no search replaces them).</summary>
    public bool ShowSessionList => HasSessions && !ShowSearchResults;

    public bool HasError => Error is not null;

    /// <summary>The pill searches transcripts, not only titles.</summary>
    public bool HasTranscriptSearch => _keywordSearch is not null || _assistant is not null;

    /// <summary>The search results replace the session list.</summary>
    public bool ShowSearchResults => HasTranscriptSearch && !string.IsNullOrWhiteSpace(SearchText);

    public bool HasSearchResults => ShowSearchResults && SearchResults.Count > 0;

    /// <summary>The search finished and found nothing.</summary>
    public bool ShowNoSearchResults => ShowSearchResults && _searchCompleted && !IsSearching && SearchError is null && SearchResults.Count == 0;

    /// <summary>"Шукаю…" or the search error while results are shown; <c>null</c> otherwise.</summary>
    public string? SearchStatusText => !ShowSearchResults ? null : SearchError ?? (IsSearching ? Strings.Library_Searching : null);

    public bool HasSearchStatus => SearchStatusText is not null;

    /// <summary>The phase-0 empty state, or a hint when a filter or search hides everything.</summary>
    public string EmptyTitle => IsFiltered ? Strings.Library_NothingFound : Strings.Library_EmptyTitle;

    public string EmptyText => IsFiltered
        ? Strings.Library_EmptyHintFiltered
        : Strings.Library_EmptyHint;

    public bool IsTodayFilter
    {
        get => Filter == LibraryFilter.Today;
        set => SelectFilter(LibraryFilter.Today, value, nameof(IsTodayFilter));
    }

    public bool IsWeekFilter
    {
        get => Filter == LibraryFilter.Week;
        set => SelectFilter(LibraryFilter.Week, value, nameof(IsWeekFilter));
    }

    public bool IsAllFilter
    {
        get => Filter == LibraryFilter.All;
        set => SelectFilter(LibraryFilter.All, value, nameof(IsAllFilter));
    }

    private bool IsFiltered => Filter != LibraryFilter.All || !string.IsNullOrWhiteSpace(SearchText) || _loaded.Count > 0;

    /// <summary>Loads the sessions of the current filter from the store.</summary>
    public async Task LoadAsync()
    {
        var version = ++_version;
        IsLoading = true;
        try
        {
            var records = await _store.ListSessionsAsync(CreateQuery(), CancellationToken.None);
            if (version != _version)
            {
                return;
            }

            _loaded = records;
            Error = null;
        }
        catch (Exception ex)
        {
            if (version != _version)
            {
                return;
            }

            _loaded = [];
            Error = UiText.Format(Strings.Library_LoadFailed, ex.Message);
        }

        IsLoading = false;
        ApplySearch();
        UpdateStaleTimer();
        if (ShowSearchResults)
        {
            // The date filter (or the data) changed under an active search: refresh it right away.
            StartTranscriptSearch(debounce: false);
        }
    }

    /// <summary>The last background re-read of stale rows (for tests).</summary>
    internal Task StaleRefreshTask { get; private set; } = Task.CompletedTask;

    /// <summary>The window was activated: sessions that were recovered in the background while the user was away show up now.</summary>
    public void OnWindowActivated() => StartStaleRefresh();

    public void Dispose()
    {
        _disposed = true;
        StopStaleTimer();
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
    }

    /// <summary>A session by id: from the loaded rows, else from the store; <c>null</c> when it no longer exists.</summary>
    public async Task<SessionRecord?> FindSessionAsync(SessionId id)
    {
        foreach (var record in _loaded)
        {
            if (record.Id == id)
            {
                return record;
            }
        }

        try
        {
            return await _store.GetSessionAsync(id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    [RelayCommand]
    private void NewRecording() => NewRecordingRequested?.Invoke();

    [RelayCommand]
    private void AskAll() => AskAllRequested?.Invoke();

    [RelayCommand]
    private void SetFilter(LibraryFilter filter) => Filter = filter;

    partial void OnFilterChanged(LibraryFilter value)
    {
        OnPropertyChanged(nameof(IsTodayFilter));
        OnPropertyChanged(nameof(IsWeekFilter));
        OnPropertyChanged(nameof(IsAllFilter));
        LoadTask = LoadAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplySearch();
        RaiseSearchLayoutChanged();
        if (HasTranscriptSearch)
        {
            StartTranscriptSearch(debounce: true);
        }
    }

    partial void OnSelectedSessionChanged(SessionItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedSession = null;
        SessionOpened?.Invoke(value.Record);
    }

    partial void OnSelectedSearchResultChanged(SearchResultViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedSearchResult = null;
        SearchResultOpened?.Invoke(value.Record, value.At);
    }

    private void OnProcessingChanged(SessionProcessingStatus status) => _post(() =>
    {
        if (_rows.TryGetValue(status.Session, out var row))
        {
            row.ApplyProcessing(status);
        }
    });

    partial void OnSessionsChanged(IReadOnlyList<SessionItemViewModel> value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowSessionList));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private void RaiseSearchLayoutChanged()
    {
        OnPropertyChanged(nameof(ShowSearchResults));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(ShowNoSearchResults));
        OnPropertyChanged(nameof(SearchStatusText));
        OnPropertyChanged(nameof(HasSearchStatus));
        OnPropertyChanged(nameof(ShowSessionList));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void SelectFilter(LibraryFilter filter, bool selected, string propertyName)
    {
        if (selected)
        {
            Filter = filter;
        }
        else
        {
            // A toggle button un-checks itself on click; push the real state back so the pill stays checked.
            OnPropertyChanged(propertyName);
        }
    }

    private SessionQuery CreateQuery()
    {
        var now = _time.GetLocalNow();
        switch (Filter)
        {
            case LibraryFilter.Today:
                return new SessionQuery(From: StartOfLocalDay(now.Date));
            case LibraryFilter.Week:
                return new SessionQuery(From: StartOfLocalDay(now.Date.AddDays(-6)));
            default:
                return new SessionQuery();
        }
    }

    private DateTimeOffset StartOfLocalDay(DateTime date)
    {
        var unspecified = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, _time.LocalTimeZone.GetUtcOffset(unspecified));
    }

    private void ApplySearch()
    {
        var needle = SearchText.Trim();
        var rows = new List<SessionItemViewModel>(_loaded.Count);
        _rows.Clear();

        // The pipeline keeps "model missing" as the last status of a session until it is processed again, even after the
        // model was installed in the Settings; the install state is asked afresh here so the badge does not outlive it.
        var asrInstalled = _processing?.IsAsrInstalled ?? true;
        foreach (var record in _loaded)
        {
            if (needle.Length == 0 || record.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                var live = _processing?.GetStatus(record.Id);
                if (asrInstalled && live is { Stage: SessionProcessingStage.ModelMissing })
                {
                    live = null;
                }

                var row = new SessionItemViewModel(record, _time, live);
                rows.Add(row);
                _rows[record.Id] = row;
            }
        }

        Sessions = rows;
    }

    // Rows left in the "recording" state ------------------------------------------------------------------------------

    /// <summary>Stored as still recording while the recorder is not recording them: nobody is writing to them.</summary>
    private bool IsStale(SessionRecord record) =>
        record.State == SessionState.Recording && _recorder?.Current?.Id != record.Id;

    private void UpdateStaleTimer()
    {
        if (_disposed)
        {
            return;
        }

        var any = false;
        foreach (var record in _loaded)
        {
            if (IsStale(record))
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            StopStaleTimer();
        }
        else if (_staleTimer is null)
        {
            _staleTimer = _time.CreateTimer(
                static state => ((LibraryViewModel)state!).OnStaleTimer(),
                this,
                StaleRefreshInterval,
                StaleRefreshInterval);
        }
    }

    private void StopStaleTimer()
    {
        _staleTimer?.Dispose();
        _staleTimer = null;
    }

    private void OnStaleTimer() => _post(StartStaleRefresh);

    private void StartStaleRefresh()
    {
        if (!_disposed && Volatile.Read(ref _staleRunning) == 0)
        {
            StaleRefreshTask = RefreshStaleAsync();
        }
    }

    /// <summary>Re-reads the stale rows from the store and updates the ones that changed, in place.</summary>
    private async Task RefreshStaleAsync()
    {
        if (Interlocked.Exchange(ref _staleRunning, 1) == 1)
        {
            return;
        }

        try
        {
            var version = _version;
            var stale = new List<SessionId>();
            foreach (var record in _loaded)
            {
                if (IsStale(record))
                {
                    stale.Add(record.Id);
                }
            }

            foreach (var id in stale)
            {
                SessionRecord? fresh;
                try
                {
                    fresh = await _store.GetSessionAsync(id, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue;
                }

                if (_disposed || version != _version)
                {
                    continue;
                }

                if (fresh is null)
                {
                    // Deleted behind our back: drop the row instead of polling it forever.
                    RemoveGone(id);
                }
                else if (fresh.State != SessionState.Recording)
                {
                    ApplyFresh(fresh);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _staleRunning, 0);
            UpdateStaleTimer();
        }
    }

    private void RemoveGone(SessionId id)
    {
        _loaded = [.. _loaded.Where(r => r.Id != id)];
        ApplySearch();
    }

    private void ApplyFresh(SessionRecord fresh)
    {
        var updated = new SessionRecord[_loaded.Count];
        for (var i = 0; i < updated.Length; i++)
        {
            updated[i] = _loaded[i].Id == fresh.Id ? fresh : _loaded[i];
        }

        _loaded = updated;
        if (_rows.TryGetValue(fresh.Id, out var row))
        {
            row.Update(fresh, _time, _processing?.GetStatus(fresh.Id));
        }
    }

    // Transcript search ---------------------------------------------------------------------------------------------

    private void StartTranscriptSearch(bool debounce)
    {
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            _searchCompleted = false;
            SearchError = null;
            IsSearching = false;
            SearchResults = [];
            SearchTask = Task.CompletedTask;
            return;
        }

        var search = new CancellationTokenSource();
        _search = search;
        IsSearching = true;
        SearchTask = RunTranscriptSearchAsync(query, debounce, search.Token);
    }

    private async Task RunTranscriptSearchAsync(string query, bool debounce, CancellationToken cancellationToken)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(SearchDebounce, _time, cancellationToken).ConfigureAwait(true);
            }

            string? error = null;
            IReadOnlyList<SearchHit> hits;
            try
            {
                // Off the UI thread: the keyword search is synchronous SQLite work and the query embedding may start llama-server.
                hits = await Task.Run(() => SearchTranscriptsAsync(query, cancellationToken), cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                hits = [];
                error = UiText.Format(Strings.Library_SearchFailed, ex.Message);
            }

            var rows = await BuildResultsAsync(query, hits, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            _searchCompleted = true;
            SearchResults = rows;
            SearchError = error;
            IsSearching = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer query (or a cleared box) took over; it owns IsSearching and the results.
        }
    }

    /// <summary>Hybrid search, or BM25 when there is no assistant or it fails.</summary>
    private async Task<IReadOnlyList<SearchHit>> SearchTranscriptsAsync(string query, CancellationToken cancellationToken)
    {
        if (_assistant is not null)
        {
            try
            {
                return await _assistant.SearchAsync(query, null, SearchLimit, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception) when (_keywordSearch is not null && !cancellationToken.IsCancellationRequested)
            {
                // Fall through to the keyword index: the search box keeps working without the models.
            }
        }

        return await _keywordSearch!.SearchKeywordAsync(query, null, SearchLimit, cancellationToken).ConfigureAwait(true);
    }

    private async Task<IReadOnlyList<SearchResultViewModel>> BuildResultsAsync(string query, IReadOnlyList<SearchHit> hits, CancellationToken cancellationToken)
    {
        var from = CreateQuery().From;
        var rows = new List<SearchResultViewModel>(hits.Count + 4);
        var known = new Dictionary<SessionId, SessionRecord?>();
        foreach (var record in _loaded)
        {
            known[record.Id] = record;
            if (record.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new SearchResultViewModel(record, null, string.Empty, _time));
            }
        }

        foreach (var hit in hits)
        {
            var id = hit.Chunk.Session;
            if (!known.TryGetValue(id, out var record))
            {
                // Outside the loaded page (or deleted since it was indexed).
                try
                {
                    record = await _store.GetSessionAsync(id, cancellationToken).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    record = null;
                }

                known[id] = record;
            }

            if (record is null || (from is { } start && record.StartedAt < start))
            {
                continue;
            }

            rows.Add(new SearchResultViewModel(record, hit.Chunk.Start, hit.Chunk.Text, _time));
        }

        return rows;
    }
}
