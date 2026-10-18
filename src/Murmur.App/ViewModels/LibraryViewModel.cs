using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Services;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

public enum LibraryFilter
{
    Today,
    Week,
    All,
}

/// <summary>The Library screen: sessions from <see cref="ISessionStore"/>, a date filter and a title search.</summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly ISessionStore _store;
    private readonly TimeProvider _time;
    private readonly ISessionProcessing? _processing;
    private readonly Action<Action> _post;
    private readonly Dictionary<SessionId, SessionItemViewModel> _rows = [];
    private IReadOnlyList<SessionRecord> _loaded = [];
    private int _version;

    /// <param name="processing">Pipeline status for the rows' badges; <c>null</c> = badges come from the stored state only.</param>
    /// <param name="post">Marshals a callback to the UI thread; pipeline events arrive on background threads.</param>
    public LibraryViewModel(ISessionStore store, TimeProvider time, ISessionProcessing? processing = null, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _time = time;
        _processing = processing;
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

    /// <summary>Raised when the user asks for a new recording.</summary>
    public event Action? NewRecordingRequested;

    /// <summary>Raised when the user opens a session row.</summary>
    public event Action<SessionRecord>? SessionOpened;

    /// <summary>The last started load (for tests that need to await a filter change).</summary>
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    public bool ShowEmptyState => !IsLoading && Sessions.Count == 0;

    public bool HasSessions => Sessions.Count > 0;

    public bool HasError => Error is not null;

    /// <summary>The phase-0 empty state, or a hint when a filter or search hides everything.</summary>
    public string EmptyTitle => IsFiltered ? "Нічого не знайдено" : "Ще немає записів";

    public string EmptyText => IsFiltered
        ? "Спробуй інший період або зміни пошуковий запит."
        : "Натисни «Новий запис», щоб записати перший мітинг. Усе лишається на твоєму компʼютері.";

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
            Error = "Не вдалося завантажити записи: " + ex.Message;
        }

        IsLoading = false;
        ApplySearch();
    }

    [RelayCommand]
    private void NewRecording() => NewRecordingRequested?.Invoke();

    [RelayCommand]
    private void SetFilter(LibraryFilter filter) => Filter = filter;

    partial void OnFilterChanged(LibraryFilter value)
    {
        OnPropertyChanged(nameof(IsTodayFilter));
        OnPropertyChanged(nameof(IsWeekFilter));
        OnPropertyChanged(nameof(IsAllFilter));
        LoadTask = LoadAsync();
    }

    partial void OnSearchTextChanged(string value) => ApplySearch();

    partial void OnSelectedSessionChanged(SessionItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedSession = null;
        SessionOpened?.Invoke(value.Record);
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
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

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
        foreach (var record in _loaded)
        {
            if (needle.Length == 0 || record.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                var row = new SessionItemViewModel(record, _time, _processing?.GetStatus(record.Id));
                rows.Add(row);
                _rows[record.Id] = row;
            }
        }

        Sessions = rows;
    }
}
