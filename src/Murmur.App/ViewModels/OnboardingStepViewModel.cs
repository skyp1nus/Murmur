using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Resources;

namespace Murmur.App.ViewModels;

/// <summary>
/// One step of the first-run onboarding. The shared frame (progress, headline, Back / Skip / Continue) reads the headline
/// and the state of the footer buttons from the current step; the step's own view shows the content.
/// </summary>
public abstract partial class OnboardingStepViewModel : ObservableObject
{
    public abstract string Title { get; }

    public abstract string Subtitle { get; }

    /// <summary>Continue is possible (step 2 needs a working microphone, or the "without microphone" choice).</summary>
    [ObservableProperty]
    public partial bool CanContinue { get; set; } = true;

    [ObservableProperty]
    public partial string ContinueLabel { get; set; } = Strings.Onb_Continue;

    /// <summary>The "Back" button is shown (not on the first step).</summary>
    [ObservableProperty]
    public partial bool ShowBack { get; set; } = true;

    /// <summary>The "Skip" button is shown (not on the audio step: the microphone permission cannot be skipped).</summary>
    [ObservableProperty]
    public partial bool ShowSkip { get; set; } = true;

    /// <summary>An extra outline button next to Continue ("Continue without microphone").</summary>
    [ObservableProperty]
    public partial bool ShowSecondary { get; set; }

    [ObservableProperty]
    public partial string SecondaryLabel { get; set; } = string.Empty;

    /// <summary>The step became the visible one: start whatever runs while it is shown (meters, downloads, the trial recording).</summary>
    internal virtual void Activate()
    {
    }

    /// <summary>The step is left or the window closes: stop everything <see cref="Activate"/> started. Safe to call twice.</summary>
    internal virtual void Deactivate()
    {
    }

    /// <summary>The secondary button was pressed; by default it only continues.</summary>
    internal virtual bool OnSecondary() => true;

    /// <summary>Completes when what <see cref="Deactivate"/> started has finished (for tests and for the final step).</summary>
    internal virtual Task Settled => Task.CompletedTask;
}
