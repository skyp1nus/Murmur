using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Calls;

namespace Murmur.App.ViewModels;

/// <summary>Which toast a call shows.</summary>
public enum CallToastKind
{
    /// <summary>"Call detected": Record / Not now and the "Always record" checkbox.</summary>
    Offer,

    /// <summary>The app is on the allowlist and the recording has started: Open / Stop.</summary>
    AutoRecording,
}

/// <summary>What the user chose on a call toast.</summary>
public enum CallToastAction
{
    Record,
    NotNow,
    Dismiss,
    Open,
    Stop,
}

/// <summary>
/// The content of the call toast (<c>CallToastWindow</c>): the title and body for the app of <see cref="Call"/>, and the buttons of
/// its <see cref="Kind"/>. A button only reports the choice through <see cref="ActionChosen"/> (once: the toast is done after it);
/// what happens is up to the call coordinator.
/// </summary>
public sealed partial class CallToastViewModel : ObservableObject
{
    private bool _done;

    /// <param name="call">The call the toast is about.</param>
    /// <param name="kind">Offer or auto-recording.</param>
    /// <param name="isSample">The toast of Settings → "Try it": no real app, so there is nothing to add to the allowlist.</param>
    public CallToastViewModel(CallInfo call, CallToastKind kind, bool isSample = false)
    {
        ArgumentNullException.ThrowIfNull(call);
        Call = call;
        Kind = kind;
        IsSample = isSample;
    }

    public CallInfo Call { get; }

    public CallToastKind Kind { get; }

    public bool IsSample { get; }

    public bool IsOffer => Kind == CallToastKind.Offer;

    public bool IsAutoRecording => Kind == CallToastKind.AutoRecording;

    public string Title => IsOffer ? Strings.Calls_DetectedTitle : UiText.Format(Strings.Calls_AutoTitle, Call.AppName);

    public string Body => IsOffer ? UiText.Format(Strings.Calls_DetectedBody, Call.AppName) : Strings.Calls_AutoBody;

    /// <summary>The "Always record calls from this app" checkbox: only on a real offer.</summary>
    public bool ShowAlwaysRecord => IsOffer && !IsSample;

    /// <summary>Checked: Record also adds the app's process name to the allowlist.</summary>
    [ObservableProperty]
    public partial bool AlwaysRecord { get; set; }

    /// <summary>The user chose; raised once.</summary>
    public event Action<CallToastViewModel, CallToastAction>? ActionChosen;

    [RelayCommand]
    private void Record() => Choose(CallToastAction.Record);

    [RelayCommand]
    private void NotNow() => Choose(CallToastAction.NotNow);

    [RelayCommand]
    private void Dismiss() => Choose(CallToastAction.Dismiss);

    [RelayCommand]
    private void Open() => Choose(CallToastAction.Open);

    [RelayCommand]
    private void Stop() => Choose(CallToastAction.Stop);

    private void Choose(CallToastAction action)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        ActionChosen?.Invoke(this, action);
    }
}
