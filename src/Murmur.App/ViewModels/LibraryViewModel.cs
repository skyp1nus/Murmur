using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Intelligence;
using Murmur.Core.Processing;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

public enum LibraryFilter
{
    Today,
    Week,
    All,

    /// <summary>The current calendar month, from its first day.</summary>
    Month,

    /// <summary>The days chosen with "Pick dates" (<see cref="LibraryViewModel.RangeStart"/> to <see cref="LibraryViewModel.RangeEnd"/>), by start date.</summary>
    Range,
}

/// <summary>The sources the New recording card has switched on.</summary>
public readonly record struct RecordingSources(bool Microphone, bool SystemAudio, bool Screen);

/// <summary>The Library screen: the New recording and Ask your library cards, then the sessions from <see cref="ISessionStore"/> by day, a date filter and the pill search.</summary>
/// <remarks>
/// <para><b>Search.</b> Without a transcript search the pill filters the list by title. With one (an
/// <see cref="IAssistant"/> and/or an <see cref="ISearchIndex"/>) the list is replaced by search results: title matches
/// first, then transcript hits (title, time chip, snippet), restricted to the date filter. The query runs
/// <see cref="SearchDebounce"/> after the last keystroke; a newer query cancels the older one. Hybrid search goes
/// through <see cref="IAssistant.SearchAsync"/> (which itself drops to BM25 when the embedding model is missing or AI
/// is paused); when the assistant is missing or throws, <see cref="ISearchIndex.SearchKeywordAsync"/> answers.</para>
/// </remarks>
public sealed partial class LibraryViewModel : ObservableObject, IDisposable, IFillsFrame
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
    private readonly ITranscriptStore? _transcripts;
    private readonly SettingsWriter? _settings;
    private readonly ISettingsStore? _settingsStore;
    private readonly Dictionary<SessionId, (string? Language, string? FirstLine)> _details = [];
    private CancellationTokenSource? _detailsCts;
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
    /// <param name="transcripts">Where the language and the first line of each row's transcript come from; <c>null</c> = rows without them.</param>
    /// <param name="settings">Where the Screen switch of the New recording card is kept (<c>ScreenByDefault</c>, like the Settings); <c>null</c> = not kept.</param>
    public LibraryViewModel(
        ISessionStore store,
        TimeProvider time,
        ISessionProcessing? processing = null,
        Action<Action>? post = null,
        ISearchIndex? keywordSearch = null,
        IAssistant? assistant = null,
        IRecorder? recorder = null,
        ITranscriptStore? transcripts = null,
        ISettingsStore? settings = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _time = time;
        _processing = processing;
        _keywordSearch = keywordSearch;
        _assistant = assistant;
        _recorder = recorder;
        _transcripts = transcripts;
        _settings = settings is null ? null : SettingsWriter.For(settings);
        _settingsStore = settings;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        if (processing is not null)
        {
            processing.ProgressChanged += OnProcessingChanged;
        }

        if (recorder is not null)
        {
            recorder.StateChanged += OnRecorderStateChanged;
        }

        if (settings is not null)
        {
            ApplySavedSwitches(settings.Current);
        }

        Suggestions = [Strings.Library_Suggestion1, Strings.Library_Suggestion2, Strings.Library_Suggestion3];
        Entries.SetHead(new LibraryHeroEntry(this), new LibraryHeaderEntry(this), new LibraryFiltersEntry(this));
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

    /// <summary>Shows the Ask your library card (the shell sets it when it can open a library-wide chat).</summary>
    [ObservableProperty]
    public partial bool CanAskAll { get; set; }

    /// <summary>The three latest recordings whatever the filter and the search say: the sidebar's Recent list.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SessionRecord> Recent { get; private set; } = [];

    /// <summary>The page as one flat list for a single virtualised scroller: hero, header, filters, then the day groups and rows (or the search results, or the empty card).</summary>
    public LibraryEntryList Entries { get; } = [];

    // ---- the New recording card ---------------------------------------------------------------------------------------

    /// <summary>The Microphone switch: "Start recording" records the microphone.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    public partial bool Microphone { get; set; } = true;

    /// <summary>The System audio switch.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    public partial bool SystemAudio { get; set; } = true;

    /// <summary>The Screen switch; kept as the "screen by default" setting, like the Settings screen does.</summary>
    [ObservableProperty]
    public partial bool Screen { get; set; }

    /// <summary>A microphone can be recorded (the shell copies it from the recording screen); a switch that cannot be used is disabled.</summary>
    [ObservableProperty]
    public partial bool MicrophoneAvailable { get; set; } = true;

    [ObservableProperty]
    public partial bool SystemAudioAvailable { get; set; } = true;

    [ObservableProperty]
    public partial bool ScreenAvailable { get; set; } = true;

    /// <summary>The record hotkey as key caps ("Ctrl Shift R") for the line beside the button; empty = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecordHotkey))]
    public partial string RecordHotkeyText { get; set; } = string.Empty;

    public bool HasRecordHotkey => RecordHotkeyText.Length > 0;

    // ---- the Ask your library card --------------------------------------------------------------------------------------

    /// <summary>The question being typed in the card's box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    public partial string AskQuestion { get; set; } = string.Empty;

    /// <summary>The suggestion chips under the box.</summary>
    public IReadOnlyList<string> Suggestions { get; }

    // ---- the date filter ---------------------------------------------------------------------------------------------------

    /// <summary>First day of the picked range (a calendar day, local); <c>null</c> until "Pick dates" was used.</summary>
    [ObservableProperty]
    public partial DateTime? RangeStart { get; private set; }

    /// <summary>Last day of the picked range, included.</summary>
    [ObservableProperty]
    public partial DateTime? RangeEnd { get; private set; }

    /// <summary>"Pick dates", or the chosen days ("Oct 5 – Oct 12") while the range filters.</summary>
    public string RangeText => Filter == LibraryFilter.Range && RangeStart is { } start && RangeEnd is { } end
        ? start.Date == end.Date ? UiFormat.ShortDate(start) : UiText.Format(Strings.Library_RangeText, UiFormat.ShortDate(start), UiFormat.ShortDate(end))
        : Strings.Library_FilterPick;

    /// <summary>Raised when the user asks for a new recording.</summary>
    public event Action? NewRecordingRequested;

    /// <summary>Raised by "Start recording" of the New recording card, with the sources its switches have on.</summary>
    public event Action<RecordingSources>? StartRecordingRequested;

    /// <summary>Raised when the user sends a question (or a suggestion) from the Ask your library card.</summary>
    public event Action<string>? AskRequested;

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

    public bool IsMonthFilter
    {
        get => Filter == LibraryFilter.Month;
        set => SelectFilter(LibraryFilter.Month, value, nameof(IsMonthFilter));
    }

    /// <summary>The picked days filter the list (the "Pick dates" chip has the dot). Turning it off goes back to All.</summary>
    public bool IsRangeFilter
    {
        get => Filter == LibraryFilter.Range;
        set
        {
            if (!value && Filter == LibraryFilter.Range)
            {
                Filter = LibraryFilter.All;
            }
            else
            {
                OnPropertyChanged(nameof(IsRangeFilter));
            }
        }
    }

    internal bool IsFilteredNow => IsFiltered;

    private bool IsFiltered => Filter != LibraryFilter.All || !string.IsNullOrWhiteSpace(SearchText) || _loaded.Count > 0;

    /// <summary>The last background read of the rows' transcript lines (for tests).</summary>
    internal Task DetailsTask { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the sessions of the current filter from the store.</summary>
    public async Task LoadAsync()
    {
        var version = ++_version;
        IsLoading = true;
        try
        {
            // The sidebar's Recent list is the same whatever the filter says; with a filter it needs a query of its own.
            var recent = Filter == LibraryFilter.All ? null : await ListRecentAsync();
            var records = await _store.ListSessionsAsync(CreateQuery(), CancellationToken.None);
            if (version != _version)
            {
                return;
            }

            _loaded = records;
            _recentPool = recent ?? [.. records.Take(RecentPoolSize)];
            UpdateRecent();
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
        if (_recorder is not null)
        {
            _recorder.StateChanged -= OnRecorderStateChanged;
        }

        StopStaleTimer();
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
        _detailsCts?.Cancel();
        _detailsCts?.Dispose();
        _detailsCts = null;
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

    /// <summary>"Start recording" of the New recording card: needs a source switched on.</summary>
    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private void StartRecording() => StartRecordingRequested?.Invoke(new RecordingSources(Microphone, SystemAudio, Screen));

    private bool CanStartRecording() => Microphone || SystemAudio;

    /// <summary>The send button (or Enter) of the Ask your library card.</summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    private void Ask()
    {
        var question = AskQuestion.Trim();
        AskQuestion = string.Empty;
        AskRequested?.Invoke(question);
    }

    private bool CanAsk() => !string.IsNullOrWhiteSpace(AskQuestion);

    /// <summary>A suggestion chip: asks that question at once.</summary>
    [RelayCommand]
    private void AskSuggestion(string? question)
    {
        if (!string.IsNullOrWhiteSpace(question))
        {
            AskRequested?.Invoke(question);
        }
    }

    [RelayCommand]
    private void SetFilter(LibraryFilter filter) => Filter = filter;

    /// <summary>The calendar of "Pick dates" chose days: the first one filters to that day alone, the second one to the whole range.</summary>
    [RelayCommand]
    private void PickRange(DateRange range) => ApplyRange(range.First, range.Last);

    /// <summary>"Clear dates" in the calendar flyout: back to all recordings.</summary>
    [RelayCommand]
    private void ClearRange()
    {
        if (Filter == LibraryFilter.Range)
        {
            Filter = LibraryFilter.All;
        }
    }

    /// <summary>
    /// Filters to the recordings that started between two days (inclusive, in either order); the calendar of "Pick dates" calls it
    /// when the second day is chosen.
    /// </summary>
    public void ApplyRange(DateTime first, DateTime second)
    {
        var start = first.Date <= second.Date ? first.Date : second.Date;
        var end = first.Date <= second.Date ? second.Date : first.Date;
        var wasRange = Filter == LibraryFilter.Range;
        RangeStart = start;
        RangeEnd = end;
        OnPropertyChanged(nameof(RangeText));
        if (wasRange)
        {
            // The filter stays "range", so only the days changed: load again.
            LoadTask = LoadAsync();
        }
        else
        {
            Filter = LibraryFilter.Range;
        }
    }

    /// <summary>Takes the "screen by default" setting into the Screen switch (the Settings may have changed it) and the recording options the platform offers.</summary>
    internal void RefreshRecordingOptions(bool microphoneAvailable, bool systemAudioAvailable, bool screenAvailable)
    {
        MicrophoneAvailable = microphoneAvailable;
        SystemAudioAvailable = systemAudioAvailable;
        ScreenAvailable = screenAvailable;
    }

    /// <summary>Re-reads the saved switches (microphone, system audio, screen by default) into the New recording card.</summary>
    internal void RefreshDefaults(ISettingsStore? settings)
    {
        if (settings is not null)
        {
            ApplySavedSwitches(settings.Current);
        }
    }

    private void ApplySavedSwitches(AppSettings settings)
    {
        _applyingSwitches = true;
        Microphone = settings.RecordMicrophoneByDefault;
        SystemAudio = settings.RecordSystemAudioByDefault;
        Screen = settings.ScreenByDefault;
        _applyingSwitches = false;
    }

    partial void OnMicrophoneChanged(bool value) => SaveSwitch(s => s with { RecordMicrophoneByDefault = value });

    partial void OnSystemAudioChanged(bool value) => SaveSwitch(s => s with { RecordSystemAudioByDefault = value });

    partial void OnScreenChanged(bool value) => SaveSwitch(s => s with { ScreenByDefault = value });

    /// <summary>The last save of a switch of the card (for tests that need to await it).</summary>
    internal Task SwitchSaveTask { get; private set; } = Task.CompletedTask;

    private bool _applyingSwitches;

    /// <summary>A switch of the card is kept as a recording default, through the writer the Settings screen shares (edits never overwrite each other); a failure shows inline.</summary>
    private void SaveSwitch(Func<AppSettings, AppSettings> edit)
    {
        if (_applyingSwitches || _settings is null)
        {
            return;
        }

        SwitchSaveTask = SaveSwitchAsync(edit);
    }

    private async Task SaveSwitchAsync(Func<AppSettings, AppSettings> edit)
    {
        if (await _settings!.UpdateAsync(edit).ConfigureAwait(true) is { } error)
        {
            Error = error;
        }
    }

    partial void OnFilterChanged(LibraryFilter value)
    {
        if (value != LibraryFilter.Range)
        {
            // The picked days only mean something while they filter: the calendar clears its selection.
            RangeStart = null;
            RangeEnd = null;
        }

        OnPropertyChanged(nameof(IsTodayFilter));
        OnPropertyChanged(nameof(IsWeekFilter));
        OnPropertyChanged(nameof(IsAllFilter));
        OnPropertyChanged(nameof(IsMonthFilter));
        OnPropertyChanged(nameof(IsRangeFilter));
        OnPropertyChanged(nameof(RangeText));
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

            // A transcript was just written (or the work failed): what the row knows of it is stale, read it again.
            if (status.Stage is SessionProcessingStage.Completed or SessionProcessingStage.Failed)
            {
                _details.Remove(status.Session);
                ApplyDetails(Sessions);
            }
        }
        else if (status.Stage is SessionProcessingStage.Completed or SessionProcessingStage.Failed)
        {
            _details.Remove(status.Session);
        }

        UpdateRecent();
    });

    partial void OnSearchResultsChanged(IReadOnlyList<SearchResultViewModel> value) => RefreshEntries();

    partial void OnIsSearchingChanged(bool value) => RefreshEntries();

    partial void OnSearchErrorChanged(string? value) => RefreshEntries();

    partial void OnSessionsChanged(IReadOnlyList<SessionItemViewModel> value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowSessionList));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        RefreshEntries();
    }

    partial void OnErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        RefreshEntries();
    }

    private void RaiseSearchLayoutChanged()
    {
        OnPropertyChanged(nameof(ShowSearchResults));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(ShowNoSearchResults));
        OnPropertyChanged(nameof(SearchStatusText));
        OnPropertyChanged(nameof(HasSearchStatus));
        OnPropertyChanged(nameof(ShowSessionList));
        OnPropertyChanged(nameof(ShowEmptyState));
        RefreshEntries();
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
            case LibraryFilter.Month:
                return new SessionQuery(From: StartOfLocalDay(new DateTime(now.Year, now.Month, 1)));
            case LibraryFilter.Range when RangeStart is { } start && RangeEnd is { } end:
                // The store compares with <= : the last tick of the last day, so a recording at midnight of the next day is not in.
                return new SessionQuery(From: StartOfLocalDay(start.Date), To: StartOfLocalDay(end.Date.AddDays(1)).AddTicks(-1));
            default:
                return new SessionQuery();
        }
    }

    private DateTimeOffset StartOfLocalDay(DateTime date)
    {
        var unspecified = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, _time.LocalTimeZone.GetUtcOffset(unspecified));
    }

    private string? _appliedNeedle;
    private IReadOnlyList<SessionRecord>? _appliedLoaded;

    private void ApplySearch()
    {
        // With a transcript search the list is not on screen (the results are): the rows stay the full list and are not rebuilt per keystroke.
        var needle = HasTranscriptSearch ? string.Empty : SearchText.Trim();
        if (needle == _appliedNeedle && ReferenceEquals(_loaded, _appliedLoaded))
        {
            return;
        }

        _appliedNeedle = needle;
        _appliedLoaded = _loaded;
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

                var row = new SessionItemViewModel(record, _time, live, OpenRow);
                rows.Add(row);
                _rows[record.Id] = row;
            }
        }

        Sessions = rows;
        ApplyDetails(rows);
        RefreshEntries();
    }

    /// <summary>Newest sessions the Recent list picks from (a few more than it shows, so recordings still being transcribed can be skipped).</summary>
    private const int RecentPoolSize = 10;

    private IReadOnlyList<SessionRecord> _recentPool = [];

    /// <summary>
    /// The sidebar's list: the newest recordings that are ready to open. One that is still being transcribed is left out until it is
    /// done (the canvas lists Client demo, Voice note and Interview while "Weekly team sync" is at 64 %).
    /// </summary>
    private void UpdateRecent()
    {
        var recent = new List<SessionRecord>(SidebarViewModel.RecentCount);
        foreach (var record in _recentPool)
        {
            if (_recorder?.Current is { } current && current.Id == record.Id && _recorder.State != RecorderState.Idle)
            {
                continue;
            }

            var live = _processing?.GetStatus(record.Id);
            var busy = live is { Stage: SessionProcessingStage.Queued or SessionProcessingStage.BuildingPeaks or SessionProcessingStage.Transcribing or SessionProcessingStage.Saving };
            if (!busy)
            {
                recent.Add(record);
                if (recent.Count == SidebarViewModel.RecentCount)
                {
                    break;
                }
            }
        }

        if (!recent.SequenceEqual(Recent))
        {
            Recent = recent;
        }
    }

    private void OpenRow(SessionItemViewModel row) => SessionOpened?.Invoke(row.Record);

    private void OpenResult(SearchResultViewModel result) => SearchResultOpened?.Invoke(result.Record, result.At);

    /// <summary>The three newest sessions without any filter, for the sidebar; empty when the store fails (the page reports that itself).</summary>
    private async Task<IReadOnlyList<SessionRecord>?> ListRecentAsync()
    {
        try
        {
            return await _store.ListSessionsAsync(new SessionQuery(Limit: RecentPoolSize), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // The page as a flat list -------------------------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds everything after the three fixed blocks: the inline error, then either the search results, the day groups with
    /// their rows, or the empty card. Called whenever the state those depend on changes; an unchanged result touches nothing.
    /// </summary>
    private void RefreshEntries()
    {
        var tail = new List<object>(Sessions.Count + 8);
        if (Error is { } error)
        {
            tail.Add(new LibraryNoticeEntry(error));
        }

        if (ShowSearchResults)
        {
            if (SearchStatusText is { } status)
            {
                tail.Add(new LibraryNoticeEntry(status));
            }

            if (HasSearchResults)
            {
                tail.Add(new LibraryGroupEntry(Strings.Library_SearchResults));
                for (var i = 0; i < SearchResults.Count; i++)
                {
                    SearchResults[i].Position = PositionOf(i, SearchResults.Count);
                }

                tail.AddRange(SearchResults);
            }
            else if (ShowNoSearchResults)
            {
                tail.Add(new LibraryEmptyEntry(this, NoMatches: true));
            }
        }
        else if (Sessions.Count > 0)
        {
            AddGroups(tail);
        }
        else if (ShowEmptyState)
        {
            tail.Add(new LibraryEmptyEntry(this, NoMatches: false));
        }

        Entries.ReplaceTail(tail);
    }

    /// <summary>Day groups newest first: Today, Yesterday, This week (the five days before), then one heading per older day.</summary>
    private void AddGroups(List<object> tail)
    {
        var index = 0;
        while (index < Sessions.Count)
        {
            var first = Sessions[index];
            var title = GroupTitle(first.Record.StartedAt, out var key);
            var end = index + 1;
            while (end < Sessions.Count)
            {
                GroupTitle(Sessions[end].Record.StartedAt, out var next);
                if (next != key)
                {
                    break;
                }

                end++;
            }

            tail.Add(new LibraryGroupEntry(title));
            for (var i = index; i < end; i++)
            {
                Sessions[i].Position = PositionOf(i - index, end - index);
                tail.Add(Sessions[i]);
            }

            index = end;
        }
    }

    private static RowPosition PositionOf(int index, int count) =>
        count == 1 ? RowPosition.Only : index == 0 ? RowPosition.First : index == count - 1 ? RowPosition.Last : RowPosition.Middle;

    /// <summary>The heading of the group a start time belongs to, and a key that is equal for the starts of one group.</summary>
    private string GroupTitle(DateTimeOffset startedAt, out DateTime key)
    {
        var local = TimeZoneInfo.ConvertTime(startedAt, _time.LocalTimeZone);
        var days = UiFormat.DaysAgo(local, _time);
        switch (days)
        {
            case <= 0:
                key = DateTime.MinValue;
                return Strings.Library_FilterToday;
            case 1:
                key = DateTime.MinValue.AddDays(1);
                return Strings.Library_GroupYesterday;
            case <= 6:
                key = DateTime.MinValue.AddDays(2);
                return Strings.Library_FilterWeek;
            default:
                key = local.Date;
                return UiFormat.DayHeading(local, _time);
        }
    }

    // The language and first line of each row's transcript ---------------------------------------------------------------

    /// <summary>
    /// Shows what is known of the rows' transcripts now and reads the rest in one batch in the background (language and first line only,
    /// one query in the store, off the UI thread). Nothing is read while a recording runs; the read is retried when it ends. A newer load or search cancels the older read.
    /// </summary>
    private void ApplyDetails(IReadOnlyList<SessionItemViewModel> rows)
    {
        _detailsCts?.Cancel();
        _detailsCts?.Dispose();
        _detailsCts = null;
        if (_transcripts is null)
        {
            return;
        }

        List<SessionItemViewModel>? pending = null;
        foreach (var row in rows)
        {
            if (_details.TryGetValue(row.Id, out var known))
            {
                row.ApplyTranscript(known.Language, known.FirstLine);
            }
            else
            {
                (pending ??= []).Add(row);
            }
        }

        if (pending is null || RecorderIsBusy)
        {
            DetailsTask = Task.CompletedTask;
            return;
        }

        var cts = new CancellationTokenSource();
        _detailsCts = cts;
        DetailsTask = ReadDetailsAsync(pending, cts.Token);
    }

    private bool RecorderIsBusy => _recorder is { State: not RecorderState.Idle };

    private void OnRecorderStateChanged(RecorderState state) => _post(() =>
    {
        UpdateRecent();
        if (state == RecorderState.Idle && !_disposed)
        {
            ApplyDetails(Sessions);
        }
    });

    private async Task ReadDetailsAsync(List<SessionItemViewModel> rows, CancellationToken cancellationToken)
    {
        var transcripts = _transcripts!;
        IReadOnlyList<SessionId> ids = [.. rows.Select(r => r.Id)];
        IReadOnlyList<TranscriptSummary> summaries;
        try
        {
            summaries = await Task.Run(async () => await transcripts.GetTranscriptSummariesAsync(ids, cancellationToken), cancellationToken).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Cancelled, or the store failed: the rows simply have no language and text line (the page reports its own load errors).
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var byId = new Dictionary<SessionId, TranscriptSummary>(summaries.Count);
        foreach (var summary in summaries)
        {
            byId[summary.Session] = summary;
        }

        foreach (var row in rows)
        {
            if (byId.TryGetValue(row.Id, out var summary))
            {
                _details[row.Id] = (summary.Language, summary.FirstLine);
                row.ApplyTranscript(summary.Language, summary.FirstLine);
            }
        }
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
        var window = CreateQuery();
        var from = window.From;
        var to = window.To;
        var rows = new List<SearchResultViewModel>(hits.Count + 4);
        var known = new Dictionary<SessionId, SessionRecord?>();
        foreach (var record in _loaded)
        {
            known[record.Id] = record;
            if (record.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new SearchResultViewModel(record, null, string.Empty, _time) { OpenAction = OpenResult });
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

            if (record is null || (from is { } start && record.StartedAt < start) || (to is { } stop && record.StartedAt > stop))
            {
                continue;
            }

            rows.Add(new SearchResultViewModel(record, hit.Chunk.Start, hit.Chunk.Text, _time) { OpenAction = OpenResult });
        }

        return rows;
    }
}
