using Avalonia.Input;
using Murmur.App.Localization;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Capture;
using Murmur.Core.Processing;
using Murmur.Core.Recording;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>The onboarding without a window: when it shows, how the steps navigate and what each of them does with its services.</summary>
public sealed class OnboardingViewModelTests : IDisposable
{
    // The texts asserted below are Ukrainian; English is the default language (D-039).
    private readonly IDisposable _ukrainian = UiCulture.Override("uk");

    public void Dispose() => _ukrainian.Dispose();

    // Start and finish -----------------------------------------------------------------------------------------------

    [Fact]
    public void FirstStart_ShowsTheOnboardingInsteadOfTheLibrary_WithoutTheSidebar()
    {
        using var rig = new OnboardingRig();

        Assert.Same(rig.Onboarding, rig.Shell.CurrentPage);
        Assert.True(rig.Shell.IsOnboarding);
        Assert.Null(rig.Shell.Sidebar);
        Assert.Equal(1, rig.Onboarding.StepNumber);
    }

    [Fact]
    public void CompletedOnboarding_StartsOnTheLibrary()
    {
        using var rig = new OnboardingRig(new AppSettings { OnboardingCompleted = true });

        Assert.Same(rig.Shell.Library, rig.Shell.CurrentPage);
        Assert.False(rig.Shell.IsOnboarding);
        Assert.NotNull(rig.Shell.Sidebar);
    }

    [Fact]
    public async Task AnExistingLibrary_DoesNotForceTheOnboarding_TheFlagIsSetSilently()
    {
        using var rig = new OnboardingRig();
        rig.Sessions.Add(Sessions.Make("Стендап", UiHarness.Now.AddDays(-2), TimeSpan.FromMinutes(20)));

        await rig.Shell.InitializeAsync();

        Assert.Same(rig.Shell.Library, rig.Shell.CurrentPage);
        Assert.True(rig.Store.Current.OnboardingCompleted);
    }

    [Fact]
    public async Task AnEmptyLibrary_KeepsTheOnboarding()
    {
        using var rig = new OnboardingRig();

        await rig.Shell.InitializeAsync();

        Assert.Same(rig.Onboarding, rig.Shell.CurrentPage);
        Assert.False(rig.Store.Current.OnboardingCompleted);
    }

    [Fact]
    public async Task AStepThatHasBeenLeft_IsNeverReplacedByTheLibraryCheck()
    {
        using var rig = new OnboardingRig();
        rig.Sessions.Add(Sessions.Make("Стендап", UiHarness.Now.AddDays(-2), TimeSpan.FromMinutes(20)));
        await rig.GoToStepAsync(2);

        await rig.Shell.InitializeAsync();

        Assert.Same(rig.Onboarding, rig.Shell.CurrentPage);
        Assert.False(rig.Store.Current.OnboardingCompleted);
    }

    [Fact]
    public async Task Skip_OnTheFirstStep_SavesTheFlagAtOnce_AndShowsTheLibrary()
    {
        using var rig = new OnboardingRig();

        rig.Onboarding.SkipCommand.Execute(null);
        await rig.Onboarding.FinishTask;

        Assert.True(rig.Store.Current.OnboardingCompleted);
        Assert.Same(rig.Shell.Library, rig.Shell.CurrentPage);
        Assert.NotNull(rig.Shell.Sidebar);
    }

    [Fact]
    public async Task Skip_OnTheModelsStep_JumpsToTheEnd_AndTheDownloadsKeepRunning()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(3);

        rig.Onboarding.SkipCommand.Execute(null);
        await rig.Onboarding.FinishTask;

        Assert.True(rig.Store.Current.OnboardingCompleted);
        Assert.Same(rig.Shell.Library, rig.Shell.CurrentPage);
        Assert.Contains(FakeModelManager.AsrId, rig.Manager.InstallCalls);
        Assert.False(rig.Manager.TokenOf(FakeModelManager.AsrId).IsCancellationRequested);
    }

    [Fact]
    public async Task Skip_IsNotOfferedOnTheAudioStep_AndDoesNothingThere()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(2);

        Assert.False(rig.Onboarding.CurrentStep.ShowSkip);
        rig.Onboarding.SkipCommand.Execute(null);

        Assert.Equal(2, rig.Onboarding.StepNumber);
        Assert.False(rig.Store.Current.OnboardingCompleted);
    }

    [Fact]
    public async Task ContinueAndBack_WalkTheFiveSteps_WithTheStepIndicator()
    {
        using var rig = new OnboardingRig();
        var labels = new List<string> { rig.Onboarding.StepLabel };
        var filled = new List<int> { rig.Onboarding.Segments.Count(s => s.IsFilled) };

        for (var step = 2; step <= 5; step++)
        {
            await rig.GoToStepAsync(step);
            labels.Add(rig.Onboarding.StepLabel);
            filled.Add(rig.Onboarding.Segments.Count(s => s.IsFilled));
        }

        Assert.Equal(["1 / 5", "2 / 5", "3 / 5", "4 / 5", "5 / 5"], labels);
        Assert.Equal([1, 2, 3, 4, 5], filled);
        Assert.Equal(5, rig.Onboarding.Segments.Count);

        rig.Onboarding.BackCommand.Execute(null);
        Assert.Equal("4 / 5", rig.Onboarding.StepLabel);
        Assert.IsType<PreferencesStepViewModel>(rig.Onboarding.CurrentStep);
    }

    [Fact]
    public void TheFirstStep_HasNoBack_AndBackDoesNothing()
    {
        using var rig = new OnboardingRig();

        Assert.False(rig.Onboarding.CurrentStep.ShowBack);
        rig.Onboarding.BackCommand.Execute(null);

        Assert.Equal(1, rig.Onboarding.StepNumber);
    }

    [Fact]
    public async Task OpenMurmur_OnTheLastStep_FinishesTheOnboarding()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(5);
        Assert.Equal("Відкрити Murmur", rig.Onboarding.CurrentStep.ContinueLabel);

        rig.Onboarding.ContinueCommand.Execute(null);
        await rig.Onboarding.FinishTask;

        Assert.True(rig.Store.Current.OnboardingCompleted);
        Assert.Same(rig.Shell.Library, rig.Shell.CurrentPage);
    }

    [Fact]
    public async Task RestartFromTheSettings_ShowsTheOnboardingAtOnce()
    {
        using var rig = new OnboardingRig(new AppSettings { OnboardingCompleted = true });
        rig.Shell.ShowSettings();
        Assert.Same(rig.Settings, rig.Shell.CurrentPage);

        rig.Settings.Appearance.RestartOnboardingCommand.Execute(null);
        await rig.Settings.LastSave;

        Assert.Same(rig.Onboarding, rig.Shell.CurrentPage);
        Assert.Equal(1, rig.Onboarding.StepNumber);
        Assert.Null(rig.Shell.Sidebar);
    }

    [Fact]
    public async Task OtherSettingsChanges_DoNotBringTheOnboardingBack()
    {
        using var rig = new OnboardingRig(new AppSettings { OnboardingCompleted = true });
        rig.Shell.ShowSettings();

        rig.Settings.Devices.ScreenByDefault = true;
        await rig.Settings.LastSave;

        Assert.Same(rig.Settings, rig.Shell.CurrentPage);
    }

    [Fact]
    public async Task LeavingTheOnboardingForAnotherPage_StopsWhatTheStepRuns()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;
        var source = rig.Devices.Latest(AudioDeviceKind.Microphone);

        rig.Shell.ShowLibrary();
        await rig.Onboarding.Audio.Settled;

        Assert.True(source.Stopped);
        Assert.True(source.Disposed);
    }

    // Step 2: audio ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Audio_TheMetersFollowTheSamples_AndAReadStartsANewWindow()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;

        rig.Devices.Latest(AudioDeviceKind.Microphone).Emit(0.02f, -0.5f, 0.1f);
        rig.Devices.Latest(AudioDeviceKind.SystemOutput).Emit(0.05f);

        var microphone = rig.Onboarding.Audio.Microphone.Meter.Level;
        var system = rig.Onboarding.Audio.SystemAudio.Meter.Level;
        Assert.InRange(microphone, 0.85f, 0.95f); // 0.5 = -6 dB on a -60 dB scale
        Assert.InRange(system, 0.4f, 0.6f);
        Assert.True(rig.Onboarding.Audio.Microphone.IsLive);
        Assert.Equal(0f, rig.Onboarding.Audio.Microphone.Meter.Level);
    }

    [Fact]
    public async Task Audio_AWorkingMicrophone_EnablesContinue_AndShowsTheAllowedCard()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;

        Assert.True(rig.Onboarding.CurrentStep.CanContinue);
        Assert.True(rig.Onboarding.Audio.ShowGranted);
        Assert.False(rig.Onboarding.Audio.ShowDenied);
        Assert.False(rig.Onboarding.CurrentStep.ShowSecondary);
    }

    [Fact]
    public async Task Audio_AccessDenied_ShowsThePermissionCard_AndOffersTheWindowsPage()
    {
        using var rig = new OnboardingRig();
        rig.Devices.StartFailures[AudioDeviceKind.Microphone] = new UnauthorizedAccessException("denied by Windows");
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;
        var audio = rig.Onboarding.Audio;

        Assert.Equal(AudioChannelState.Denied, audio.Microphone.State);
        Assert.True(audio.ShowDenied);
        Assert.True(audio.CanOpenSettings);
        Assert.False(audio.CanContinue);
        Assert.True(audio.ShowSecondary);
        Assert.Equal("Далі без мікрофона", audio.SecondaryLabel);

        audio.OpenSettingsCommand.Execute(null);
        Assert.Equal(1, rig.Launcher.Opened);
        Assert.False(audio.HasLauncherError);

        // The system audio is metered all the same.
        Assert.True(audio.SystemAudio.IsLive);
    }

    [Fact]
    public async Task Audio_AccessDeniedInsideAnotherException_IsStillAPermissionProblem()
    {
        using var rig = new OnboardingRig();
        rig.Devices.StartFailures[AudioDeviceKind.Microphone] = new InvalidOperationException("start", new UnauthorizedAccessException("denied"));
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;

        Assert.True(rig.Onboarding.Audio.ShowDenied);
    }

    [Fact]
    public async Task Audio_TryAgain_OpensTheMicrophoneOnceMore_AndContinueBecomesPossible()
    {
        using var rig = new OnboardingRig();
        rig.Devices.StartFailures[AudioDeviceKind.Microphone] = new UnauthorizedAccessException("denied");
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;
        Assert.True(rig.Onboarding.Audio.ShowDenied);

        rig.Devices.StartFailures.Clear();
        rig.Onboarding.Audio.TryAgainCommand.Execute(null);
        await rig.Onboarding.Audio.Settled;

        Assert.True(rig.Onboarding.Audio.ShowGranted);
        Assert.True(rig.Onboarding.CurrentStep.CanContinue);
        Assert.False(rig.Onboarding.CurrentStep.ShowSecondary);
    }

    [Fact]
    public async Task Audio_WithoutAWindowsSettingsPage_TheButtonIsHidden()
    {
        using var rig = new OnboardingRig();
        rig.Launcher.CanOpen = false;
        rig.Devices.StartFailures[AudioDeviceKind.Microphone] = new UnauthorizedAccessException("denied");
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;

        Assert.True(rig.Onboarding.Audio.ShowDenied);
        Assert.False(rig.Onboarding.Audio.CanOpenSettings);
    }

    [Fact]
    public async Task Audio_AFailingLauncher_ShowsTheReasonInline()
    {
        using var rig = new OnboardingRig();
        rig.Launcher.Failure = "no shell";
        rig.Devices.StartFailures[AudioDeviceKind.Microphone] = new UnauthorizedAccessException("denied");
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;

        rig.Onboarding.Audio.OpenSettingsCommand.Execute(null);

        Assert.True(rig.Onboarding.Audio.HasLauncherError);
        Assert.Contains("no shell", rig.Onboarding.Audio.LauncherError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audio_ContinueWithoutMicrophone_MovesOnToTheModels()
    {
        using var rig = new OnboardingRig();
        rig.Devices.StartFailures[AudioDeviceKind.Microphone] = new UnauthorizedAccessException("denied");
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;

        rig.Onboarding.SecondaryCommand.Execute(null);

        Assert.Equal(3, rig.Onboarding.StepNumber);
        Assert.IsType<ModelsStepViewModel>(rig.Onboarding.CurrentStep);
    }

    [Fact]
    public async Task Audio_ContinueIsRefusedWhileTheMicrophoneIsNotWorking()
    {
        using var rig = new OnboardingRig();
        rig.Devices.StartFailures[AudioDeviceKind.Microphone] = new UnauthorizedAccessException("denied");
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;

        rig.Onboarding.ContinueCommand.Execute(null);

        Assert.Equal(2, rig.Onboarding.StepNumber);
    }

    [Fact]
    public async Task Audio_NoMicrophone_SaysSo_AndOffersToContinueWithout()
    {
        using var rig = new OnboardingRig();
        rig.Devices.Microphones.Clear();
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;

        Assert.Equal(AudioChannelState.NoDevice, rig.Onboarding.Audio.Microphone.State);
        Assert.True(rig.Onboarding.Audio.ShowNoMicrophone);
        Assert.True(rig.Onboarding.CurrentStep.ShowSecondary);
        Assert.DoesNotContain(rig.Devices.Opened, o => o.Kind == AudioDeviceKind.Microphone);
    }

    [Fact]
    public async Task Audio_AnotherFailure_ShowsItsReason_AndOffersTheWayOut()
    {
        using var rig = new OnboardingRig();
        rig.Devices.StartFailures[AudioDeviceKind.Microphone] = new InvalidOperationException("device busy");
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;

        Assert.True(rig.Onboarding.Audio.ShowMicrophoneFailed);
        Assert.Contains("device busy", rig.Onboarding.Audio.MicrophoneFailedText, StringComparison.Ordinal);
        Assert.True(rig.Onboarding.CurrentStep.ShowSecondary);
    }

    [Fact]
    public async Task Audio_ASystemOutputThatFails_DoesNotBlockContinue()
    {
        using var rig = new OnboardingRig();
        rig.Devices.CreateFailures[AudioDeviceKind.SystemOutput] = new InvalidOperationException("no loopback");
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;

        Assert.True(rig.Onboarding.Audio.ShowSystemUnavailable);
        Assert.Contains("no loopback", rig.Onboarding.Audio.SystemUnavailableText, StringComparison.Ordinal);
        Assert.True(rig.Onboarding.CurrentStep.CanContinue);
    }

    [Fact]
    public async Task Audio_LeavingTheStep_StopsAndReleasesBothSources()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;
        var microphone = rig.Devices.Latest(AudioDeviceKind.Microphone);
        var system = rig.Devices.Latest(AudioDeviceKind.SystemOutput);
        Assert.True(microphone.Started && system.Started);

        rig.Onboarding.ContinueCommand.Execute(null);
        await rig.Onboarding.Audio.Settled;

        Assert.True(microphone.Stopped && microphone.Disposed);
        Assert.True(system.Stopped && system.Disposed);
        Assert.Equal(AudioChannelState.Idle, rig.Onboarding.Audio.Microphone.State);
    }

    [Fact]
    public async Task Audio_PickingAnotherMicrophone_RestartsTheMeterOnIt_AndSavesTheChoice()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;
        var first = rig.Devices.Latest(AudioDeviceKind.Microphone);

        var usb = rig.Onboarding.Audio.Devices.Microphones.Single(m => m.Id == "mic-usb");
        rig.Onboarding.Audio.Devices.SelectedMicrophone = usb;
        await rig.Onboarding.Audio.Settled;
        await rig.Onboarding.Audio.Devices.SaveTask;

        var second = rig.Devices.Latest(AudioDeviceKind.Microphone);
        Assert.NotSame(first, second);
        Assert.Equal("mic-usb", second.Device.Id);
        Assert.True(first.Disposed);
        Assert.Equal("mic-usb", rig.Store.Current.MicrophoneDeviceId);
    }

    [Fact]
    public async Task Audio_ASourceThatFaultsWhileRunning_ReportsAFailure_AndIsReleased()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;
        var source = rig.Devices.Latest(AudioDeviceKind.Microphone);

        source.Fault("unplugged");
        await rig.Onboarding.Audio.Settled;

        Assert.Equal(AudioChannelState.Failed, rig.Onboarding.Audio.Microphone.State);
        Assert.Equal("unplugged", rig.Onboarding.Audio.Microphone.Error);
        Assert.True(source.Disposed);
        Assert.True(rig.Onboarding.CurrentStep.ShowSecondary);
    }

    [Fact]
    public async Task Audio_AStartThatIsSupersededBeforeItOpens_ReleasesWhatItOpened()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(2);
        var microphone = rig.Onboarding.Audio.Microphone;

        _ = microphone.StartAsync();
        _ = microphone.StartAsync();
        await rig.Onboarding.Audio.Settled;
        await OnboardingRig.UntilAsync(() => rig.Devices.Sources.Count(s => s.Device.Kind == AudioDeviceKind.Microphone && !s.Disposed) <= 1);

        Assert.Single(rig.Devices.Sources, s => s.Device.Kind == AudioDeviceKind.Microphone && !s.Disposed);
    }

    // The level meter feed --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(-1f, 0f)]
    [InlineData(0.0005f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(4f, 1f)]
    public void LevelMeter_MapsPeaksToTheMeterScale(float peak, float expected)
    {
        Assert.Equal(expected, AudioLevelMeter.ToDisplay(peak), 3);
    }

    [Fact]
    public void LevelMeter_QuietSpeechStillMovesTheMeter()
    {
        Assert.InRange(AudioLevelMeter.ToDisplay(0.01f), 0.25f, 0.4f);
    }

    [Fact]
    public void LevelMeter_TheAudioCallbackDoesNotAllocate()
    {
        var meter = new AudioLevelMeter();
        var samples = new float[480];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Sin(i * 0.1f) * 0.3f;
        }

        // Warm both paths up first. The steady state must be allocation-free: a pass that allocates nothing proves it,
        // while one-time runtime work (tiered JIT promoting a method mid-loop) can land in any single pass.
        var attempts = new List<long>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            meter.Write(samples);
            meter.WriteSilence(480);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                meter.Write(samples);
                meter.WriteSilence(480);
            }

            attempts.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            if (attempts[^1] == 0)
            {
                break;
            }
        }

        Assert.True(attempts[^1] == 0, $"bytes allocated per pass: {string.Join(", ", attempts)}");
        Assert.True(meter.Level > 0);
    }

    // Step 3: models --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Models_TheRequiredItemsInstallByThemselves_WhenTheStepOpens()
    {
        using var rig = new OnboardingRig();
        Assert.Empty(rig.Manager.InstallCalls);

        await rig.GoToStepAsync(3);

        Assert.Equal(2, rig.Manager.InstallCalls.Count);
        Assert.Contains(FakeModelManager.AsrId, rig.Manager.InstallCalls);
        Assert.Contains(FakeModelManager.VadId, rig.Manager.InstallCalls);
        Assert.Equal(RequiredModelsState.Downloading, rig.Onboarding.Models.RequiredState);
        Assert.True(rig.Onboarding.CurrentStep.CanContinue);
    }

    [Fact]
    public async Task Models_ProgressIsCombinedAndWeightedBySize_AndTheReadyStateFollows()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(3);
        var models = rig.Onboarding.Models;

        rig.Manager.Progress(FakeModelManager.AsrId, 335_239_386, 670_478_772);
        Assert.InRange(models.RequiredPercent, 49d, 50d);
        Assert.Equal("0,67 ГБ", models.RequiredSizeText);
        Assert.Contains(" / ", models.RequiredTransferText, StringComparison.Ordinal);
        Assert.True(models.ShowRequiredProgress);

        rig.Manager.Complete(FakeModelManager.AsrId);
        rig.Manager.Complete(FakeModelManager.VadId);

        Assert.Equal(RequiredModelsState.Ready, models.RequiredState);
        Assert.True(models.IsRequiredReady);
        Assert.Equal(100d, models.RequiredPercent);
        Assert.True(models.AllDone);
        Assert.False(models.ShowBackgroundNote);
    }

    [Fact]
    public async Task Models_AFailedRequiredItem_ShowsTheFailure_AndRetryInstallsItAgain()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(3);

        rig.Manager.Fail(FakeModelManager.AsrId, "network down");
        await OnboardingRig.UntilAsync(() => rig.Onboarding.Models.IsRequiredFailed);
        var calls = rig.Manager.InstallCalls.Count(c => c == FakeModelManager.AsrId);

        rig.Onboarding.Models.RetryRequiredCommand.Execute(null);

        Assert.Equal(calls + 1, rig.Manager.InstallCalls.Count(c => c == FakeModelManager.AsrId));
        Assert.True(rig.Onboarding.CurrentStep.CanContinue);
    }

    [Fact]
    public async Task Models_TheAiCards_ListTheChatModels_AndMarkTheRecommendedOne()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(3);

        var choices = rig.Onboarding.Models.Choices;

        Assert.Equal(2, choices.Count);
        Assert.True(choices[0].IsRecommended);
        Assert.False(choices[1].IsRecommended);
        Assert.Equal("Рекомендовано для твого ПК", choices[0].Description);
        Assert.Equal("Найкраща якість, потрібна потужна відеокарта", choices[1].Description);
        Assert.Equal("5.00 ГБ".Replace('.', ','), choices[0].SizeText);
    }

    [Fact]
    public async Task Models_TheRecommendationFollowsTheModelManager()
    {
        using var rig = new OnboardingRig();
        rig.Manager.RecommendedChatModelId = FakeModelManager.ChatLargeId;
        var step = new ModelsStepViewModel(rig.Settings.Models, rig.Manager);

        Assert.False(step.Choices[0].IsRecommended);
        Assert.True(step.Choices[1].IsRecommended);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Models_ChoosingAChatModel_StartsTheSameBundleAsTheSettings()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(3);
        var models = rig.Onboarding.Models;
        rig.Manager.InstallCalls.Clear();

        models.Choices[0].SelectCommand.Execute(null);

        Assert.True(models.Choices[0].IsSelected);
        Assert.False(models.SkipAiSelected);
        await OnboardingRig.UntilAsync(() => rig.Manager.WasInstallCalled(FakeModelManager.RuntimeId));
        Assert.Equal([FakeModelManager.RuntimeId], rig.Manager.InstallCalls);
        rig.Manager.Complete(FakeModelManager.RuntimeId);
        await OnboardingRig.UntilAsync(() => rig.Manager.WasInstallCalled(FakeModelManager.EmbeddingId));
        rig.Manager.Complete(FakeModelManager.EmbeddingId);
        await OnboardingRig.UntilAsync(() => rig.Manager.WasInstallCalled(FakeModelManager.ChatSmallId));
        Assert.Equal([FakeModelManager.RuntimeId, FakeModelManager.EmbeddingId, FakeModelManager.ChatSmallId], rig.Manager.InstallCalls);
        Assert.True(rig.Onboarding.CurrentStep.CanContinue);
    }

    [Fact]
    public async Task Models_WhileABundleDownloads_TheOtherCardIsLocked_UntilSkipAiCancelsIt()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(3);
        var models = rig.Onboarding.Models;
        models.Choices[0].SelectCommand.Execute(null);
        await OnboardingRig.UntilAsync(() => rig.Manager.WasInstallCalled(FakeModelManager.RuntimeId));
        Assert.True(models.Choices[0].IsBusy);
        Assert.False(models.Choices[1].IsEnabled);

        models.Choices[1].SelectCommand.Execute(null);
        Assert.False(models.Choices[1].IsSelected);
        Assert.True(models.Choices[0].IsSelected);

        models.SkipAiCommand.Execute(null);

        Assert.True(models.SkipAiSelected);
        Assert.False(models.Choices[0].IsSelected);
        Assert.True(rig.Manager.TokenOf(FakeModelManager.RuntimeId).IsCancellationRequested);
        await OnboardingRig.UntilAsync(() => models.Choices[1].IsEnabled);
    }

    [Fact]
    public async Task Models_SkipAi_WithoutAChoice_DownloadsNothing()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(3);
        rig.Manager.InstallCalls.Clear();

        rig.Onboarding.Models.SkipAiCommand.Execute(null);

        Assert.True(rig.Onboarding.Models.SkipAiSelected);
        Assert.Empty(rig.Manager.InstallCalls);
        Assert.True(rig.Onboarding.CurrentStep.CanContinue);
    }

    [Fact]
    public async Task Models_ADriveThatCannotHoldTheChoice_SaysSoBeforeStarting()
    {
        using var rig = new OnboardingRig();
        rig.Manager.AvailableFreeSpaceBytes = 2_000_000_000;
        await rig.GoToStepAsync(3);
        var models = rig.Onboarding.Models;
        rig.Manager.InstallCalls.Clear();

        Assert.NotNull(models.Choices[0].SpaceText); // the card says it before anyone clicks
        models.Choices[0].SelectCommand.Execute(null);

        Assert.Empty(rig.Manager.InstallCalls);
        Assert.False(models.Choices[0].IsSelected);
        Assert.True(models.HasSpaceMessage);
        Assert.Contains("Недостатньо місця", models.SpaceMessage, StringComparison.Ordinal);
        Assert.Equal(models.Choices[0].SpaceText, models.Choices[0].StatusText);
    }

    [Fact]
    public async Task Models_UnknownFreeSpace_NeverBlocks()
    {
        using var rig = new OnboardingRig();
        rig.Manager.AvailableFreeSpaceBytes = long.MaxValue;
        await rig.GoToStepAsync(3);

        rig.Onboarding.Models.Choices[0].SelectCommand.Execute(null);

        Assert.True(rig.Onboarding.Models.Choices[0].IsSelected);
    }

    [Fact]
    public async Task Models_TheFreeSpaceOfTheModelsDriveIsShown()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(3);

        Assert.True(rig.Onboarding.Models.HasFreeSpace);
        Assert.Contains("123,46", rig.Onboarding.Models.FreeSpaceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Models_EverythingInstalled_IsTheReadyVariant_WithTheInstalledChatModelChosen()
    {
        using var rig = new OnboardingRig();
        foreach (var id in new[] { FakeModelManager.AsrId, FakeModelManager.VadId, FakeModelManager.RuntimeId, FakeModelManager.EmbeddingId, FakeModelManager.ChatSmallId })
        {
            rig.Manager.SetInstalled(id);
        }

        await rig.GoToStepAsync(3);
        var models = rig.Onboarding.Models;

        Assert.True(models.IsRequiredReady);
        Assert.True(models.Choices[0].IsSelected);
        Assert.True(models.Choices[0].IsInstalled);
        Assert.Equal("Не завантажено", models.Choices[1].StatusText);
        Assert.True(models.AllDone);
        Assert.Empty(rig.Manager.InstallCalls); // nothing to download
    }

    [Fact]
    public async Task Models_WithoutAModelManager_TheStepOnlyExplainsIt_AndContinueWorks()
    {
        using var rig = new OnboardingRig(models: false);
        await rig.GoToStepAsync(3);

        Assert.True(rig.Onboarding.Models.IsUnavailable);
        Assert.False(rig.Onboarding.Models.IsAvailable);
        Assert.True(rig.Onboarding.CurrentStep.CanContinue);
        rig.Onboarding.ContinueCommand.Execute(null);
        Assert.Equal(4, rig.Onboarding.StepNumber);
    }

    // Step 4: preferences ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preferences_RecordScreenByDefault_IsSavedAtOnce()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(4);

        rig.Onboarding.Preferences.Devices.ScreenByDefault = true;
        await rig.Settings.LastSave;

        Assert.True(rig.Store.Current.ScreenByDefault);
    }

    [Fact]
    public async Task Preferences_TheLanguageIsAutoDetectOnly()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(4);

        Assert.Equal(["Автовизначення"], rig.Onboarding.Preferences.Languages);
        Assert.Equal("auto", rig.Store.Current.TranscriptionLanguage);
    }

    [Fact]
    public async Task Preferences_TheHotkeyField_CapturesAndSavesACombination()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(4);
        var preferences = rig.Onboarding.Preferences;
        Assert.Equal(["Ctrl", "Shift", "R"], preferences.Caps.Select(c => c.Text));
        Assert.Equal([true, true, false], preferences.Caps.Select(c => c.HasPlus));

        preferences.HotkeyRow!.CaptureCommand.Execute(null);
        Assert.True(preferences.HotkeyRow!.IsCapturing);
        Assert.False(preferences.ShowCaps);

        Assert.True(preferences.Hotkeys.HandleKey(Key.K, KeyModifiers.Control | KeyModifiers.Alt));
        await preferences.Hotkeys.LastSave;

        Assert.Equal("Ctrl+Alt+K", rig.Store.Current.RecordHotkey);
        Assert.Equal(["Ctrl", "Alt", "K"], preferences.Caps.Select(c => c.Text));
        Assert.False(preferences.HotkeyRow!.IsCapturing);
    }

    [Fact]
    public async Task Preferences_LeavingTheStep_EndsAHotkeyCapture()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(4);
        rig.Onboarding.Preferences.HotkeyRow!.CaptureCommand.Execute(null);

        rig.Onboarding.BackCommand.Execute(null);

        Assert.False(rig.Onboarding.Preferences.HotkeyRow!.IsCapturing);
    }

    [Fact]
    public async Task Preferences_TheFolder_ChangesLikeInTheSettings_AndShowsTheFreeSpaceOfItsDrive()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(4);
        var preferences = rig.Onboarding.Preferences;
        Assert.Equal("/data/Recordings", preferences.Storage.EffectivePath);
        Assert.Contains("250", preferences.FreeSpaceText, StringComparison.Ordinal);
        // Fully qualified on every OS: the view model rejects a rooted-but-driveless "/mnt/..." on Windows.
        var chosen = OperatingSystem.IsWindows() ? @"D:\big\Murmur" : "/mnt/big/Murmur";
        preferences.Storage.FolderPicker = new FakeFolderPicker { Result = chosen };

        preferences.Storage.ChangeCommand.Execute(null);
        await preferences.Storage.LastOperation;

        Assert.Equal(chosen, rig.Store.Current.RecordingsDirectory);
        Assert.Equal(chosen, preferences.Storage.EffectivePath);
    }

    [Fact]
    public async Task Preferences_FreeSpaceIsLeftOutWhenTheDriveCannotBeRead()
    {
        using var rig = new OnboardingRig();
        var step = new PreferencesStepViewModel(rig.Settings, _ => null);

        Assert.False(step.HasFreeSpace);
        Assert.Equal(string.Empty, step.FreeSpaceText);
        await Task.CompletedTask;
    }

    // Step 5: first recording -----------------------------------------------------------------------------------------

    private static TrialState StateOf(OnboardingRig rig) => rig.Onboarding.FirstRecording.State;

    private static void SeedTranscript(OnboardingRig rig, params (Core.Transcription.Speaker Speaker, double Start, string Text)[] lines)
    {
        var id = rig.Onboarding.FirstRecording.Trial!.Id;
        rig.Transcripts.Transcript = new Core.Transcription.Transcript(
            id,
            [.. lines.Select(l => new Core.Transcription.TranscriptSegment(l.Speaker, TimeSpan.FromSeconds(l.Start), TimeSpan.FromSeconds(l.Start + 2), l.Text, []))],
            "test");
    }

    private static async Task RecordTenSecondsAsync(OnboardingRig rig)
    {
        await rig.GoToStepAsync(5);
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Recording);
        await rig.RunClockUntilAsync(() => StateOf(rig) != TrialState.Recording);
        await OnboardingRig.UntilAsync(() => StateOf(rig) is not (TrialState.Recording or TrialState.Stopping));
    }

    [Fact]
    public async Task FirstRecording_RecordsTenSecondsThroughTheRecorder_WithoutTheScreen_AndCountsDown()
    {
        using var rig = new OnboardingRig(new AppSettings { MicrophoneDeviceId = "mic-usb" });
        await rig.GoToStepAsync(5);
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Recording);
        var step = rig.Onboarding.FirstRecording;

        var options = Assert.Single(rig.Recorder.Starts);
        Assert.False(options.Screen);
        Assert.True(options.Microphone);
        Assert.True(options.SystemAudio);
        Assert.Equal("mic-usb", options.MicrophoneDeviceId);
        Assert.Equal("Пробний запис" + TrialSessionCleaner.Marker, options.Title);
        Assert.Equal("00:10", step.ClockText);
        Assert.True(step.IsRecording);
        Assert.Equal("Слухаю", step.StatusLabel);

        await rig.RunClockUntilAsync(() => step.ClockText == "00:07");
        Assert.Equal("00:07", step.ClockText);

        await rig.RunClockUntilAsync(() => StateOf(rig) != TrialState.Recording);
        await OnboardingRig.UntilAsync(() => rig.Recorder.Stops == 1 && StateOf(rig) != TrialState.Stopping);
        Assert.Equal("00:00", step.ClockText);
        Assert.Equal(1, rig.Recorder.Stops);
        Assert.False(step.IsRecording);
    }

    [Fact]
    public async Task FirstRecording_TheOrbFollowsTheRecordersLevels()
    {
        using var rig = new OnboardingRig();
        rig.Recorder.Levels = new AudioLevels(0.2f, 0.6f);

        Assert.Equal(0.6f, rig.Onboarding.FirstRecording.Level);
        rig.Recorder.Levels = new AudioLevels(0.9f, 0.1f);
        Assert.Equal(0.9f, rig.Onboarding.FirstRecording.Level);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task FirstRecording_ShowsTheRecognisedText_WithATimestampChip_WhenTheProcessorIsDone()
    {
        using var rig = new OnboardingRig();
        await RecordTenSecondsAsync(rig);
        var step = rig.Onboarding.FirstRecording;
        var id = step.Trial!.Id;
        Assert.Equal(TrialState.Transcribing, StateOf(rig)); // the pipeline has not reported yet: queued
        Assert.True(step.ShowPlaceholder);

        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.Transcribing, 0.5));
        Assert.Equal(50d, step.Progress);
        Assert.True(step.ShowProgress);

        SeedTranscript(rig, (Core.Transcription.Speaker.Me, 3, "Привіт, це мій перший запис."));
        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.Completed, 1));
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Done);

        var line = Assert.Single(step.Segments);
        Assert.Equal("Привіт, це мій перший запис.", line.Text);
        Assert.Equal("00:03", line.Time);
        Assert.Equal("Я", line.Speaker);
        Assert.True(step.ShowTranscript);
        Assert.False(step.ShowPlaceholder);
        Assert.True(step.ShowRetry);
        Assert.Equal("Готово", step.StatusLabel);
        Assert.Empty(rig.Sessions.Deleted); // still on the step
    }

    [Fact]
    public async Task FirstRecording_TheTrialSessionIsDeleted_WhenTheUserLeavesAfterProcessingFinished()
    {
        using var rig = new OnboardingRig();
        await RecordTenSecondsAsync(rig);
        var id = rig.Onboarding.FirstRecording.Trial!.Id;
        SeedTranscript(rig, (Core.Transcription.Speaker.Others, 1, "Hello"));
        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.Completed, 1));
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Done);
        var deleted = 0;
        rig.Onboarding.TrialSessionDeleted += () => deleted++;

        rig.Onboarding.BackCommand.Execute(null);
        await rig.Onboarding.FirstRecording.Settled;

        Assert.Equal([id], rig.Sessions.Deleted);
        Assert.Equal(1, deleted);
        Assert.Equal(TrialState.Ready, StateOf(rig));
    }

    [Fact]
    public async Task FirstRecording_OpenMurmur_DeletesTheTrialBeforeShowingTheLibrary()
    {
        using var rig = new OnboardingRig();
        await RecordTenSecondsAsync(rig);
        var id = rig.Onboarding.FirstRecording.Trial!.Id;
        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.Completed, 1));
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Done);

        rig.Onboarding.ContinueCommand.Execute(null);
        await rig.Onboarding.FinishTask;

        Assert.Equal([id], rig.Sessions.Deleted);
        Assert.Same(rig.Shell.Library, rig.Shell.CurrentPage);
        Assert.True(rig.Store.Current.OnboardingCompleted);
    }

    [Fact]
    public async Task FirstRecording_ASessionTheProcessorStillWorksOn_IsNeverDeleted_UntilItIsFinished()
    {
        using var rig = new OnboardingRig();
        await RecordTenSecondsAsync(rig);
        var id = rig.Onboarding.FirstRecording.Trial!.Id;
        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.Transcribing, 0.3));

        rig.Onboarding.ContinueCommand.Execute(null); // "Open Murmur" without waiting
        await rig.Onboarding.FinishTask;

        Assert.Empty(rig.Sessions.Deleted);
        Assert.Same(rig.Shell.Library, rig.Shell.CurrentPage);

        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.Saving, 0.98));
        Assert.Empty(rig.Sessions.Deleted);
        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.Indexing, 1));
        Assert.Empty(rig.Sessions.Deleted); // the indexer still reads the session

        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.Completed, 1));
        await rig.Onboarding.FirstRecording.DeleteTask;

        Assert.Equal([id], rig.Sessions.Deleted);
    }

    [Fact]
    public async Task FirstRecording_ASessionNobodyHasReportedYet_WaitsForTheProcessor_AndIsDeletedWhenItFails()
    {
        using var rig = new OnboardingRig();
        await RecordTenSecondsAsync(rig);
        var id = rig.Onboarding.FirstRecording.Trial!.Id;

        rig.Onboarding.BackCommand.Execute(null);
        await rig.Onboarding.FirstRecording.Settled;
        Assert.Empty(rig.Sessions.Deleted);

        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.Failed, 0, "boom"));
        await rig.Onboarding.FirstRecording.DeleteTask;

        Assert.Equal([id], rig.Sessions.Deleted);
    }

    [Fact]
    public async Task FirstRecording_LeavingWhileRecording_StopsTheRecorder_AndKeepsTheSessionForTheProcessor()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(5);
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Recording);
        var id = rig.Onboarding.FirstRecording.Trial!.Id;

        rig.Onboarding.BackCommand.Execute(null);
        await rig.Onboarding.FirstRecording.Settled;

        Assert.Equal(1, rig.Recorder.Stops);
        Assert.Equal(Core.Recording.RecorderState.Idle, rig.Recorder.State);
        Assert.Empty(rig.Sessions.Deleted); // the pipeline takes it over the moment the recorder stops

        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.Completed, 1));
        await rig.Onboarding.FirstRecording.DeleteTask;
        Assert.Equal([id], rig.Sessions.Deleted);
    }

    [Fact]
    public async Task FirstRecording_WithoutTheModel_WaitsForIt_ThenQueuesTheSession_AndContinuesByItself()
    {
        using var rig = new OnboardingRig();
        rig.Processing.IsAsrInstalled = false;
        await RecordTenSecondsAsync(rig);
        var step = rig.Onboarding.FirstRecording;
        var id = step.Trial!.Id;
        Assert.Equal(TrialState.WaitingForModel, StateOf(rig));
        Assert.Equal("Чекаємо на модель транскрипції…", step.PlaceholderText);
        Assert.True(step.ShowProgress);
        Assert.Empty(rig.Processing.Enqueued);

        rig.Manager.Progress(FakeModelManager.AsrId, 335_239_386, 670_478_772);
        Assert.InRange(step.Progress, 49d, 50d);

        rig.Processing.IsAsrInstalled = true;
        rig.Manager.Complete(FakeModelManager.AsrId);
        rig.Manager.Complete(FakeModelManager.VadId);
        await OnboardingRig.UntilAsync(() => rig.Processing.Enqueued.Contains(id));
        Assert.Equal(TrialState.Transcribing, StateOf(rig));

        SeedTranscript(rig, (Core.Transcription.Speaker.Me, 2, "Працює"));
        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.Completed, 1));
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Done);
        Assert.Equal("Працює", Assert.Single(step.Segments).Text);
    }

    [Fact]
    public async Task FirstRecording_TheModelStatusFromThePipeline_PutsTheTrialInTheWaitingState()
    {
        using var rig = new OnboardingRig();
        await RecordTenSecondsAsync(rig);
        var id = rig.Onboarding.FirstRecording.Trial!.Id;

        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.ModelMissing, 0, "no model"));

        Assert.Equal(TrialState.WaitingForModel, StateOf(rig));
    }

    [Fact]
    public async Task FirstRecording_TheUserCanFinishWithoutWaitingForTheModel_AndTheTrialIsRemoved()
    {
        using var rig = new OnboardingRig();
        rig.Processing.IsAsrInstalled = false;
        await RecordTenSecondsAsync(rig);
        var id = rig.Onboarding.FirstRecording.Trial!.Id;
        rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.ModelMissing, 0, "no model"));

        rig.Onboarding.ContinueCommand.Execute(null);
        await rig.Onboarding.FinishTask;

        Assert.Equal([id], rig.Sessions.Deleted); // the job has ended with "model missing": safe to delete
        Assert.True(rig.Store.Current.OnboardingCompleted);
    }

    [Fact]
    public async Task FirstRecording_AFailedTranscription_IsShownInline_WithATryAgain()
    {
        using var rig = new OnboardingRig();
        await RecordTenSecondsAsync(rig);
        var step = rig.Onboarding.FirstRecording;

        rig.Processing.Raise(new SessionProcessingStatus(step.Trial!.Id, SessionProcessingStage.Failed, 0, "worker crashed"));

        Assert.Equal(TrialState.Failed, StateOf(rig));
        Assert.True(step.ShowMessage);
        Assert.Contains("worker crashed", step.MessageText, StringComparison.Ordinal);
        Assert.True(step.ShowRetry);
        Assert.True(rig.Onboarding.CurrentStep.CanContinue);
    }

    [Fact]
    public async Task FirstRecording_NothingRecognised_SaysSo()
    {
        using var rig = new OnboardingRig();
        await RecordTenSecondsAsync(rig);
        var step = rig.Onboarding.FirstRecording;
        rig.Transcripts.Transcript = new Core.Transcription.Transcript(step.Trial!.Id, [], "test");

        rig.Processing.Raise(new SessionProcessingStatus(step.Trial.Id, SessionProcessingStage.Completed, 1));
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Done);

        Assert.True(step.ShowMessage);
        Assert.False(step.ShowTranscript);
        Assert.Contains("Нічого не розпізнано", step.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstRecording_ARecorderThatCannotStart_ShowsTheErrorInline_AndTryAgainWorks()
    {
        using var rig = new OnboardingRig();
        rig.Recorder.StartFailure = new InvalidOperationException("device lost");
        await rig.GoToStepAsync(5);
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Failed);
        var step = rig.Onboarding.FirstRecording;

        Assert.Contains("device lost", step.MessageText, StringComparison.Ordinal);
        Assert.True(step.ShowMessage);
        Assert.True(rig.Onboarding.CurrentStep.CanContinue); // the way out: finish

        rig.Recorder.StartFailure = null;
        step.TryAgainCommand.Execute(null);
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Recording);

        Assert.Equal(2, rig.Recorder.Starts.Count);
    }

    [Fact]
    public async Task FirstRecording_WithoutAnyAudioDevice_SaysSo_AndDoesNotStartTheRecorder()
    {
        using var rig = new OnboardingRig();
        rig.Devices.Microphones.Clear();
        rig.Devices.Outputs.Clear();
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;
        rig.Onboarding.SecondaryCommand.Execute(null);
        await rig.GoToStepAsync(5);

        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Failed);

        Assert.Empty(rig.Recorder.Starts);
        Assert.Contains("Немає пристрою", rig.Onboarding.FirstRecording.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstRecording_TryAgain_RemovesThePreviousTrial_AndRecordsANewOne()
    {
        using var rig = new OnboardingRig();
        await RecordTenSecondsAsync(rig);
        var step = rig.Onboarding.FirstRecording;
        var first = step.Trial!.Id;
        rig.Processing.Raise(new SessionProcessingStatus(first, SessionProcessingStage.Completed, 1));
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Done);

        step.TryAgainCommand.Execute(null);
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Recording);

        Assert.Equal([first], rig.Sessions.Deleted);
        Assert.NotEqual(first, step.Trial!.Id);
        Assert.Equal(2, rig.Recorder.Starts.Count);
        Assert.Equal("00:10", step.ClockText);
    }

    [Fact]
    public async Task FirstRecording_ReturningToTheStep_RecordsAFreshTrial()
    {
        using var rig = new OnboardingRig();
        await RecordTenSecondsAsync(rig);
        var first = rig.Onboarding.FirstRecording.Trial!.Id;
        rig.Processing.Raise(new SessionProcessingStatus(first, SessionProcessingStage.Completed, 1));

        rig.Onboarding.BackCommand.Execute(null);
        await rig.Onboarding.FirstRecording.Settled;
        rig.Onboarding.ContinueCommand.Execute(null);
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Recording);

        Assert.Equal(2, rig.Recorder.Starts.Count);
        Assert.NotEqual(first, rig.Onboarding.FirstRecording.Trial!.Id);
    }

    [Fact]
    public async Task FirstRecording_WithoutAPipeline_StillRecords_AndRemovesTheSession()
    {
        using var rig = new OnboardingRig(processing: false);
        await RecordTenSecondsAsync(rig);
        var step = rig.Onboarding.FirstRecording;
        var id = step.Trial!.Id;
        Assert.Equal(TrialState.Done, StateOf(rig));
        Assert.True(step.ShowMessage);

        rig.Onboarding.ContinueCommand.Execute(null);
        await rig.Onboarding.FinishTask;

        Assert.Equal([id], rig.Sessions.Deleted);
    }

    [Fact]
    public async Task Restart_ShowsTheFirstStepAgain_AndStopsTheLastOne()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(5);
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Recording);

        rig.Onboarding.Begin();
        await rig.Onboarding.FirstRecording.Settled;

        Assert.Equal(1, rig.Onboarding.StepNumber);
        Assert.Equal(1, rig.Recorder.Stops);
        Assert.Equal(TrialState.Ready, StateOf(rig));
    }

    // A trial the window closed on ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ATrialLeftByAClosedWindow_IsRemovedAtTheNextStart_AndDoesNotCompleteTheOnboarding()
    {
        using var first = new OnboardingRig();
        await RecordTenSecondsAsync(first);
        var trial = first.Onboarding.FirstRecording.Trial!;
        first.Processing.Raise(new SessionProcessingStatus(trial.Id, SessionProcessingStage.Transcribing, 0.4));
        first.Sessions.Add(trial); // what the real recorder's store holds

        first.Shell.OnWindowClosed(); // the app ends: the deferred deletion is lost
        await first.Onboarding.FirstRecording.Settled;
        Assert.Empty(first.Sessions.Deleted);

        using var second = new OnboardingRig(store: first.Store, sessions: first.Sessions);
        await second.Shell.InitializeAsync();

        Assert.Equal([trial.Id], second.Sessions.Deleted);
        Assert.Same(second.Onboarding, second.Shell.CurrentPage);
        Assert.False(second.Store.Current.OnboardingCompleted);
        Assert.False(second.Shell.Library.HasSessions);
    }

    [Fact]
    public async Task ALeftoverTrialThatIsBeingTranscribed_IsKept_ButStillNoSignOfAnExistingUser()
    {
        using var rig = new OnboardingRig();
        var trial = Sessions.Make(TrialSessionCleaner.TitleOfTrial(), UiHarness.Now, TimeSpan.FromSeconds(10));
        rig.Sessions.Add(trial);
        rig.Processing.Set(new SessionProcessingStatus(trial.Id, SessionProcessingStage.Transcribing, 0.5));

        await rig.Shell.InitializeAsync();

        Assert.Empty(rig.Sessions.Deleted);
        Assert.Same(rig.Onboarding, rig.Shell.CurrentPage);
        Assert.False(rig.Store.Current.OnboardingCompleted);
    }

    [Fact]
    public async Task ALeftoverTrial_NextToARealRecording_IsRemoved_AndTheExistingUserSkipsTheOnboarding()
    {
        using var rig = new OnboardingRig();
        var trial = Sessions.Make("Пробний запис", UiHarness.Now, TimeSpan.FromSeconds(10)); // an old title without the marker
        var real = Sessions.Make("Стендап", UiHarness.Now.AddDays(-3), TimeSpan.FromMinutes(20));
        rig.Sessions.Add(trial);
        rig.Sessions.Add(real);

        await rig.Shell.InitializeAsync();

        Assert.Equal([trial.Id], rig.Sessions.Deleted);
        Assert.Same(rig.Shell.Library, rig.Shell.CurrentPage);
        Assert.True(rig.Store.Current.OnboardingCompleted);
        Assert.Single(rig.Shell.Library.Sessions);
    }

    [Theory]
    [InlineData("Пробний запис", true)]
    [InlineData("Test recording", true)]
    [InlineData("Щось\u2063", true)]
    [InlineData("Стендап", false)]
    public void TheTrialIsRecognisedByItsMarkerOrItsTitle(string title, bool expected)
    {
        Assert.Equal(expected, TrialSessionCleaner.IsTrial(Sessions.Make(title, UiHarness.Now, TimeSpan.Zero)));
    }

    [Fact]
    public async Task LeavingAfterTheRecorderEndedTheSessionItself_ReadsTheStoredSession_AndDeletesIt()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(5);
        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Recording);
        var stale = rig.Onboarding.FirstRecording.Trial!; // the copy says "Recording"
        rig.Recorder.FailBySelf();
        await rig.RunClockUntilAsync(() => StateOf(rig) == TrialState.Failed);
        rig.Sessions.Add(stale with { State = Core.Sessions.SessionState.Failed });

        rig.Onboarding.BackCommand.Execute(null);
        await rig.Onboarding.FirstRecording.Settled;

        Assert.Equal([stale.Id], rig.Sessions.Deleted);
    }

    // Smaller guarantees ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FirstRecording_ARecorderThatIsAlreadyBusy_SaysSo()
    {
        using var rig = new OnboardingRig();
        rig.Recorder.ForceRecording(Sessions.Make("Нарада", UiHarness.Now, TimeSpan.Zero, state: Core.Sessions.SessionState.Recording));
        await rig.GoToStepAsync(5);

        await OnboardingRig.UntilAsync(() => StateOf(rig) == TrialState.Failed);

        Assert.Empty(rig.Recorder.Starts);
        Assert.Contains("Запис уже триває", rig.Onboarding.FirstRecording.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRecordingScreen_DoesNotReportATrialThatEndedNormally_AsStoppedByItself()
    {
        using var rig = new OnboardingRig();
        await RecordTenSecondsAsync(rig);

        await rig.Shell.Recording.AutoStopReport;

        Assert.Null(rig.Shell.Recording.Error);
    }

    [Fact]
    public void Models_TheTransferText_WritesTheSharedUnitOnce()
    {
        Assert.Equal("0,31 / 0,67 ГБ", UiFormat.SizeProgress(310_000_000, 670_000_000));
        Assert.Equal("50 МБ / 0,67 ГБ", UiFormat.SizeProgress(50_000_000, 670_000_000));
    }

    [Theory]
    [InlineData("Gemma 4 26B A4B", "Gemma 4 26B")]
    [InlineData("Gemma 4 E4B", "Gemma 4 E4B")]
    [InlineData("Gemma 4 26B A4B (Q4_K_M)", "Gemma 4 26B A4B (Q4_K_M)")]
    [InlineData("Alpha", "Alpha")]
    [InlineData("Model AB", "Model AB")]
    public void Models_TheAiCardName_DropsTheActiveParameterMarkerOnlyAtTheEnd(string catalogName, string expected)
    {
        Assert.Equal(expected, AiChoiceViewModel.WithoutActiveParameters(catalogName));
    }

    [Fact]
    public async Task Models_FreeSpaceIsNotReadOnEveryProgressTick()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(3);
        var reads = rig.Manager.FreeSpaceReads;

        for (var i = 1; i <= 30; i++)
        {
            rig.Manager.Progress(FakeModelManager.AsrId, i * 10_000_000, 670_478_772);
        }

        Assert.Equal(reads, rig.Manager.FreeSpaceReads);
    }

    [Fact]
    public void TheOnboarding_IsNotBuiltWhileTheFlagIsSet_AndNotShownWhileARecordingRuns()
    {
        var built = 0;
        using var rig = new OnboardingRig(new AppSettings { OnboardingCompleted = true });
        var library = new LibraryViewModel(rig.Sessions, rig.Time, null, action => action());
        var recording = new RecordingViewModel(rig.Recorder, rig.Capture, rig.Sessions, action => action(), rig.Store);
        var shell = new ShellViewModel(
            library,
            recording,
            settings: rig.Settings,
            onboardingFactory: () =>
            {
                built++;
                return rig.Onboarding;
            },
            settingsStore: rig.Store,
            post: action => action());
        Assert.Equal(0, built);
        Assert.Null(shell.Onboarding);

        rig.Recorder.ForceRecording(Sessions.Make("Нарада", UiHarness.Now, TimeSpan.Zero, state: Core.Sessions.SessionState.Recording));
        shell.ShowOnboarding();
        Assert.Equal(0, built);
        Assert.Same(library, shell.CurrentPage);
    }

    [Fact]
    public async Task Audio_ADeviceThatCannotBeReleased_DoesNotLeaveTheChannelStuck()
    {
        using var rig = new OnboardingRig();
        rig.Devices.DisposeFailures[AudioDeviceKind.Microphone] = new InvalidOperationException("gone");
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;

        rig.Onboarding.Audio.Devices.SelectedMicrophone = rig.Onboarding.Audio.Devices.Microphones.Single(m => m.Id == "mic-usb");
        await rig.Onboarding.Audio.Settled;

        Assert.Equal(AudioChannelState.Live, rig.Onboarding.Audio.Microphone.State);
        Assert.Equal("mic-usb", rig.Devices.Latest(AudioDeviceKind.Microphone).Device.Id);
    }

    [Fact]
    public async Task Audio_TheFaultHandlerIsAttachedBeforeTheSourceStarts()
    {
        using var rig = new OnboardingRig();
        await rig.GoToStepAsync(2);
        await rig.Onboarding.Audio.Settled;

        Assert.True(rig.Devices.Latest(AudioDeviceKind.Microphone).FaultedHandledAtStart);
        Assert.True(rig.Devices.Latest(AudioDeviceKind.SystemOutput).FaultedHandledAtStart);
    }
}
