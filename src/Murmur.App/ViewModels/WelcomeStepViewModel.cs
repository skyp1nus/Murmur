using Murmur.App.Resources;

namespace Murmur.App.ViewModels;

/// <summary>Step 1: the animated orb and "Get started".</summary>
public sealed class WelcomeStepViewModel : OnboardingStepViewModel
{
    public WelcomeStepViewModel()
    {
        ShowBack = false;
        ContinueLabel = Strings.Onb1_Start;
    }

    public override string Title => Strings.Onb1_Headline;

    public override string Subtitle => Strings.Onb1_Subline;
}
