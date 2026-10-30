using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Models;
using Murmur.Core.Sessions;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>The follow-ups collected from the reviews of the Settings, recovery and installer work (task p4-app-polish).</summary>
public sealed class AppPolishTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 15, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public Rig(AppSettings? initial = null, ISettingsStore? store = null, string? defaultFolder = "/data/Recordings")
        {
            Store = store ?? new FakeSettingsStore(initial);
            Launcher = new FakeFolderLauncher();
            Vm = new SettingsViewModel(Store, Capture, Manager, null, defaultFolder, Launcher, Post, version: "1.2.3");
        }

        public ISettingsStore Store { get; }

        public FakeModelManager Manager { get; } = new();

        public FakeFolderLauncher Launcher { get; }

        public FakeDisplayCapture Capture { get; } = new();

        public SettingsViewModel Vm { get; }

        /// <summary>Events marshalled to the "UI thread": held until <see cref="Flush"/> when <see cref="Hold"/> is set.</summary>
        public bool Hold { get; set; }

        public List<Action> Posted { get; } = [];

        public ModelRowViewModel Row(string id) => Vm.Models!.Rows.Single(r => r.Id == id);

        public void Flush()
        {
            var pending = Posted.ToArray();
            Posted.Clear();
            foreach (var action in pending)
            {
                action();
            }
        }

        private void Post(Action action)
        {
            if (Hold)
            {
                Posted.Add(action);
            }
            else
            {
                action();
            }
        }
    }

    private sealed class FailingStore : ISettingsStore
    {
        public AppSettings Current { get; } = new();

        public event Action<AppSettings>? Changed
        {
            add { }
            remove { }
        }

        public ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken) => throw new IOException("disk is read-only");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    // 1. Models -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Models_RowsOfARunningAiBundle_CannotBeRemoved_UntilItEnds()
    {
        var rig = new Rig();
        rig.Manager.SetInstalled(FakeModelManager.RuntimeId);
        rig.Vm.Models!.Refresh();
        var runtime = rig.Row(FakeModelManager.RuntimeId);
        var embedding = rig.Row(FakeModelManager.EmbeddingId);
        var chat = rig.Row(FakeModelManager.ChatSmallId);
        Assert.True(runtime.ShowRemove);

        chat.InstallCommand.Execute(null);

        // The embedding model is in the bundle; the installed runtime is shared by it.
        Assert.True(embedding.IsRemoveLocked);
        Assert.True(runtime.IsRemoveLocked);
        Assert.False(runtime.ShowRemove);
        Assert.False(runtime.CanRemoveOrInstall);
        runtime.IsConfirmingRemove = true;
        runtime.ConfirmRemoveCommand.Execute(null);
        Assert.Empty(rig.Manager.RemoveCalls);

        await WaitFor(() => rig.Manager.WasInstallCalled(FakeModelManager.EmbeddingId));
        rig.Manager.Complete(FakeModelManager.EmbeddingId);
        await WaitFor(() => rig.Manager.WasInstallCalled(FakeModelManager.ChatSmallId));
        rig.Manager.Complete(FakeModelManager.ChatSmallId);
        await rig.Vm.Models.LastOperation;

        Assert.False(runtime.IsRemoveLocked);
        Assert.True(runtime.ShowRemove);
        Assert.True(embedding.ShowRemove);
    }

    [Fact]
    public async Task Models_RemoveFailure_ShowsALocalizedGenericText_NotTheRawException()
    {
        var rig = new Rig();
        rig.Manager.SetInstalled(FakeModelManager.VadId);
        rig.Vm.Models!.Refresh();
        rig.Manager.RemoveFailure = new IOException("Access to the path 'C:\\secret\\silero.onnx' is denied.");
        var vad = rig.Row(FakeModelManager.VadId);

        vad.RequestRemoveCommand.Execute(null);
        vad.ConfirmRemoveCommand.Execute(null);
        await rig.Vm.Models.LastOperation;

        Assert.Equal(Strings.Models_RemoveFailed, vad.Error);
        Assert.DoesNotContain("secret", vad.Error, StringComparison.Ordinal);
        Assert.True(vad.IsInstalled);
    }

    [Fact]
    public async Task Models_ASecondChatInstall_IsRefusedWhileABundleRuns_AndWorksAfterwards()
    {
        var rig = new Rig();
        var small = rig.Row(FakeModelManager.ChatSmallId);
        var large = rig.Row(FakeModelManager.ChatLargeId);

        small.InstallCommand.Execute(null);
        var first = rig.Vm.Models!.LastOperation;
        large.InstallCommand.Execute(null);

        Assert.Equal(Strings.Models_WaitForOther, large.Error);
        Assert.True(large.HasError);
        Assert.Equal(ModelRowState.NotInstalled, large.State);
        Assert.False(rig.Manager.WasInstallCalled(FakeModelManager.ChatLargeId));
        Assert.Same(first, rig.Vm.Models.LastOperation);

        foreach (var id in new[] { FakeModelManager.RuntimeId, FakeModelManager.EmbeddingId, FakeModelManager.ChatSmallId })
        {
            await WaitFor(() => rig.Manager.WasInstallCalled(id));
            rig.Manager.Complete(id);
        }

        await first;
        large.InstallCommand.Execute(null);
        Assert.Null(large.Error);
        Assert.Equal(ModelRowState.Downloading, large.State);
        rig.Vm.Models.Dispose();
    }

    [Fact]
    public async Task Models_SavingTheChatModelFails_TheErrorTextOfTheWriterGoesToTheRow()
    {
        var rig = new Rig(store: new FailingStore());
        rig.Manager.SetInstalled(FakeModelManager.RuntimeId);
        rig.Manager.SetInstalled(FakeModelManager.EmbeddingId);
        rig.Vm.Models!.Refresh();
        var chat = rig.Row(FakeModelManager.ChatSmallId);

        chat.InstallCommand.Execute(null);
        await WaitFor(() => rig.Manager.WasInstallCalled(FakeModelManager.ChatSmallId));
        rig.Manager.Complete(FakeModelManager.ChatSmallId);
        await rig.Vm.Models.LastOperation;

        Assert.True(chat.IsInstalled);
        Assert.NotNull(chat.Error);
        Assert.Contains("disk is read-only", chat.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Models_ForgettingTheChatModelFails_TheErrorTextOfTheWriterGoesToTheRow()
    {
        var rig = new Rig(store: new FailingStore());
        rig.Manager.SetInstalled(FakeModelManager.ChatSmallId);
        rig.Vm.Models!.Refresh();
        var chat = rig.Row(FakeModelManager.ChatSmallId);

        chat.MakeDefaultCommand.Execute(null);
        await rig.Vm.Models.LastOperation;

        Assert.Contains("disk is read-only", chat.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("en")]
    public async Task Models_InstallFailure_IsNeutral_AndAFullDiskHasItsOwnText(string language)
    {
        using var scope = UiCulture.Override(language);
        var rig = new Rig();
        var vad = rig.Row(FakeModelManager.VadId);
        var asr = rig.Row(FakeModelManager.AsrId);

        vad.InstallCommand.Execute(null);
        var first = rig.Vm.Models!.LastOperation;
        rig.Manager.Fail(FakeModelManager.VadId, "SHA-256 mismatch for 'vad'");
        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        asr.InstallCommand.Execute(null);
        var second = rig.Vm.Models.LastOperation;
        rig.Manager.Fail(FakeModelManager.AsrId, "Not enough disk space for 'asr': 700 bytes needed, 10 free in /models");
        await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(Strings.Models_InstallFailed, vad.Error);
        Assert.Equal(Strings.Models_NoSpace, asr.Error);
        Assert.NotEqual(vad.Error, asr.Error);
        Assert.Equal(language == "en" ? "Install failed. Try again." : "Не вдалося встановити. Спробуйте ще раз.", vad.Error);
        Assert.DoesNotContain(language == "en" ? "connection" : "зʼєднання", vad.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Models_OutOfSpaceIsRecognisedFromTheFileSystemErrorToo()
    {
        var rig = new Rig();
        var vad = rig.Row(FakeModelManager.VadId);
        var full = OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28;

        vad.MarkFailed(new IOException("Locale specific text.", full));

        Assert.Equal(Strings.Models_NoSpace, vad.Error);
    }

    [Fact]
    public void Models_OtherErrorCodes_AreNotMistakenForAFullDisk()
    {
        var rig = new Rig();
        var vad = rig.Row(FakeModelManager.VadId);

        // ENOTEMPTY (39) and the low word 0x70 of an unrelated HRESULT must not match.
        vad.MarkFailed(new IOException("Directory not empty.", 39));
        Assert.Equal(Strings.Models_InstallFailed, vad.Error);
        vad.MarkFailed(new IOException("Other.", unchecked((int)0x80030070)));
        Assert.Equal(Strings.Models_InstallFailed, vad.Error);
    }

    [Fact]
    public async Task Models_AFailureWhileTheDriveIsTooSmall_ShowsTheNoSpaceText_WhateverTheMessageSays()
    {
        var rig = new Rig();
        rig.Manager.AvailableFreeSpaceBytes = 1_000_000;
        var asr = rig.Row(FakeModelManager.AsrId);

        asr.InstallCommand.Execute(null);
        var operation = rig.Vm.Models!.LastOperation;
        rig.Manager.Fail(FakeModelManager.AsrId, "Zu wenig Speicherplatz");
        await operation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(Strings.Models_NoSpace, asr.Error);
    }

    [Fact]
    public async Task Models_TheWaitText_IsClearedWhenTheOtherBundleFinishes()
    {
        var rig = new Rig();
        var small = rig.Row(FakeModelManager.ChatSmallId);
        var large = rig.Row(FakeModelManager.ChatLargeId);
        small.InstallCommand.Execute(null);
        var first = rig.Vm.Models!.LastOperation;
        large.InstallCommand.Execute(null);
        Assert.Equal(Strings.Models_WaitForOther, large.Error);

        foreach (var id in new[] { FakeModelManager.RuntimeId, FakeModelManager.EmbeddingId, FakeModelManager.ChatSmallId })
        {
            await WaitFor(() => rig.Manager.WasInstallCalled(id));
            rig.Manager.Complete(id);
        }

        await first;

        Assert.Null(large.Error);
    }

    [Fact]
    public void Models_RuntimeAndEmbedding_AreNotLockedByAnAsrOnlyBundle()
    {
        var rig = new Rig();
        rig.Manager.SetInstalled(FakeModelManager.RuntimeId);
        rig.Manager.SetInstalled(FakeModelManager.EmbeddingId);
        rig.Vm.Models!.Refresh();

        rig.Row(FakeModelManager.AsrId).InstallCommand.Execute(null);

        Assert.True(rig.Row(FakeModelManager.RuntimeId).ShowRemove);
        Assert.True(rig.Row(FakeModelManager.EmbeddingId).ShowRemove);
        Assert.True(rig.Row(FakeModelManager.AsrId).IsRemoveLocked);
        rig.Vm.Models.Dispose();
    }

    // 2. Start-up budget ----------------------------------------------------------------------------------------------

    [Fact]
    public void Settings_SectionViewModels_AreCreatedOnFirstUse_NotInTheConstructor()
    {
        var rig = new Rig();

        Assert.False(rig.Vm.AreSectionsCreated);
        Assert.Equal(0, rig.Capture.Inner.DeviceQueries);
        Assert.Equal(0, rig.Manager.StatusReads);
        Assert.Equal(6, rig.Vm.Sections.Count);
        Assert.True(rig.Vm.HasModels);

        var devices = rig.Vm.Devices;

        Assert.True(rig.Capture.Inner.DeviceQueries > 0);
        Assert.Same(devices, rig.Vm.Devices);
        Assert.Equal(0, rig.Manager.StatusReads);
        Assert.NotNull(rig.Vm.Models);
        Assert.True(rig.Manager.StatusReads > 0);
        Assert.Same(rig.Vm.Models, rig.Vm.Models);
    }

    [Fact]
    public void Shell_Create_DoesNotBuildTheSettingsSections_UntilTheSettingsAreShown()
    {
        var capture = new FakeDisplayCapture();
        var manager = new FakeModelManager();
        var store = new FakeSettingsStore(new AppSettings { OnboardingCompleted = true });
        var services = new AppServices(new FakeRecorder(), capture, new FakeSessionStore(), new TestTime(Now), Settings: store, Models: manager);

        var shell = ShellViewModel.Create(services);

        Assert.NotNull(shell.Settings);
        Assert.False(shell.Settings.AreSectionsCreated);
        Assert.Equal(0, manager.StatusReads);
        Assert.Equal(0, manager.FreeSpaceReads);

        shell.ShowSettings();
        Assert.Same(shell.Settings, shell.CurrentPage);
        _ = shell.Settings.Models;
        _ = shell.Settings.Devices;

        Assert.True(shell.Settings.AreSectionsCreated);
        Assert.True(manager.StatusReads > 0);
        Assert.True(manager.FreeSpaceReads > 0);
    }

    [Fact]
    public void Onboarding_SharesTheSectionsWithTheSettings_WhoeverNeedsThemFirstCreatesThem()
    {
        var rig = new OnboardingRig();

        Assert.NotNull(rig.Shell.Onboarding);
        Assert.Same(rig.Settings.Devices, rig.Onboarding.Preferences.Devices);
        Assert.Same(rig.Settings.Storage, rig.Onboarding.Preferences.Storage);
        Assert.Same(rig.Settings.Hotkeys, rig.Onboarding.Preferences.Hotkeys);
    }

    [Fact]
    public void Shell_OnWindowClosed_DisposesTheSettings()
    {
        var store = new FakeSettingsStore(new AppSettings { OnboardingCompleted = true });
        var shell = ShellViewModel.Create(new AppServices(new FakeRecorder(), new FakeDisplayCapture(), new FakeSessionStore(), new TestTime(Now), Settings: store, Models: new FakeModelManager()));
        _ = shell.Settings!.Devices;
        var before = store.SubscriberCount;

        shell.OnWindowClosed();

        // The Settings screen's own handler, and the shell's (the record-hotkey hint) go with the window.
        Assert.True(before >= 2);
        Assert.Equal(0, store.SubscriberCount);
    }

    // 3. Devices ------------------------------------------------------------------------------------------------------

    [Fact]
    public void RecordingScreen_ASavedDeviceThatIsGone_FallsBackToTheSystemDefault_NotToTheLastPick()
    {
        var store = new FakeSettingsStore(new AppSettings { MicrophoneDeviceId = "mic-usb", SystemAudioDeviceId = "out-hdmi" });
        var capture = new FakeCapture();
        var recording = new RecordingViewModel(new FakeRecorder(), capture, new FakeSessionStore(), action => action(), store);
        Assert.Equal("mic-usb", recording.SelectedMicrophone!.Id);
        Assert.Equal("out-hdmi", recording.SelectedSystemDevice!.Id);

        // Unplugged; the user also picked another one by hand in the meantime.
        capture.Microphones.RemoveAll(m => m.Id == "mic-usb");
        capture.Outputs.RemoveAll(m => m.Id == "out-hdmi");
        recording.Prepare();

        Assert.Equal("mic-default", recording.SelectedMicrophone!.Id);
        Assert.Equal("out-default", recording.SelectedSystemDevice!.Id);
    }

    // 4. Settings follow the store ------------------------------------------------------------------------------------

    [Fact]
    public async Task Settings_FollowChangesSavedByOthers_OnTheUiThread_AndStopOnDispose()
    {
        var rig = new Rig(new AppSettings { Theme = ThemePreference.Light });
        var store = (FakeSettingsStore)rig.Store;
        var devices = rig.Vm.Devices;
        var appearance = rig.Vm.Appearance;
        var storage = rig.Vm.Storage;
        var queries = rig.Capture.Inner.DeviceQueries;
        rig.Hold = true;

        // Someone else (the onboarding, the hotkey task) saves.
        await store.SaveAsync(store.Current with { Theme = ThemePreference.Dark, ScreenByDefault = true, MicrophoneDeviceId = "mic-usb", RecordingsDirectory = "/data/elsewhere" }, CancellationToken.None);

        Assert.Equal(ThemePreference.Light, appearance.Theme);
        Assert.Single(rig.Posted);
        rig.Flush();

        Assert.Equal(ThemePreference.Dark, appearance.Theme);
        Assert.True(devices.ScreenByDefault);
        Assert.Equal("mic-usb", devices.SelectedMicrophone!.Id);
        Assert.Equal("/data/elsewhere", storage.EffectivePath);
        Assert.Equal(queries, rig.Capture.Inner.DeviceQueries);
        Assert.Equal(1, store.SaveCount);

        rig.Vm.Dispose();
        Assert.Equal(0, store.SubscriberCount);
        await store.SaveAsync(store.Current with { Theme = ThemePreference.System }, CancellationToken.None);
        Assert.Empty(rig.Posted);
    }

    [Fact]
    public async Task Settings_AChangeThatArrivesAfterDispose_IsIgnored()
    {
        var rig = new Rig();
        var store = (FakeSettingsStore)rig.Store;
        var appearance = rig.Vm.Appearance;
        rig.Hold = true;
        await store.SaveAsync(store.Current with { Theme = ThemePreference.Dark }, CancellationToken.None);

        rig.Vm.Dispose();
        rig.Flush();

        Assert.Equal(ThemePreference.System, appearance.Theme);
    }

    [Fact]
    public async Task Settings_OwnSaves_DoNotDisturbTheSections()
    {
        var rig = new Rig();
        var devices = rig.Vm.Devices;

        devices.SelectedMicrophone = devices.Microphones.Single(m => m.Id == "mic-usb");
        await devices.SaveTask;

        Assert.Equal("mic-usb", devices.SelectedMicrophone.Id);
        Assert.Equal("mic-usb", rig.Store.Current.MicrophoneDeviceId);
    }

    [Fact]
    public async Task Settings_AnUnrelatedSave_KeepsErrorsStatusAndCaptureOfTheHotkeysAndAppearance()
    {
        var rig = new Rig();
        var store = (FakeSettingsStore)rig.Store;
        var hotkeys = rig.Vm.Hotkeys;
        var appearance = rig.Vm.Appearance;
        var record = hotkeys.Rows[0];
        var toggle = hotkeys.Rows[2];
        record.Error = "taken by another app";
        hotkeys.Error = "section error";
        appearance.Error = "could not save the theme";
        toggle.CaptureCommand.Execute(null);
        Assert.True(toggle.IsCapturing);

        await store.SaveAsync(store.Current with { ScreenByDefault = true, ShowAppHotkey = "Ctrl+Alt+K" }, CancellationToken.None);

        Assert.Equal("taken by another app", record.Error);
        Assert.Equal("section error", hotkeys.Error);
        Assert.Equal("could not save the theme", appearance.Error);
        Assert.True(toggle.IsCapturing);
        Assert.Same(toggle, hotkeys.Capturing);
        Assert.Equal("Ctrl+Alt+K", hotkeys.Rows[3].Gesture);
    }

    [Fact]
    public void Settings_AfterDispose_NoNewSectionIsCreated()
    {
        var rig = new Rig();
        var devices = rig.Vm.Devices;
        rig.Vm.Dispose();

        Assert.Same(devices, rig.Vm.Devices);
        Assert.Null(rig.Vm.Models);
        Assert.Throws<ObjectDisposedException>(() => rig.Vm.Storage);
        Assert.Throws<ObjectDisposedException>(() => rig.Vm.Hotkeys);
        Assert.Throws<ObjectDisposedException>(() => rig.Vm.Appearance);
        Assert.Equal(0, rig.Manager.StatusReads);
    }

    [Fact]
    public void Shell_AfterTheWindowClosed_NeitherTheOnboardingNorTheSettingsOpen()
    {
        var rig = new OnboardingRig(new AppSettings { OnboardingCompleted = true });
        rig.Shell.OnWindowClosed();

        rig.Shell.ShowOnboarding();
        rig.Shell.ShowSettings();

        Assert.Null(rig.Shell.Onboarding);
        Assert.NotSame(rig.Settings, rig.Shell.CurrentPage);
    }

    // 5. Storage ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("recordings")]
    [InlineData("../recordings")]
    [InlineData("~/recordings")]
    public async Task Storage_ARelativeFolderFromThePicker_IsRefused(string chosen)
    {
        var rig = new Rig();
        var storage = rig.Vm.Storage;
        storage.FolderPicker = new FakeFolderPicker { Result = chosen };

        await storage.ChangeCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Storage_NotAbsolute, storage.Error);
        Assert.Null(rig.Store.Current.RecordingsDirectory);
        Assert.True(storage.IsDefaultFolder);
    }

    [Fact]
    public async Task Storage_AnAbsoluteFolder_IsNormalizedBeforeItIsSaved()
    {
        var rig = new Rig();
        var storage = rig.Vm.Storage;
        var root = Path.Combine(Path.GetTempPath(), "murmur-polish");
        storage.FolderPicker = new FakeFolderPicker { Result = Path.Combine(root, "a", "..", "recordings") };

        await storage.ChangeCommand.ExecuteAsync(null);

        Assert.Null(storage.Error);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "recordings")), rig.Store.Current.RecordingsDirectory);
        Assert.DoesNotContain("..", rig.Store.Current.RecordingsDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Storage_OpeningARelativeSavedFolder_CreatesNothingAndOpensNothing()
    {
        var rig = new Rig(new AppSettings { RecordingsDirectory = "murmur-polish-relative" });

        rig.Vm.Storage.OpenCommand.Execute(null);

        Assert.Equal(Strings.Storage_NotAbsolute, rig.Vm.Storage.Error);
        Assert.Empty(rig.Launcher.Opened);
        Assert.False(Directory.Exists(Path.GetFullPath("murmur-polish-relative")));
    }

    // 6. About --------------------------------------------------------------------------------------------------------

    [Fact]
    public void About_TheVersionIsABuildTimeConstant_WithoutARevisionSuffix()
    {
        Assert.Equal("0.4.0", BuildInfo.Version);
        Assert.DoesNotContain('+', BuildInfo.Version);
        Assert.Equal(BuildInfo.Version, new AboutViewModel().Version);
        Assert.Equal("9.9", new AboutViewModel("9.9").Version);
    }

    // 7. Hotkey display -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Ctrl+Shift+R", "Ctrl Shift R")]
    [InlineData("Ctrl+Shift+D1", "Ctrl Shift 1")]
    [InlineData("Ctrl+D0", "Ctrl 0")]
    [InlineData("Ctrl+OemComma", "Ctrl ,")]
    [InlineData("Ctrl+Alt+OemPeriod", "Ctrl Alt .")]
    [InlineData("Ctrl+OemMinus", "Ctrl -")]
    [InlineData("Ctrl+Oem3", "Ctrl `")]
    [InlineData("Ctrl+Oem4", "Ctrl [")]
    [InlineData("Ctrl+Oem6", "Ctrl ]")]
    [InlineData("Ctrl+Oem1", "Ctrl ;")]
    [InlineData("Ctrl+Oem7", "Ctrl '")]
    [InlineData("ctrl+shift+f9", "Ctrl Shift F9")]
    [InlineData("F13", "F13")]
    [InlineData("Ctrl+Space", "Ctrl Space")]
    [InlineData("nonsense+", "nonsense ")]
    public void HotkeyDisplay_ShowsDigitAndOemKeysAsTheirCharacters(string stored, string expected)
    {
        Assert.Equal(expected, HotkeyGestures.Display(stored));
    }

    [Fact]
    public void HotkeyDisplay_DoesNotChangeTheStoredValue()
    {
        Assert.True(HotkeyGestures.Same("Ctrl+D1", "ctrl+d1"));
        Assert.Equal("Ctrl+Shift+D1", HotkeyGestures.TryFormat(Key.D1, KeyModifiers.Control | KeyModifiers.Shift, out _));
    }

    // 8. English copy -------------------------------------------------------------------------------------------------

    [Fact]
    public void English_ThemeSystemIsMatchSystem()
    {
        using var scope = UiCulture.Override("en");
        Assert.Equal("Match system", Strings.Appearance_ThemeSystem);
    }

    // 9. The library sees a background recovery -----------------------------------------------------------------------

    private static SessionRecord Stuck() => Sessions.Make("Crashed call", Now.AddHours(-2), TimeSpan.FromMinutes(5), state: SessionState.Recording);

    [Fact]
    public async Task Library_RereadsRowsStillRecording_EveryThirtySeconds_AndUpdatesThemInPlace()
    {
        var store = new FakeSessionStore();
        var time = new TestTime(Now);
        var stuck = Stuck();
        store.Add(stuck);
        using var library = new LibraryViewModel(store, time, null, action => action(), recorder: new FakeRecorder());
        await library.LoadAsync();
        var row = library.Sessions.Single();
        Assert.Equal(Strings.Processing_Recording, row.StatusText);
        Assert.Equal(1, time.ActiveTimers);

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(Strings.Processing_Recording, row.StatusText);

        // The recovery finishes in the background.
        store.Replace(stuck with { State = SessionState.Interrupted, Duration = TimeSpan.FromMinutes(7) });
        time.Advance(TimeSpan.FromSeconds(1));
        await library.StaleRefreshTask;

        Assert.Same(row, library.Sessions.Single());
        Assert.Equal(Strings.Processing_Interrupted, row.StatusText);
        Assert.Equal("00:07:00", row.DurationText);
        Assert.Equal(SessionState.Interrupted, row.Record.State);
        Assert.Equal(0, time.ActiveTimers);
    }

    [Fact]
    public async Task Library_RereadsStaleRows_WhenTheWindowIsActivated()
    {
        var store = new FakeSessionStore();
        var stuck = Stuck();
        store.Add(stuck);
        using var library = new LibraryViewModel(store, new TestTime(Now), null, action => action(), recorder: new FakeRecorder());
        await library.LoadAsync();
        store.Replace(stuck with { State = SessionState.Failed });

        library.OnWindowActivated();
        await library.StaleRefreshTask;

        Assert.Equal(Strings.Processing_Failed, library.Sessions.Single().StatusText);
    }

    [Fact]
    public async Task Library_AStaleRowWhoseSessionIsGone_IsRemoved_AndNotPolledAgain()
    {
        var store = new FakeSessionStore();
        var time = new TestTime(Now);
        var stuck = Stuck();
        store.Add(stuck);
        store.Add(Sessions.Make("Kept", Now.AddHours(-1), TimeSpan.FromMinutes(3)));
        using var library = new LibraryViewModel(store, time, null, action => action(), recorder: new FakeRecorder());
        await library.LoadAsync();
        Assert.Equal(2, library.Sessions.Count);

        await store.DeleteSessionAsync(stuck.Id, CancellationToken.None);
        time.Advance(LibraryViewModel.StaleRefreshInterval);
        await library.StaleRefreshTask;

        Assert.Equal(["Kept"], library.Sessions.Select(s => s.Title));
        Assert.Equal(0, time.ActiveTimers);
    }

    [Fact]
    public async Task Library_TheSessionTheRecorderIsRecording_IsNotWatched()
    {
        var store = new FakeSessionStore();
        var time = new TestTime(Now);
        var recorder = new FakeRecorder();
        var current = await recorder.StartAsync(new Murmur.Core.Recording.RecordingOptions(), CancellationToken.None);
        store.Add(current);
        using var library = new LibraryViewModel(store, time, null, action => action(), recorder: recorder);

        await library.LoadAsync();

        Assert.Equal(0, time.ActiveTimers);
        Assert.Equal(Strings.Processing_Recording, library.Sessions.Single().StatusText);
    }

    [Fact]
    public async Task Library_WithoutStaleRows_KeepsNoTimer_AndDisposeStopsIt()
    {
        var store = new FakeSessionStore();
        var time = new TestTime(Now);
        store.Add(Sessions.Make("Done", Now.AddHours(-1), TimeSpan.FromMinutes(3)));
        var library = new LibraryViewModel(store, time, null, action => action(), recorder: new FakeRecorder());
        await library.LoadAsync();
        Assert.Equal(0, time.ActiveTimers);

        store.Add(Stuck());
        await library.LoadAsync();
        Assert.Equal(1, time.ActiveTimers);

        library.Dispose();
        Assert.Equal(0, time.ActiveTimers);
    }

    // 10. Window icon -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MainWindow_HasTheAppIcon()
    {
        var hasIcon = await UiHarness.Session.Dispatch(
            () =>
            {
                var window = new MainWindow();
                var result = window.Icon is not null;
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(hasIcon);
    }

    // 11. Progress bars on the Settings screen ------------------------------------------------------------------------

    [Theory]
    [InlineData("Light", "uk")]
    [InlineData("Dark", "uk")]
    [InlineData("Light", "en")]
    [InlineData("Dark", "en")]
    public async Task Settings_ModelProgressBars_AreInkOnStone_InBothThemes(string theme, string language)
    {
        var (bars, ink, stone) = await UiHarness.Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override(language);
                var rig = new Rig();
                rig.Manager.SetInstalled(FakeModelManager.AsrId);
                rig.Vm.Models!.Refresh();
                var vad = rig.Row(FakeModelManager.VadId);
                vad.InstallCommand.Execute(null);
                rig.Manager.Progress(FakeModelManager.VadId, 400_000, 643_854);
                var chat = rig.Row(FakeModelManager.ChatSmallId);
                chat.InstallCommand.Execute(null);
                rig.Manager.Progress(FakeModelManager.RuntimeId, 12_000_000, 30_000_000);

                var harness = new UiHarness();
                var shell = new ShellViewModel(
                    new LibraryViewModel(harness.Store, harness.Time, null, action => action()),
                    new RecordingViewModel(harness.Recorder, rig.Capture, harness.Store, action => action(), rig.Store),
                    settings: rig.Vm);
                shell.ShowSettings();
                var window = new MainWindow { DataContext = shell, Height = 2800, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
                window.Show();
                UiHarness.Pump(6);
                if (Environment.GetEnvironmentVariable("MURMUR_SCREENSHOT_DIR") is { Length: > 0 } directory)
                {
                    UiHarness.Save(window, Path.Combine(directory, $"settings-progress-{theme.ToLowerInvariant()}-{language}.png"));
                }

                Assert.True(window.TryFindResource("Brush.Ink", window.ActualThemeVariant, out var inkBrush));
                Assert.True(window.TryFindResource("Brush.Stone", window.ActualThemeVariant, out var stoneBrush));
                var visible = window.GetVisualDescendants().OfType<ProgressBar>().Where(b => b.IsEffectivelyVisible)
                    .Select(b => ((b.Foreground as ISolidColorBrush)?.Color, (b.Background as ISolidColorBrush)?.Color)).ToArray();
                window.Close();
                rig.Vm.Dispose();
                return (visible, ((ISolidColorBrush)inkBrush!).Color, ((ISolidColorBrush)stoneBrush!).Color);
            },
            TestContext.Current.CancellationToken);

        Assert.True(bars.Length >= 2, "The downloading rows show progress bars.");
        Assert.All(bars, bar =>
        {
            Assert.Equal(ink, bar.Item1);
            Assert.Equal(stone, bar.Item2);
        });
    }
}
