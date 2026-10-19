using Avalonia.Input;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Models;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>The Settings screen's view models: models, devices, storage, hotkeys, appearance and about, against fake services.</summary>
public sealed class SettingsViewModelTests
{
    private sealed class Rig
    {
        public Rig(AppSettings? initial = null, Func<ModelKind, string?>? activeModel = null, bool withModels = true, string? defaultFolder = "/data/Recordings")
        {
            Store = new FakeSettingsStore(initial);
            Manager = new FakeModelManager();
            Launcher = new FakeFolderLauncher();
            Vm = new SettingsViewModel(
                Store,
                Capture,
                withModels ? Manager : null,
                activeModel,
                defaultFolder,
                Launcher,
                action => action(),
                version: "1.2.3");
        }

        public FakeSettingsStore Store { get; }

        public FakeModelManager Manager { get; }

        public FakeFolderLauncher Launcher { get; }

        public FakeDisplayCapture Capture { get; } = new();

        public SettingsViewModel Vm { get; }

        public ModelRowViewModel Row(string id) => Vm.Models!.Rows.Single(r => r.Id == id);
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

    // Models ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Models_OneRowPerCatalogItem_WithNameSizeAndState()
    {
        using var en = UiCulture.Override("en");
        var rig = new Rig();
        rig.Manager.SetInstalled(FakeModelManager.AsrId);
        rig.Vm.Refresh();

        var rows = rig.Vm.Models!.Rows;
        Assert.Equal(rig.Manager.Catalog.Select(m => m.Id), rows.Select(r => r.Id));
        var asr = rig.Row(FakeModelManager.AsrId);
        Assert.Equal("Parakeet TDT 0.6B v3 (int8)", asr.Name);
        Assert.Equal(ModelRowState.Installed, asr.State);
        Assert.Equal("0.67 GB", asr.SizeText);
        Assert.Equal("30 MB", rig.Row(FakeModelManager.RuntimeId).SizeText);
        Assert.Equal(ModelRowState.NotInstalled, rig.Row(FakeModelManager.VadId).State);
        Assert.True(rig.Row(FakeModelManager.VadId).ShowInstall);
        Assert.True(asr.ShowRemove);
        Assert.Equal("Free on this drive: 123.46 GB", rig.Vm.Models.FreeSpaceText);
        Assert.Contains("/models", rig.Vm.Models.FolderText, StringComparison.Ordinal);
    }

    [Fact]
    public void Models_RecommendedChatIsMarked_UntilOneIsInstalled_ThenTheActiveOneIsTheDefault()
    {
        var rig = new Rig(activeModel: kind => kind == ModelKind.Chat ? FakeModelManager.ChatLargeId : null);
        Assert.True(rig.Row(FakeModelManager.ChatSmallId).IsRecommended);
        Assert.Equal(Strings.Models_Recommended, rig.Row(FakeModelManager.ChatSmallId).BadgeText);
        Assert.False(rig.Row(FakeModelManager.ChatLargeId).HasBadge);
        Assert.All(rig.Vm.Models!.Rows, r => Assert.False(r.IsDefault));

        rig.Manager.SetInstalled(FakeModelManager.ChatSmallId);
        rig.Manager.SetInstalled(FakeModelManager.ChatLargeId);
        rig.Vm.Refresh();

        Assert.True(rig.Row(FakeModelManager.ChatLargeId).IsDefault);
        Assert.Equal(Strings.Models_Default, rig.Row(FakeModelManager.ChatLargeId).BadgeText);
        Assert.False(rig.Row(FakeModelManager.ChatSmallId).IsDefault);
        Assert.False(rig.Row(FakeModelManager.ChatSmallId).IsRecommended);
        Assert.True(rig.Row(FakeModelManager.ChatSmallId).ShowMakeDefault);
    }

    [Fact]
    public void Models_DefaultFallsBackToTheSavedChatModel_ThenToTheFirstInstalledOne()
    {
        var rig = new Rig(new AppSettings { ChatModelId = FakeModelManager.ChatLargeId });
        rig.Manager.SetInstalled(FakeModelManager.ChatSmallId);
        rig.Manager.SetInstalled(FakeModelManager.ChatLargeId);
        rig.Vm.Refresh();
        Assert.True(rig.Row(FakeModelManager.ChatLargeId).IsDefault);

        var other = new Rig();
        other.Manager.SetInstalled(FakeModelManager.ChatLargeId);
        other.Vm.Refresh();
        Assert.True(other.Row(FakeModelManager.ChatLargeId).IsDefault);
    }

    [Fact]
    public async Task Install_ShowsProgress_ThenInstalled_AndFreeSpaceIsReRead()
    {
        using var en = UiCulture.Override("en");
        var rig = new Rig();
        var vad = rig.Row(FakeModelManager.VadId);

        vad.InstallCommand.Execute(null);
        Assert.Equal(ModelRowState.Downloading, vad.State);
        Assert.True(vad.ShowCancel);
        Assert.False(vad.ShowInstall);

        rig.Manager.Progress(FakeModelManager.VadId, 160_000, 640_000);
        Assert.Equal(25, vad.Percent);
        Assert.Equal("25%", vad.PercentText);
        Assert.Equal("160 KB of 640 KB", vad.TransferText);
        Assert.True(vad.ShowProgress);
        Assert.False(vad.ShowSize);

        rig.Manager.AvailableFreeSpaceBytes = 99_000_000_000;
        rig.Manager.Complete(FakeModelManager.VadId);
        await rig.Vm.Models!.LastOperation;

        Assert.Equal(ModelRowState.Installed, vad.State);
        Assert.True(vad.IsInstalled);
        Assert.False(vad.ShowProgress);
        Assert.Null(vad.Error);
        Assert.Equal("Free on this drive: 99.00 GB", rig.Vm.Models.FreeSpaceText);
    }

    [Fact]
    public async Task Install_UsesAnAppLifetimeTokenPerItem_AndCancelStopsOnlyThatItem()
    {
        var rig = new Rig();
        var vad = rig.Row(FakeModelManager.VadId);
        var asr = rig.Row(FakeModelManager.AsrId);
        vad.InstallCommand.Execute(null);
        var vadTask = rig.Vm.Models!.LastOperation;
        asr.InstallCommand.Execute(null);
        var asrTask = rig.Vm.Models.LastOperation;
        Assert.True(rig.Manager.TokenOf(FakeModelManager.VadId).CanBeCanceled);

        vad.CancelCommand.Execute(null);
        await vadTask;

        Assert.True(rig.Manager.TokenOf(FakeModelManager.VadId).IsCancellationRequested);
        Assert.False(rig.Manager.TokenOf(FakeModelManager.AsrId).IsCancellationRequested);
        Assert.Equal(ModelRowState.NotInstalled, vad.State);
        Assert.Null(vad.Error);
        Assert.True(vad.ShowInstall);
        Assert.Equal(ModelRowState.Downloading, asr.State);

        rig.Manager.Complete(FakeModelManager.AsrId);
        await asrTask;
        Assert.True(asr.IsInstalled);
    }

    [Fact]
    public async Task Install_Failure_ShowsAShortTextOnTheRow_AndRetryWorks()
    {
        var rig = new Rig();
        var vad = rig.Row(FakeModelManager.VadId);
        vad.InstallCommand.Execute(null);
        var first = rig.Vm.Models!.LastOperation;

        rig.Manager.Fail(FakeModelManager.VadId, "HTTP 503 from the CDN");
        await first;

        Assert.Equal(ModelRowState.Failed, vad.State);
        Assert.Equal(Strings.Models_InstallFailed, vad.Error);
        Assert.DoesNotContain("503", vad.Error, StringComparison.Ordinal);
        Assert.True(vad.HasError);
        Assert.Equal(Strings.Models_Retry, vad.InstallLabel);
        Assert.True(vad.ShowInstall);

        vad.InstallCommand.Execute(null);
        Assert.Equal(ModelRowState.Downloading, vad.State);
        Assert.Null(vad.Error);
        rig.Manager.Complete(FakeModelManager.VadId);
        await rig.Vm.Models.LastOperation;
        Assert.True(vad.IsInstalled);
        Assert.Equal(2, rig.Manager.InstallCalls.Count(id => id == FakeModelManager.VadId));
    }

    [Fact]
    public async Task AiBundle_InstallsTheRuntimeAndTheEmbeddingModelFirst_ThenTheChatModel_WithProgressPerRow()
    {
        var rig = new Rig();
        var chat = rig.Row(FakeModelManager.ChatSmallId);
        var runtime = rig.Row(FakeModelManager.RuntimeId);
        var embedding = rig.Row(FakeModelManager.EmbeddingId);

        chat.InstallCommand.Execute(null);
        var bundle = rig.Vm.Models!.LastOperation;

        Assert.Equal([FakeModelManager.RuntimeId], rig.Manager.InstallCalls);
        Assert.Equal(ModelRowState.Downloading, runtime.State);
        Assert.Equal(ModelRowState.Queued, embedding.State);
        Assert.Equal(ModelRowState.Queued, chat.State);
        Assert.True(chat.ShowCancel);
        rig.Manager.Progress(FakeModelManager.RuntimeId, 15_000_000, 30_000_000);
        Assert.Equal(50, runtime.Percent);

        rig.Manager.Complete(FakeModelManager.RuntimeId);
        await WaitFor(() => rig.Manager.WasInstallCalled(FakeModelManager.EmbeddingId));
        Assert.True(runtime.IsInstalled);
        Assert.Equal(ModelRowState.Downloading, embedding.State);
        Assert.Equal(ModelRowState.Queued, chat.State);
        Assert.False(rig.Manager.WasInstallCalled(FakeModelManager.ChatSmallId));

        rig.Manager.Complete(FakeModelManager.EmbeddingId);
        await WaitFor(() => rig.Manager.WasInstallCalled(FakeModelManager.ChatSmallId));
        Assert.Equal(ModelRowState.Downloading, chat.State);
        rig.Manager.Progress(FakeModelManager.ChatSmallId, 2_500_000_000, 5_000_000_000);
        Assert.Equal(50, chat.Percent);

        rig.Manager.Complete(FakeModelManager.ChatSmallId);
        await bundle;

        Assert.Equal([FakeModelManager.RuntimeId, FakeModelManager.EmbeddingId, FakeModelManager.ChatSmallId], rig.Manager.InstallCalls);
        Assert.All(new[] { runtime, embedding, chat }, r => Assert.True(r.IsInstalled));
        Assert.True(chat.IsDefault);
        Assert.Equal(FakeModelManager.ChatSmallId, rig.Store.Current.ChatModelId);
    }

    [Fact]
    public async Task AiBundle_SkipsTheDependenciesThatAreAlreadyInstalled()
    {
        var rig = new Rig();
        rig.Manager.SetInstalled(FakeModelManager.RuntimeId);
        rig.Manager.SetInstalled(FakeModelManager.EmbeddingId);
        rig.Vm.Refresh();

        rig.Row(FakeModelManager.ChatLargeId).InstallCommand.Execute(null);
        var bundle = rig.Vm.Models!.LastOperation;
        Assert.Equal([FakeModelManager.ChatLargeId], rig.Manager.InstallCalls);

        rig.Manager.Complete(FakeModelManager.ChatLargeId);
        await bundle;
        Assert.True(rig.Row(FakeModelManager.ChatLargeId).IsDefault);
    }

    [Fact]
    public async Task AiBundle_CancelOnTheChatRow_StopsTheRunningDependency_AndNothingElseStarts()
    {
        var rig = new Rig();
        var chat = rig.Row(FakeModelManager.ChatSmallId);
        chat.InstallCommand.Execute(null);
        var bundle = rig.Vm.Models!.LastOperation;

        chat.CancelCommand.Execute(null);
        await bundle;

        Assert.True(rig.Manager.TokenOf(FakeModelManager.RuntimeId).IsCancellationRequested);
        Assert.Equal([FakeModelManager.RuntimeId], rig.Manager.InstallCalls);
        Assert.All(new[] { FakeModelManager.RuntimeId, FakeModelManager.EmbeddingId, FakeModelManager.ChatSmallId }, id =>
        {
            Assert.Equal(ModelRowState.NotInstalled, rig.Row(id).State);
            Assert.Null(rig.Row(id).Error);
        });
    }

    [Fact]
    public async Task AiBundle_WhenTheRuntimeFails_ItsRowShowsTheError_AndTheChatModelIsNotStarted()
    {
        var rig = new Rig();
        rig.Row(FakeModelManager.ChatSmallId).InstallCommand.Execute(null);
        var bundle = rig.Vm.Models!.LastOperation;

        rig.Manager.Fail(FakeModelManager.RuntimeId, "disk full");
        await bundle;

        Assert.Equal(ModelRowState.Failed, rig.Row(FakeModelManager.RuntimeId).State);
        Assert.Equal(Strings.Models_InstallFailed, rig.Row(FakeModelManager.RuntimeId).Error);
        Assert.Equal(ModelRowState.NotInstalled, rig.Row(FakeModelManager.ChatSmallId).State);
        Assert.Equal(ModelRowState.NotInstalled, rig.Row(FakeModelManager.EmbeddingId).State);
        Assert.Equal([FakeModelManager.RuntimeId], rig.Manager.InstallCalls);
        Assert.Null(rig.Store.Current.ChatModelId);
    }

    [Fact]
    public void ManagerEvents_AreMarshalledThroughThePostCallback()
    {
        var posted = new List<Action>();
        var store = new FakeSettingsStore();
        var manager = new FakeModelManager();
        var vm = new SettingsViewModel(store, new FakeDisplayCapture(), manager, null, null, new FakeFolderLauncher(), posted.Add);
        var row = vm.Models!.Rows.Single(r => r.Id == FakeModelManager.AsrId);

        manager.Raise(new ModelStatus(row.Info, ModelInstallState.Downloading, 100, 1000, null));
        Assert.Equal(ModelRowState.NotInstalled, row.State);
        Assert.Single(posted);

        posted[0]();
        Assert.Equal(ModelRowState.Downloading, row.State);
        Assert.Equal(10, row.Percent);
    }

    [Fact]
    public async Task Remove_AsksToConfirm_ThenRemoves_AndForgetsTheActiveChatModel()
    {
        var rig = new Rig(new AppSettings { ChatModelId = FakeModelManager.ChatSmallId });
        rig.Manager.SetInstalled(FakeModelManager.ChatSmallId);
        rig.Vm.Refresh();
        var chat = rig.Row(FakeModelManager.ChatSmallId);
        Assert.True(chat.IsDefault);

        chat.RequestRemoveCommand.Execute(null);
        Assert.True(chat.IsConfirmingRemove);
        Assert.False(chat.ShowRemove);
        Assert.Empty(rig.Manager.RemoveCalls);

        chat.CancelRemoveCommand.Execute(null);
        Assert.False(chat.IsConfirmingRemove);
        Assert.True(chat.ShowRemove);

        chat.RequestRemoveCommand.Execute(null);
        chat.ConfirmRemoveCommand.Execute(null);
        await rig.Vm.Models!.LastOperation;

        Assert.Equal([FakeModelManager.ChatSmallId], rig.Manager.RemoveCalls);
        Assert.Equal(ModelRowState.NotInstalled, chat.State);
        Assert.False(chat.IsDefault);
        Assert.Null(rig.Store.Current.ChatModelId);
        Assert.True(chat.IsRecommended);
    }

    [Fact]
    public async Task MakeDefault_ActivatesTheInstalledChatModel_WithoutDownloading()
    {
        var rig = new Rig(new AppSettings { ChatModelId = FakeModelManager.ChatSmallId });
        rig.Manager.SetInstalled(FakeModelManager.ChatSmallId);
        rig.Manager.SetInstalled(FakeModelManager.ChatLargeId);
        rig.Vm.Refresh();

        rig.Row(FakeModelManager.ChatLargeId).MakeDefaultCommand.Execute(null);
        await rig.Vm.Models!.LastOperation;

        Assert.Equal([FakeModelManager.ChatLargeId], rig.Manager.InstallCalls);
        Assert.Equal(FakeModelManager.ChatLargeId, rig.Store.Current.ChatModelId);
        Assert.True(rig.Row(FakeModelManager.ChatLargeId).IsDefault);
        Assert.False(rig.Row(FakeModelManager.ChatSmallId).IsDefault);
    }

    [Fact]
    public void WithoutAModelManager_TheModelsSectionIsHidden()
    {
        var rig = new Rig(withModels: false);

        Assert.Null(rig.Vm.Models);
        Assert.False(rig.Vm.HasModels);
        Assert.DoesNotContain(rig.Vm.Sections, s => s.Id == SettingsViewModel.ModelsSectionId);
        Assert.Equal(
            [SettingsViewModel.DevicesSectionId, SettingsViewModel.StorageSectionId, SettingsViewModel.HotkeysSectionId, SettingsViewModel.AppearanceSectionId, SettingsViewModel.AboutSectionId],
            rig.Vm.Sections.Select(s => s.Id));
    }

    // Devices -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Devices_ListTheDefaultFirst_AndSavedChoicesAreShown()
    {
        var rig = new Rig(new AppSettings { MicrophoneDeviceId = "mic-usb", SystemAudioDeviceId = "out-hdmi", ScreenDisplayId = "display-2", ScreenByDefault = true });
        await Task.CompletedTask;

        var devices = rig.Vm.Devices;
        Assert.Equal([null, "mic-default", "mic-usb"], devices.Microphones.Select(m => m.Id));
        Assert.Equal(Strings.Devices_SystemDefault, devices.Microphones[0].Name);
        Assert.Equal("mic-usb", devices.SelectedMicrophone!.Id);
        Assert.Equal("out-hdmi", devices.SelectedSystemDevice!.Id);
        Assert.Equal(Strings.Devices_PrimaryDisplay, devices.Displays[0].Name);
        Assert.Equal("display-2", devices.SelectedDisplay!.Id);
        Assert.True(devices.ScreenByDefault);
    }

    [Fact]
    public async Task Devices_Choices_AreSavedAtOnce()
    {
        var rig = new Rig();
        var devices = rig.Vm.Devices;
        Assert.Null(devices.SelectedMicrophone!.Id);

        devices.SelectedMicrophone = devices.Microphones.Single(m => m.Id == "mic-usb");
        await devices.SaveTask;
        devices.SelectedSystemDevice = devices.SystemDevices.Single(m => m.Id == "out-hdmi");
        await devices.SaveTask;
        devices.SelectedDisplay = devices.Displays.Single(m => m.Id == "display-2");
        await devices.SaveTask;
        devices.ScreenByDefault = true;
        await devices.SaveTask;

        var saved = rig.Store.Current;
        Assert.Equal("mic-usb", saved.MicrophoneDeviceId);
        Assert.Equal("out-hdmi", saved.SystemAudioDeviceId);
        Assert.Equal("display-2", saved.ScreenDisplayId);
        Assert.True(saved.ScreenByDefault);
        Assert.Equal(4, rig.Store.SaveCount);

        devices.SelectedMicrophone = devices.Microphones[0];
        await devices.SaveTask;
        Assert.Null(rig.Store.Current.MicrophoneDeviceId);
    }

    [Fact]
    public void Devices_AnUnpluggedSavedDevice_ShowsAsTheDefault_WithoutOverwritingTheSetting()
    {
        var rig = new Rig(new AppSettings { MicrophoneDeviceId = "mic-gone" });

        Assert.Null(rig.Vm.Devices.SelectedMicrophone!.Id);
        Assert.Equal("mic-gone", rig.Store.Current.MicrophoneDeviceId);
        Assert.Equal(0, rig.Store.SaveCount);
    }

    [Fact]
    public async Task Devices_SaveFailure_ShowsAnInlineError()
    {
        var store = new ThrowingStore();
        var vm = new SettingsViewModel(store, new FakeDisplayCapture(), null, null, null, new FakeFolderLauncher(), action => action());

        vm.Devices.ScreenByDefault = true;
        await vm.Devices.SaveTask;

        Assert.True(vm.Devices.HasError);
        Assert.Contains("disk is read-only", vm.Devices.Error, StringComparison.Ordinal);
    }

    private sealed class ThrowingStore : ISettingsStore
    {
        public AppSettings Current { get; } = new();

        public event Action<AppSettings>? Changed
        {
            add { }
            remove { }
        }

        public ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken) =>
            throw new IOException("disk is read-only");
    }

    // The recording screen picks the defaults up ---------------------------------------------------------------------

    [Fact]
    public async Task RecordingScreen_StartsWithTheSavedDevices_Screen_AndDisplay()
    {
        var rig = new Rig();
        var recorder = new FakeRecorder();
        var sessions = new FakeSessionStore();
        var recording = new RecordingViewModel(recorder, rig.Capture, sessions, action => action(), rig.Store);
        Assert.Equal("mic-default", recording.SelectedMicrophone!.Id);
        Assert.False(recording.Screen);

        var devices = rig.Vm.Devices;
        devices.SelectedMicrophone = devices.Microphones.Single(m => m.Id == "mic-usb");
        devices.SelectedSystemDevice = devices.SystemDevices.Single(m => m.Id == "out-hdmi");
        devices.SelectedDisplay = devices.Displays.Single(m => m.Id == "display-2");
        devices.ScreenByDefault = true;
        await rig.Vm.LastSave;

        recording.Prepare();
        Assert.Equal("mic-usb", recording.SelectedMicrophone!.Id);
        Assert.Equal("out-hdmi", recording.SelectedSystemDevice!.Id);
        Assert.True(recording.Screen);

        await recording.StartStopAsync();
        var options = Assert.Single(recorder.Starts);
        Assert.Equal("mic-usb", options.MicrophoneDeviceId);
        Assert.Equal("out-hdmi", options.SystemDeviceId);
        Assert.True(options.Screen);
        Assert.Equal("display-2", options.DisplayId);
    }

    [Fact]
    public async Task RecordingScreen_WithTheDefaultsSaved_UsesTheSystemDefaults_AndIgnoresAnUnpluggedDisplay()
    {
        var rig = new Rig(new AppSettings { MicrophoneDeviceId = "mic-gone", ScreenDisplayId = "display-gone", ScreenByDefault = true });
        var recorder = new FakeRecorder();
        var recording = new RecordingViewModel(recorder, rig.Capture, new FakeSessionStore(), action => action(), rig.Store);

        Assert.Equal("mic-default", recording.SelectedMicrophone!.Id);
        Assert.True(recording.Screen);
        await recording.StartStopAsync();

        var options = Assert.Single(recorder.Starts);
        Assert.Null(options.MicrophoneDeviceId);
        Assert.Null(options.DisplayId);
        Assert.True(options.Screen);
    }

    [Fact]
    public void RecordingScreen_WithoutSettings_KeepsItsOwnDefaults()
    {
        var capture = new FakeDisplayCapture();
        var recording = new RecordingViewModel(new FakeRecorder(), capture, new FakeSessionStore(), action => action());

        Assert.False(recording.Screen);
        Assert.Equal("mic-default", recording.SelectedMicrophone!.Id);
    }

    // Storage -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Storage_ShowsTheEffectivePath_ChangeSavesTheChosenFolder_ResetGoesBackToTheDefault()
    {
        var rig = new Rig();
        var storage = rig.Vm.Storage;
        var picker = new FakeFolderPicker();
        storage.FolderPicker = picker;
        Assert.Equal("/data/Recordings", storage.EffectivePath);
        Assert.True(storage.IsDefaultFolder);
        Assert.False(storage.CanReset);

        picker.Result = "/mnt/big/meetings";
        await storage.ChangeCommand.ExecuteAsync(null);

        Assert.Equal("/mnt/big/meetings", rig.Store.Current.RecordingsDirectory);
        Assert.Equal("/mnt/big/meetings", storage.EffectivePath);
        Assert.False(storage.IsDefaultFolder);
        Assert.True(storage.CanReset);
        Assert.Equal(Strings.Storage_PickerTitle, picker.Calls[0].Title);
        Assert.Equal("/data/Recordings", picker.Calls[0].Start);

        picker.Result = null;
        await storage.ChangeCommand.ExecuteAsync(null);
        Assert.Equal("/mnt/big/meetings", rig.Store.Current.RecordingsDirectory);

        await storage.ResetCommand.ExecuteAsync(null);
        Assert.Null(rig.Store.Current.RecordingsDirectory);
        Assert.Equal("/data/Recordings", storage.EffectivePath);
        Assert.True(storage.IsDefaultFolder);
    }

    [Fact]
    public void Storage_OpenHandsTheEffectiveFolderToTheLauncher_AndShowsItsFailure()
    {
        var folder = Path.Combine(Path.GetTempPath(), "murmur-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rig = new Rig(new AppSettings { RecordingsDirectory = folder });
            rig.Vm.Storage.OpenCommand.Execute(null);

            Assert.Equal([folder], rig.Launcher.Opened);
            Assert.True(Directory.Exists(folder));
            Assert.False(rig.Vm.Storage.HasError);

            rig.Launcher.Failure = "no file manager";
            rig.Vm.Storage.OpenCommand.Execute(null);
            Assert.True(rig.Vm.Storage.HasError);
            Assert.Contains("no file manager", rig.Vm.Storage.Error, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder);
            }
        }
    }

    // Hotkeys -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Hotkeys_ShowTheStoredGestures_AsKeyCaps()
    {
        var rig = new Rig();
        var rows = rig.Vm.Hotkeys.Rows;

        Assert.Equal(["Ctrl Shift R", "Ctrl Shift S", "Ctrl Shift M"], rows.Select(r => r.DisplayText));
        Assert.Equal([HotkeyAction.Record, HotkeyAction.ToggleScreen, HotkeyAction.ShowApp], rows.Select(r => r.Action));
    }

    [Fact]
    public async Task Hotkeys_ClickThenAValidCombination_IsSaved()
    {
        var rig = new Rig();
        var hotkeys = rig.Vm.Hotkeys;
        var row = hotkeys.Rows[0];

        row.CaptureCommand.Execute(null);
        Assert.True(row.IsCapturing);
        Assert.Equal(Strings.Hotkeys_Press, row.DisplayText);
        Assert.Same(row, hotkeys.Capturing);

        // A modifier on its own keeps waiting.
        Assert.True(hotkeys.HandleKey(Key.LeftCtrl, KeyModifiers.Control));
        Assert.True(row.IsCapturing);

        Assert.True(hotkeys.HandleKey(Key.K, KeyModifiers.Control | KeyModifiers.Alt));
        await hotkeys.LastSave;

        Assert.False(row.IsCapturing);
        Assert.Null(hotkeys.Capturing);
        Assert.Equal("Ctrl+Alt+K", row.Gesture);
        Assert.Equal("Ctrl Alt K", row.DisplayText);
        Assert.Equal("Ctrl+Alt+K", rig.Store.Current.RecordHotkey);
        Assert.Equal("Ctrl+Shift+S", rig.Store.Current.ToggleScreenHotkey);
    }

    [Fact]
    public async Task Hotkeys_EachRowSavesItsOwnSetting()
    {
        var rig = new Rig();
        var hotkeys = rig.Vm.Hotkeys;

        hotkeys.Rows[1].CaptureCommand.Execute(null);
        hotkeys.HandleKey(Key.F13, KeyModifiers.None);
        await hotkeys.LastSave;
        hotkeys.Rows[2].CaptureCommand.Execute(null);
        hotkeys.HandleKey(Key.J, KeyModifiers.Control | KeyModifiers.Shift);
        await hotkeys.LastSave;

        Assert.Equal("F13", rig.Store.Current.ToggleScreenHotkey);
        Assert.Equal("Ctrl+Shift+J", rig.Store.Current.ShowAppHotkey);
        Assert.Equal("Ctrl+Shift+R", rig.Store.Current.RecordHotkey);
    }

    [Fact]
    public void Hotkeys_EscapeCancels_AndChangesNothing()
    {
        var rig = new Rig();
        var hotkeys = rig.Vm.Hotkeys;
        hotkeys.Rows[0].CaptureCommand.Execute(null);

        Assert.True(hotkeys.HandleKey(Key.Escape, KeyModifiers.None));

        Assert.False(hotkeys.Rows[0].IsCapturing);
        Assert.Equal("Ctrl+Shift+R", hotkeys.Rows[0].Gesture);
        Assert.Equal(0, rig.Store.SaveCount);
        Assert.False(hotkeys.HandleKey(Key.A, KeyModifiers.Control));
    }

    [Fact]
    public async Task Hotkeys_BackspaceClearsTheHotkey()
    {
        var rig = new Rig();
        var hotkeys = rig.Vm.Hotkeys;
        hotkeys.Rows[0].CaptureCommand.Execute(null);

        Assert.True(hotkeys.HandleKey(Key.Back, KeyModifiers.None));
        await hotkeys.LastSave;

        Assert.Null(hotkeys.Rows[0].Gesture);
        Assert.Equal(Strings.Hotkeys_None, hotkeys.Rows[0].DisplayText);
        Assert.Null(rig.Store.Current.RecordHotkey);
    }

    [Fact]
    public void Hotkeys_ADuplicate_IsRefused_NamingTheOtherAction_AndTheRowKeepsWaiting()
    {
        var rig = new Rig();
        var hotkeys = rig.Vm.Hotkeys;
        hotkeys.Rows[0].CaptureCommand.Execute(null);

        hotkeys.HandleKey(Key.S, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.True(hotkeys.Rows[0].IsCapturing);
        Assert.True(hotkeys.Rows[0].HasError);
        Assert.Contains(Strings.Hotkeys_ToggleScreen, hotkeys.Rows[0].Error, StringComparison.Ordinal);
        Assert.Equal("Ctrl+Shift+R", rig.Store.Current.RecordHotkey);
        Assert.Equal(0, rig.Store.SaveCount);

        // The same gesture on the row's own current value is fine, and a valid one afterwards clears the error.
        hotkeys.HandleKey(Key.R, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.False(hotkeys.Rows[0].IsCapturing);
        Assert.False(hotkeys.Rows[0].HasError);
    }

    [Theory]
    [InlineData(Key.A, KeyModifiers.None)]
    [InlineData(Key.A, KeyModifiers.Shift)]
    [InlineData(Key.Space, KeyModifiers.None)]
    [InlineData(Key.F1, KeyModifiers.None)]
    [InlineData(Key.F9, KeyModifiers.None)]
    [InlineData(Key.F12, KeyModifiers.Shift)]
    [InlineData(Key.Pause, KeyModifiers.None)]
    [InlineData(Key.PrintScreen, KeyModifiers.None)]
    public void Hotkeys_AKeyWithoutCtrlAltOrWin_IsRefused(Key key, KeyModifiers modifiers)
    {
        var rig = new Rig();
        var hotkeys = rig.Vm.Hotkeys;
        hotkeys.Rows[0].CaptureCommand.Execute(null);

        hotkeys.HandleKey(key, modifiers);

        Assert.Equal(Strings.Hotkeys_NeedModifier, hotkeys.Rows[0].Error);
        Assert.Equal("Ctrl+Shift+R", rig.Store.Current.RecordHotkey);
    }

    [Fact]
    public void Hotkeys_StartingAnotherRow_MovesTheCapture()
    {
        var rig = new Rig();
        var hotkeys = rig.Vm.Hotkeys;
        hotkeys.Rows[0].CaptureCommand.Execute(null);
        hotkeys.Rows[1].CaptureCommand.Execute(null);

        Assert.False(hotkeys.Rows[0].IsCapturing);
        Assert.True(hotkeys.Rows[1].IsCapturing);
        hotkeys.CancelCapture();
        Assert.Null(hotkeys.Capturing);
    }

    [Theory]
    [InlineData(Key.R, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+R")]
    [InlineData(Key.F13, KeyModifiers.None, "F13")]
    [InlineData(Key.F24, KeyModifiers.Shift, "Shift+F24")]
    [InlineData(Key.F9, KeyModifiers.Control, "Ctrl+F9")]
    [InlineData(Key.D1, KeyModifiers.Alt, "Alt+D1")]
    [InlineData(Key.OemPlus, KeyModifiers.Control, "Ctrl+OemPlus")]
    public void HotkeyGestures_TryFormat_ReturnsTheAvaloniaSyntax_ThatParsesBack(Key key, KeyModifiers modifiers, string expected)
    {
        var text = HotkeyGestures.TryFormat(key, modifiers, out var problem);

        Assert.Equal(HotkeyProblem.None, problem);
        Assert.Equal(expected, text);
        Assert.True(Murmur.Core.Input.HotkeyGesture.TryParse(text, out var core));
        Assert.NotNull(text); // what Core registers
        Assert.Equal(text, core.ToString());
        var parsed = KeyGesture.Parse(text); // what Avalonia shows
        Assert.Equal(key, parsed.Key);
        Assert.Equal(modifiers, parsed.KeyModifiers);
    }

    [Theory]
    [InlineData(Key.LeftCtrl, KeyModifiers.Control, HotkeyProblem.ModifierOnly)]
    [InlineData(Key.RightShift, KeyModifiers.Shift, HotkeyProblem.ModifierOnly)]
    [InlineData(Key.None, KeyModifiers.Control, HotkeyProblem.ModifierOnly)]
    [InlineData(Key.A, KeyModifiers.None, HotkeyProblem.NeedsModifier)]
    [InlineData(Key.F12, KeyModifiers.None, HotkeyProblem.NeedsModifier)]
    [InlineData(Key.PrintScreen, KeyModifiers.Shift, HotkeyProblem.NeedsModifier)]
    [InlineData(Key.Escape, KeyModifiers.Control, HotkeyProblem.Unsupported)]
    [InlineData(Key.Enter, KeyModifiers.Alt, HotkeyProblem.Unsupported)]
    [InlineData(Key.Tab, KeyModifiers.None, HotkeyProblem.Unsupported)]
    public void HotkeyGestures_TryFormat_ExplainsWhyACombinationCannotBeUsed(Key key, KeyModifiers modifiers, HotkeyProblem expected)
    {
        Assert.Null(HotkeyGestures.TryFormat(key, modifiers, out var problem));
        Assert.Equal(expected, problem);
    }

    [Theory]
    [InlineData("Ctrl+Shift+R", "shift+ctrl+r", true)]
    [InlineData("Ctrl+Shift+R", "Ctrl+Shift+S", false)]
    [InlineData("Ctrl+Shift+R", null, false)]
    [InlineData("Win+E", "Cmd+e", true)]
    [InlineData(null, null, false)]
    public void HotkeyGestures_Same_IgnoresOrderAndCase(string? a, string? b, bool expected) =>
        Assert.Equal(expected, HotkeyGestures.Same(a, b));

    // Appearance --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Appearance_ThemeChoices_AreSavedAtOnce()
    {
        var rig = new Rig();
        var appearance = rig.Vm.Appearance;
        Assert.True(appearance.IsSystem);

        appearance.IsDark = true;
        await appearance.LastSave;
        Assert.Equal(ThemePreference.Dark, rig.Store.Current.Theme);
        Assert.True(appearance.IsDark);
        Assert.False(appearance.IsSystem);

        appearance.IsLight = true;
        await appearance.LastSave;
        Assert.Equal(ThemePreference.Light, rig.Store.Current.Theme);

        // Clicking the checked pill must not uncheck it.
        var changed = new List<string?>();
        appearance.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        appearance.IsLight = false;
        Assert.True(appearance.IsLight);
        Assert.Contains(nameof(AppearanceSettingsViewModel.IsLight), changed);
        Assert.Equal(ThemePreference.Light, rig.Store.Current.Theme);
    }

    [Fact]
    public async Task Appearance_Language_IsSaved_AndFlagsARestartOnlyWhenItDiffersFromTheRunningOne()
    {
        using var uk = UiCulture.Override("uk");
        var rig = new Rig();
        var appearance = rig.Vm.Appearance;
        Assert.True(appearance.IsUkrainian);
        Assert.False(appearance.NeedsRestart);

        appearance.IsEnglish = true;
        await appearance.LastSave;

        Assert.Equal("en", rig.Store.Current.Language);
        Assert.True(appearance.NeedsRestart);
        Assert.True(appearance.IsEnglish);
        Assert.False(appearance.IsUkrainian);
        // The running language does not change (decision D-032).
        Assert.Equal("uk", UiCulture.Language);

        appearance.IsUkrainian = true;
        await appearance.LastSave;
        Assert.Equal("uk", rig.Store.Current.Language);
        Assert.False(appearance.NeedsRestart);
    }

    [Fact]
    public async Task Appearance_RestartOnboarding_ClearsTheCompletedFlag()
    {
        var rig = new Rig(new AppSettings { OnboardingCompleted = true });
        var appearance = rig.Vm.Appearance;
        Assert.False(appearance.OnboardingReset);

        appearance.RestartOnboardingCommand.Execute(null);
        await appearance.LastSave;

        Assert.False(rig.Store.Current.OnboardingCompleted);
        Assert.True(appearance.OnboardingReset);
    }

    // About -------------------------------------------------------------------------------------------------------

    [Fact]
    public void About_ShowsTheVersion_TheLicence_AndTheAttributionsTheLicencesRequire()
    {
        using var en = UiCulture.Override("en");
        var about = new SettingsViewModel(new FakeSettingsStore(), new FakeDisplayCapture(), version: "9.8.7").About;

        Assert.Equal("9.8.7", about.Version);
        Assert.Equal("Project licence — Apache 2.0", about.License);
        Assert.Contains("Parakeet TDT v3 — NVIDIA, CC-BY-4.0", about.Credits);
        Assert.Contains("Gemma 4 — Google, Apache 2.0", about.Credits);
        Assert.Contains("EmbeddingGemma — Google, Gemma Terms of Use", about.Credits);
        Assert.Contains("llama.cpp — MIT", about.Credits);
        Assert.Contains("sherpa-onnx — Apache 2.0", about.Credits);
    }

    [Fact]
    public void About_TheProjectLicenceNameMatchesTheLicenceFile()
    {
        var licence = File.ReadAllText(Path.Combine(RepoPaths.Root, "LICENSE"));

        Assert.Contains("Apache License", licence, StringComparison.Ordinal);
        Assert.Contains("Version 2.0", licence, StringComparison.Ordinal);
        Assert.Equal("Apache 2.0", AboutViewModel.LicenseName);
    }

    [Fact]
    public void About_WithoutAVersion_ReadsTheInformationalVersion_WithoutTheRevisionSuffix()
    {
        var version = new AboutViewModel().Version;

        Assert.NotEmpty(version);
        Assert.DoesNotContain('+', version);
    }

    // Writer ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task QuickChanges_AreAppliedOneAfterAnother_NoneIsLost()
    {
        var store = new SlowStore();
        var writer = new SettingsWriter(store);

        var first = writer.UpdateAsync(s => s with { ScreenByDefault = true });
        var second = writer.UpdateAsync(s => s with { RecordingsDirectory = "/x" });
        var third = writer.UpdateAsync(s => s with { Language = "en" });
        await Task.WhenAll(first, second, third);

        Assert.True(store.Current.ScreenByDefault);
        Assert.Equal("/x", store.Current.RecordingsDirectory);
        Assert.Equal("en", store.Current.Language);
        Assert.Equal(3, store.Saves);
    }

    [Fact]
    public async Task AChangeThatChangesNothing_DoesNotTouchTheFile()
    {
        var store = new FakeSettingsStore();
        var writer = new SettingsWriter(store);

        Assert.Null(await writer.UpdateAsync(s => s));
        Assert.Equal(0, store.SaveCount);
    }

    private sealed class SlowStore : ISettingsStore
    {
        public AppSettings Current { get; private set; } = new();

        public int Saves { get; private set; }

        public event Action<AppSettings>? Changed;

        public async ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            // Like the real store: Current changes only after the (slow) write.
            await Task.Delay(20, cancellationToken);
            Current = settings;
            Saves++;
            Changed?.Invoke(settings);
        }
    }
}
