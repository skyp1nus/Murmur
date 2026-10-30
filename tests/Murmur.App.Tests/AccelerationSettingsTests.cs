using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Intelligence;
using Murmur.Core.Models;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>The Acceleration card of the Settings (D-042): which builds are offered, choosing one, downloading a missing runtime, the fallback reason.</summary>
public sealed class AccelerationSettingsTests : IDisposable
{
    private readonly IDisposable _english = UiCulture.Override("en");

    public void Dispose() => _english.Dispose();

    private sealed class Rig
    {
        public Rig(IAccelerationProbe? probe = null, AppSettings? initial = null, bool gpuRuntimes = true, bool vulkanInstalled = false, Action<Action>? post = null)
        {
            Store = new FakeSettingsStore(initial);
            Manager = new FakeModelManager();
            if (gpuRuntimes)
            {
                Manager.AddUnlisted(new ModelInfo(FakeModelManager.VulkanRuntimeId, ModelKind.LlmRuntime, "llama.cpp (Vulkan server)", 32_000_000, Required: false));
                Manager.AddUnlisted(new ModelInfo(FakeModelManager.CudaRuntimeId, ModelKind.LlmRuntime, "llama.cpp (CUDA server)", 660_000_000, Required: false));
            }

            if (vulkanInstalled)
            {
                Manager.SetInstalled(FakeModelManager.VulkanRuntimeId);
            }

            Probe = probe;
            Status = new FakeAccelerationStatus();
            Vm = new SettingsViewModel(Store, new FakeDisplayCapture(), Manager, null, "/data/Recordings", new FakeFolderLauncher(), post ?? (action => action()), accelerationProbe: probe, accelerationStatus: Status, accelerationRuntimeIds: FakeModelManager.AccelerationRuntimeIds);
        }

        public FakeSettingsStore Store { get; }

        public FakeModelManager Manager { get; }

        public IAccelerationProbe? Probe { get; }

        public FakeAccelerationStatus Status { get; }

        public SettingsViewModel Vm { get; }

        public AccelerationSettingsViewModel Card => Vm.Acceleration!;

        public AccelerationOptionViewModel Option(AiAcceleration value) => Card.Options.Single(o => o.Value == value);
    }

    private static FakeAccelerationProbe All(string? gpu = null) => new(AiAcceleration.Cpu, AiAcceleration.Vulkan, AiAcceleration.Cuda) { GpuName = gpu };

    [Fact]
    public void TheControlOffersCpuVulkanAndCuda_InThatOrder_WithTheSavedChoiceSelected()
    {
        var rig = new Rig(All(), new AppSettings { Acceleration = AiAcceleration.Vulkan });

        Assert.Equal(["CPU", "Vulkan", "CUDA"], rig.Card.Options.Select(o => o.Label));
        Assert.Equal([false, true, false], rig.Card.Options.Select(o => o.IsSelected));
        Assert.All(rig.Card.Options, o => Assert.True(o.IsAvailable));
        Assert.All(rig.Card.Options, o => Assert.Null(o.Tooltip));
    }

    [Fact]
    public void AnOptionThisPcCannotUse_IsDisabled_WithTheReasonAsItsTooltip()
    {
        var rig = new Rig(new FakeAccelerationProbe(AiAcceleration.Cpu, AiAcceleration.Vulkan));

        Assert.True(rig.Option(AiAcceleration.Cpu).IsAvailable);
        Assert.True(rig.Option(AiAcceleration.Vulkan).IsAvailable);
        Assert.False(rig.Option(AiAcceleration.Cuda).IsAvailable);
        Assert.Equal(Strings.Accel_UnavailableCuda, rig.Option(AiAcceleration.Cuda).Tooltip);
        Assert.Contains("NVIDIA", rig.Option(AiAcceleration.Cuda).Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAProbe_OnlyTheCpuBuildIsOffered()
    {
        var rig = new Rig(probe: null);

        Assert.True(rig.Option(AiAcceleration.Cpu).IsAvailable);
        Assert.False(rig.Option(AiAcceleration.Vulkan).IsAvailable);
        Assert.Equal(Strings.Accel_UnavailableVulkan, rig.Option(AiAcceleration.Vulkan).Tooltip);
        Assert.False(rig.Option(AiAcceleration.Cuda).IsAvailable);
    }

    [Fact]
    public async Task ChoosingAnUnavailableOption_ChangesNothing()
    {
        var rig = new Rig(new FakeAccelerationProbe(AiAcceleration.Cpu));

        rig.Option(AiAcceleration.Cuda).ChooseCommand.Execute(null);
        await rig.Card.LastOperation;

        Assert.Equal(AiAcceleration.Cpu, rig.Store.Current.Acceleration);
        Assert.Empty(rig.Manager.InstallCalls);
    }

    [Fact]
    public async Task ChoosingABuildWhoseRuntimeIsInstalled_SavesItAndDownloadsNothing()
    {
        var rig = new Rig(All(), vulkanInstalled: true);

        rig.Option(AiAcceleration.Vulkan).ChooseCommand.Execute(null);
        await rig.Card.LastOperation;

        Assert.Equal(AiAcceleration.Vulkan, rig.Store.Current.Acceleration);
        Assert.True(rig.Option(AiAcceleration.Vulkan).IsSelected);
        Assert.False(rig.Option(AiAcceleration.Cpu).IsSelected);
        Assert.Empty(rig.Manager.InstallCalls);
        Assert.False(rig.Card.IsDownloading);
        Assert.False(rig.Card.CanDownload);
        Assert.False(rig.Card.HasDetails);
    }

    [Fact]
    public async Task ChoosingABuildWhoseRuntimeIsMissing_SavesItAndDownloadsTheRuntime_WithProgressInTheCard()
    {
        var rig = new Rig(All());

        rig.Option(AiAcceleration.Vulkan).ChooseCommand.Execute(null);
        var operation = rig.Card.LastOperation;

        Assert.Equal(AiAcceleration.Vulkan, rig.Store.Current.Acceleration);
        Assert.Equal([FakeModelManager.VulkanRuntimeId], rig.Manager.InstallCalls);
        Assert.True(rig.Card.IsDownloading);
        Assert.True(rig.Card.HasDetails);
        Assert.Equal(string.Format(UiCulture.Culture, Strings.Accel_Downloading, "Vulkan"), rig.Card.NoteText);

        rig.Manager.Progress(FakeModelManager.VulkanRuntimeId, 8_000_000, 32_000_000);

        Assert.Equal(25, rig.Card.Percent);
        Assert.Equal("25%", rig.Card.PercentText);
        Assert.Equal(UiText.Format(Strings.Models_Downloaded, UiFormat.Size(8_000_000), UiFormat.Size(32_000_000)), rig.Card.TransferText);

        rig.Manager.Complete(FakeModelManager.VulkanRuntimeId);
        await operation;

        Assert.False(rig.Card.IsDownloading);
        Assert.False(rig.Card.CanDownload);
        Assert.False(rig.Card.HasError);
    }

    [Fact]
    public async Task ACancelledDownload_KeepsTheChoice_AndOffersTheDownloadAgain()
    {
        var rig = new Rig(All());
        rig.Option(AiAcceleration.Cuda).ChooseCommand.Execute(null);
        var operation = rig.Card.LastOperation;
        Assert.True(rig.Card.IsDownloading);

        rig.Card.CancelDownloadCommand.Execute(null);
        await operation;

        Assert.Equal(AiAcceleration.Cuda, rig.Store.Current.Acceleration);
        Assert.False(rig.Card.IsDownloading);
        Assert.True(rig.Card.CanDownload);
        Assert.Equal(string.Format(UiCulture.Culture, Strings.Accel_NeedsRuntime, "CUDA"), rig.Card.NoteText);

        rig.Card.DownloadCommand.Execute(null);

        Assert.Equal([FakeModelManager.CudaRuntimeId, FakeModelManager.CudaRuntimeId], rig.Manager.InstallCalls);
        Assert.True(rig.Card.IsDownloading);
        rig.Manager.Complete(FakeModelManager.CudaRuntimeId);
        await rig.Card.LastOperation;
        Assert.False(rig.Card.CanDownload);
    }

    [Fact]
    public async Task AFailedDownload_ShowsAnError_AndOffersARetry()
    {
        var rig = new Rig(All());
        rig.Option(AiAcceleration.Vulkan).ChooseCommand.Execute(null);
        var operation = rig.Card.LastOperation;

        rig.Manager.Fail(FakeModelManager.VulkanRuntimeId, "HTTP 500");
        await operation;

        Assert.True(rig.Card.HasError);
        Assert.Equal(string.Format(UiCulture.Culture, Strings.Accel_DownloadFailed, "Vulkan"), rig.Card.Error);
        Assert.True(rig.Card.CanDownload);
        Assert.Equal(AiAcceleration.Vulkan, rig.Store.Current.Acceleration);
    }

    [Fact]
    public void ASavedGpuChoiceWhoseRuntimeIsMissing_SaysSoUnderTheControl_WithTheDownloadOffered()
    {
        var rig = new Rig(All(), new AppSettings { Acceleration = AiAcceleration.Cuda });

        Assert.True(rig.Card.CanDownload);
        Assert.False(rig.Card.IsDownloading);
        Assert.Equal(string.Format(UiCulture.Culture, Strings.Accel_NeedsRuntime, "CUDA"), rig.Card.NoteText);
        Assert.Empty(rig.Manager.InstallCalls); // nothing downloads by itself
    }

    [Fact]
    public void AGpuThatFailedToStart_ShowsTheReasonUnderTheControl_UntilTheBackendRecovers()
    {
        var rig = new Rig(All("NVIDIA GeForce RTX 4070"), new AppSettings { Acceleration = AiAcceleration.Vulkan }, vulkanInstalled: true);
        Assert.False(rig.Card.HasNote);

        rig.Status.Set(new AccelerationStatus(AiAcceleration.Vulkan, AiAcceleration.Cpu, AccelerationFallback.StartFailed, "out of device memory"));

        Assert.True(rig.Card.HasNote);
        Assert.Equal(string.Format(UiCulture.Culture, Strings.Accel_GpuUnavailable, "out of device memory"), rig.Card.NoteText);
        Assert.StartsWith("GPU unavailable: out of device memory", rig.Card.NoteText, StringComparison.Ordinal);

        rig.Status.Set(new AccelerationStatus(AiAcceleration.Vulkan, AiAcceleration.Vulkan, AccelerationFallback.None));

        Assert.Equal(string.Format(UiCulture.Culture, Strings.Accel_RunningOn, "NVIDIA GeForce RTX 4070"), rig.Card.NoteText);
    }

    [Fact]
    public async Task AChoiceSavedElsewhere_IsShownInTheControl()
    {
        var rig = new Rig(All(), vulkanInstalled: true);
        Assert.True(rig.Option(AiAcceleration.Cpu).IsSelected);

        await rig.Store.SaveAsync(rig.Store.Current with { Acceleration = AiAcceleration.Vulkan }, CancellationToken.None);

        Assert.True(rig.Option(AiAcceleration.Vulkan).IsSelected);
        Assert.False(rig.Option(AiAcceleration.Cpu).IsSelected);
        Assert.Empty(rig.Manager.InstallCalls); // a change from elsewhere starts no download
    }

    [Fact]
    public void WithoutAModelManager_ThereIsNoCard()
    {
        var vm = new SettingsViewModel(new FakeSettingsStore(), new FakeDisplayCapture(), null, null, null, new FakeFolderLauncher(), action => action(), accelerationProbe: All());

        Assert.Null(vm.Acceleration);
    }

    [Fact]
    public void ABuildTheManagerDoesNotKnow_HasNothingToDownload()
    {
        var rig = new Rig(new FakeAccelerationProbe(AiAcceleration.Cpu, AiAcceleration.Vulkan, AiAcceleration.Cuda), gpuRuntimes: false);

        rig.Option(AiAcceleration.Vulkan).ChooseCommand.Execute(null);

        Assert.Equal(AiAcceleration.Vulkan, rig.Store.Current.Acceleration);
        Assert.Empty(rig.Manager.InstallCalls);
        Assert.False(rig.Card.CanDownload);
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

    [Fact]
    public async Task ChoosingAnotherBuildWhileARuntimeDownloads_CancelsThatDownload_AndShowsTheNewOne()
    {
        var rig = new Rig(All());
        rig.Option(AiAcceleration.Cuda).ChooseCommand.Execute(null);
        Assert.True(rig.Card.IsDownloading);
        var cudaToken = rig.Manager.TokenOf(FakeModelManager.CudaRuntimeId);

        rig.Option(AiAcceleration.Vulkan).ChooseCommand.Execute(null);
        await WaitFor(() => rig.Manager.WasInstallCalled(FakeModelManager.VulkanRuntimeId));

        Assert.True(cudaToken.IsCancellationRequested);
        Assert.Equal(AiAcceleration.Vulkan, rig.Store.Current.Acceleration);
        Assert.True(rig.Card.IsDownloading);
        Assert.Equal(string.Format(UiCulture.Culture, Strings.Accel_Downloading, "Vulkan"), rig.Card.NoteText);

        // What the abandoned download still reports is not the card's business.
        rig.Manager.Progress(FakeModelManager.CudaRuntimeId, 600_000_000, 660_000_000);
        Assert.True(rig.Card.Percent < 1);
        rig.Manager.Progress(FakeModelManager.VulkanRuntimeId, 16_000_000, 32_000_000);
        Assert.Equal(50, rig.Card.Percent);

        rig.Manager.Complete(FakeModelManager.VulkanRuntimeId);
        await rig.Card.LastOperation;
        Assert.False(rig.Card.IsDownloading);
        Assert.False(rig.Card.HasError);
    }

    [Fact]
    public async Task Cancel_AlwaysStopsTheDownloadTheCardShows_AfterSwitchingBuildsBackAndForth()
    {
        var rig = new Rig(All());
        rig.Option(AiAcceleration.Cuda).ChooseCommand.Execute(null);
        rig.Option(AiAcceleration.Vulkan).ChooseCommand.Execute(null);
        await WaitFor(() => rig.Manager.WasInstallCalled(FakeModelManager.VulkanRuntimeId));
        var vulkanOperation = rig.Card.LastOperation;
        rig.Option(AiAcceleration.Cuda).ChooseCommand.Execute(null);
        await WaitFor(() => rig.Manager.InstallCalls.Count(id => id == FakeModelManager.CudaRuntimeId) == 2);
        await vulkanOperation;

        Assert.True(rig.Card.CanCancel);
        var token = rig.Manager.TokenOf(FakeModelManager.CudaRuntimeId);
        rig.Card.CancelDownloadCommand.Execute(null);
        await rig.Card.LastOperation;

        Assert.True(token.IsCancellationRequested);
        Assert.False(rig.Card.IsDownloading);
        Assert.False(rig.Card.CanCancel);
        Assert.True(rig.Card.CanDownload);
        Assert.False(rig.Card.HasError);
    }

    [Fact]
    public async Task TheDownloadThatWasLeft_LeavesNoErrorOrStateBehindForTheNewBuild()
    {
        var rig = new Rig(All());
        rig.Option(AiAcceleration.Cuda).ChooseCommand.Execute(null);
        rig.Option(AiAcceleration.Vulkan).ChooseCommand.Execute(null);
        await WaitFor(() => rig.Manager.WasInstallCalled(FakeModelManager.VulkanRuntimeId));

        Assert.False(rig.Card.HasError);
        Assert.Equal(string.Format(UiCulture.Culture, Strings.Accel_Downloading, "Vulkan"), rig.Card.NoteText);
        rig.Manager.Complete(FakeModelManager.VulkanRuntimeId);
        await rig.Card.LastOperation;
    }

    [Fact]
    public async Task TwoQuickChoices_BeforeTheFirstSaveFinished_DownloadOnlyTheLastOne()
    {
        var rig = new Rig(All());
        var release = new TaskCompletionSource();
        rig.Store.SaveBlocker = release.Task;

        rig.Option(AiAcceleration.Cuda).ChooseCommand.Execute(null);
        var first = rig.Card.LastOperation;
        rig.Option(AiAcceleration.Vulkan).ChooseCommand.Execute(null);
        var second = rig.Card.LastOperation;
        release.SetResult();
        await first;
        await WaitFor(() => rig.Manager.WasInstallCalled(FakeModelManager.VulkanRuntimeId));

        Assert.Equal([FakeModelManager.VulkanRuntimeId], rig.Manager.InstallCalls);
        Assert.Equal(AiAcceleration.Vulkan, rig.Store.Current.Acceleration);
        Assert.True(rig.Option(AiAcceleration.Vulkan).IsSelected);
        rig.Manager.Complete(FakeModelManager.VulkanRuntimeId);
        await second;
    }

    [Fact]
    public async Task AClickOnTheBuildAlreadyAskedFor_IsIgnored()
    {
        var rig = new Rig(All());
        var release = new TaskCompletionSource();
        rig.Store.SaveBlocker = release.Task;

        rig.Option(AiAcceleration.Cuda).ChooseCommand.Execute(null);
        var operation = rig.Card.LastOperation;
        rig.Option(AiAcceleration.Cuda).ChooseCommand.Execute(null);
        release.SetResult();
        await WaitFor(() => rig.Manager.WasInstallCalled(FakeModelManager.CudaRuntimeId));

        Assert.Equal([FakeModelManager.CudaRuntimeId], rig.Manager.InstallCalls);
        rig.Manager.Complete(FakeModelManager.CudaRuntimeId);
        await operation;
    }

    [Fact]
    public async Task AStatusAboutAnotherBuildThanTheChosenOne_IsIgnored()
    {
        var rig = new Rig(All(), new AppSettings { Acceleration = AiAcceleration.Vulkan }, vulkanInstalled: true);
        rig.Status.Set(new AccelerationStatus(AiAcceleration.Cuda, AiAcceleration.Cpu, AccelerationFallback.StartFailed, "driver too old"));
        Assert.False(rig.Card.HasNote);

        rig.Status.Set(new AccelerationStatus(AiAcceleration.Vulkan, AiAcceleration.Cpu, AccelerationFallback.StartFailed, "out of memory"));
        Assert.Contains("out of memory", rig.Card.NoteText, StringComparison.Ordinal);

        // Choosing the build the old report was about does not bring the report of the left one along.
        rig.Manager.SetInstalled(FakeModelManager.CudaRuntimeId);
        rig.Option(AiAcceleration.Cuda).ChooseCommand.Execute(null);
        await rig.Card.LastOperation;

        Assert.DoesNotContain("out of memory", rig.Card.NoteText, StringComparison.Ordinal);
    }

    [Fact]
    public void ASavedBuildThisPcCanNoLongerUse_SaysWhy_AndOffersNoDownload()
    {
        var rig = new Rig(new FakeAccelerationProbe(AiAcceleration.Cpu), new AppSettings { Acceleration = AiAcceleration.Cuda });

        Assert.False(rig.Option(AiAcceleration.Cuda).IsAvailable);
        Assert.False(rig.Card.CanDownload);
        Assert.Equal(Strings.Accel_UnavailableCuda, rig.Card.NoteText);
        Assert.Empty(rig.Manager.InstallCalls);
    }

    [Fact]
    public async Task ADownloadThatDoesNotFitOnTheDrive_IsNotStarted_AndSaysSo()
    {
        var rig = new Rig(All());
        rig.Manager.AvailableFreeSpaceBytes = 100_000_000;

        rig.Option(AiAcceleration.Cuda).ChooseCommand.Execute(null);
        await rig.Card.LastOperation;

        Assert.Empty(rig.Manager.InstallCalls);
        Assert.Equal(AiAcceleration.Cuda, rig.Store.Current.Acceleration);
        Assert.Equal(Strings.Models_NoSpace, rig.Card.Error);
        Assert.True(rig.Card.CanDownload);
    }

    [Fact]
    public async Task ModelAndStatusEvents_AreMarshalledWithThePostDelegate_NotAppliedOnTheCallingThread()
    {
        var queue = new Queue<Action>();
        var rig = new Rig(All("GPU one"), post: queue.Enqueue);
        rig.Option(AiAcceleration.Vulkan).ChooseCommand.Execute(null);
        var operation = rig.Card.LastOperation;
        Assert.True(rig.Card.IsDownloading);

        rig.Manager.Progress(FakeModelManager.VulkanRuntimeId, 8_000_000, 32_000_000);
        rig.Status.Set(new AccelerationStatus(AiAcceleration.Vulkan, AiAcceleration.Cpu, AccelerationFallback.RuntimeNotInstalled));

        Assert.Equal(0, rig.Card.Percent);
        Assert.NotEmpty(queue);
        while (queue.Count > 0)
        {
            queue.Dequeue()();
        }

        Assert.Equal(25, rig.Card.Percent);
        rig.Manager.Complete(FakeModelManager.VulkanRuntimeId);
        await operation;
        while (queue.Count > 0)
        {
            queue.Dequeue()();
        }

        Assert.False(rig.Card.IsDownloading);
    }

    [Fact]
    public async Task AfterDispose_AChoiceStartsNothing()
    {
        var rig = new Rig(All());
        rig.Card.Dispose();

        rig.Option(AiAcceleration.Vulkan).ChooseCommand.Execute(null);
        await rig.Card.LastOperation;

        Assert.Empty(rig.Manager.InstallCalls);
        Assert.Equal(AiAcceleration.Cpu, rig.Store.Current.Acceleration);
    }
}
