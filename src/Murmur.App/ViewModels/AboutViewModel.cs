using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Diagnostics;

namespace Murmur.App.ViewModels;

/// <summary>
/// The "Про програму" section: version, the project licence and the attributions the licences of the bundled models require; with a real
/// diagnostic log also "Open logs folder" and "Copy diagnostics" (#100).
/// </summary>
public sealed partial class AboutViewModel : ObservableObject
{
    /// <summary>The project licence (see the LICENSE file at the repository root).</summary>
    public const string LicenseName = "Apache 2.0";

    /// <summary>How long "Copied" stays beside the buttons.</summary>
    internal static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(2);

    private readonly IAppLog _log;
    private readonly IFolderLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private CancellationTokenSource? _statusReset;

    /// <param name="version">The version shown; the build-time version by default.</param>
    /// <param name="log">
    /// The diagnostic log the two buttons open and copy; <c>null</c> or <see cref="NullAppLog"/> (designer, tests, the parity boards) hides them.
    /// </param>
    /// <param name="launcher">Opens the logs folder; the OS shell by default.</param>
    /// <param name="time">The clock of the "Copied" confirmation.</param>
    /// <param name="post">Marshals the end of the confirmation to the UI thread; runs inline by default.</param>
    public AboutViewModel(string? version = null, IAppLog? log = null, IFolderLauncher? launcher = null, TimeProvider? time = null, Action<Action>? post = null)
    {
        Version = version ?? BuildInfo.Version;
        License = UiText.Format(Strings.About_License, LicenseName);

        // Licence names follow THIRD-PARTY-NOTICES.md. Parakeet's CC-BY-4.0 requires the attribution shown here.
        ParakeetCredit = Credit("Parakeet TDT v3", "NVIDIA, CC-BY-4.0");
        var gemma = Credit("Gemma 4", "Google, Apache 2.0");
        var embedding = Credit("EmbeddingGemma", "Google, Gemma Terms of Use");
        GoogleCredit = UiText.Format(Strings.About_Google, gemma, embedding);
        string[] others = [Credit("llama.cpp", "MIT"), Credit("sherpa-onnx", "Apache 2.0"), Credit("Silero VAD", "MIT")];
        OtherCredits = string.Join(" · ", others);
        Credits = [ParakeetCredit, gemma, embedding, .. others];

        _log = log ?? NullAppLog.Instance;
        _launcher = launcher ?? SystemFolderLauncher.Instance;
        _time = time ?? TimeProvider.System;
        _post = post ?? (static action => action());
    }

    public string Version { get; }

    public string License { get; }

    public IReadOnlyList<string> Credits { get; }

    /// <summary>The credit the CC-BY licence requires on the card.</summary>
    public string ParakeetCredit { get; }

    /// <summary>Both Google models in one line, each with its own licence (they differ).</summary>
    public string GoogleCredit { get; }

    /// <summary>The remaining components, one line under the card.</summary>
    public string OtherCredits { get; }

    /// <summary>A real log is wired: the "Open logs folder" and "Copy diagnostics" row shows (never with the null log of the boards).</summary>
    public bool HasDiagnostics => _log.Directory.Length > 0;

    /// <summary>
    /// Writes text to the clipboard and tells whether it did; set by the view (the clipboard belongs to the window). <c>null</c> = no
    /// clipboard, so "Copy diagnostics" says so.
    /// </summary>
    public Func<string, Task<bool>>? ClipboardWriter { get; set; }

    /// <summary>"Copied" for a moment after a copy, or why the folder or the copy failed; <c>null</c> otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnosticsStatus))]
    public partial string? DiagnosticsStatus { get; private set; }

    public bool HasDiagnosticsStatus => DiagnosticsStatus is not null;

    /// <summary>Opens the logs folder, creating it first (a healthy run never did).</summary>
    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (!HasDiagnostics)
        {
            return;
        }

        string? failure;
        try
        {
            Directory.CreateDirectory(_log.Directory);
            failure = _launcher.TryOpen(_log.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            failure = ex.Message;
        }

        ShowStatus(failure is null ? null : UiText.Format(Strings.Storage_OpenFailed, failure), reset: false);
    }

    /// <summary>Puts <see cref="IAppLog.Snapshot"/> (the recent events and the last crash) on the clipboard; "Copied" shows for two seconds.</summary>
    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        if (!HasDiagnostics)
        {
            return;
        }

        bool copied;
        string? failure = null;
        try
        {
            // The snapshot reads the last crash file: off the UI thread.
            copied = ClipboardWriter is { } write && await write(await Task.Run(_log.Snapshot));
        }
        catch (Exception ex)
        {
            copied = false;
            failure = ex.Message;
        }

        ShowStatus(
            copied
                ? Strings.About_DiagnosticsCopied
                : UiText.Format(Strings.About_DiagnosticsFailed, failure ?? Strings.Recording_ClipboardUnavailable),
            reset: copied);
    }

    private void ShowStatus(string? status, bool reset)
    {
        var previous = Interlocked.Exchange(ref _statusReset, null);
        previous?.Cancel();
        previous?.Dispose();
        DiagnosticsStatus = status;
        if (reset)
        {
            var next = new CancellationTokenSource();
            _statusReset = next;
            _ = ClearStatusAsync(status, next.Token);
        }
    }

    private async Task ClearStatusAsync(string? shown, CancellationToken token)
    {
        try
        {
            await Task.Delay(CopiedFor, _time, token).ConfigureAwait(false);
            _post(() =>
            {
                if (!token.IsCancellationRequested && ReferenceEquals(DiagnosticsStatus, shown))
                {
                    DiagnosticsStatus = null;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Pressed again: the newer status owns the line.
        }
    }

    private static string Credit(string component, string licence) => UiText.Format(Strings.About_Credit, component, licence);
}
