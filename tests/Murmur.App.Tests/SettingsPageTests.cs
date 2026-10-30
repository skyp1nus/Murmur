using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Intelligence;
using Murmur.Core.Models;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

public sealed partial class SettingsViewTests
{
    private sealed class PageRig
    {
        public PageRig(AppSettings? initial = null, params AiAcceleration[] supported)
        {
            Store = new FakeSettingsStore(initial);
            Manager = new FakeModelManager();
            Manager.AddUnlisted(new ModelInfo(FakeModelManager.VulkanRuntimeId, ModelKind.LlmRuntime, "llama.cpp (Vulkan server)", 32_000_000, Required: false));
            Status = new FakeAccelerationStatus();
            Settings = new SettingsViewModel(
                Store,
                Capture,
                Manager,
                null,
                "/data/Recordings",
                new FakeFolderLauncher(),
                action => action(),
                version: "0.4.0",
                accelerationProbe: new FakeAccelerationProbe(supported.Length == 0 ? [AiAcceleration.Cpu, AiAcceleration.Vulkan] : supported),
                accelerationStatus: Status, accelerationRuntimeIds: FakeModelManager.AccelerationRuntimeIds);
            Harness = new UiHarness();
            Shell = new ShellViewModel(
                new LibraryViewModel(Harness.Store, Harness.Time, null, action => action()),
                new RecordingViewModel(Harness.Recorder, Capture, Harness.Store, action => action(), Store),
                chatFactory: scope => new ChatViewModel(new FakeAssistant(), new FakeLlm(), Harness.Recorder, scope, "/models"),
                settings: Settings);
        }

        public UiHarness Harness { get; }

        public FakeSettingsStore Store { get; }

        public FakeDisplayCapture Capture { get; } = new();

        public FakeModelManager Manager { get; }

        public FakeAccelerationStatus Status { get; }

        public SettingsViewModel Settings { get; }

        public ShellViewModel Shell { get; }

        public MainWindow Show(double height)
        {
            Shell.ShowSettings();
            var window = new MainWindow { DataContext = Shell, Height = height };
            window.Show();
            UiHarness.Pump(6);
            return window;
        }
    }

    private static string[] ActiveSections(PageRig rig) => [.. rig.Shell.Sidebar!.Entries.Where(e => e.IsActive).Select(e => e.Title)];

    private static double TopOfSection(Window window, string name) =>
        Named<Control>(window, name).TranslatePoint(default, Named<Border>(window, "Page"))!.Value.Y;

    [Fact]
    public async Task SectionsList_HighlightsTheSectionInView_AsThePageScrolls()
    {
        var snapshot = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new PageRig();
                var window = rig.Show(height: 700);
                var scroller = Named<ScrollViewer>(window, "Scroller");
                var titles = rig.Shell.Sidebar!.Entries.Select(e => e.Title).ToArray();

                // At the top of the page the canvas highlights no section.
                var atTop = (ActiveSections(rig), rig.Settings.CurrentSectionId);

                scroller.Offset = new Vector(0, TopOfSection(window, "DevicesSection") + 10);
                UiHarness.Pump(4);
                var devices = (ActiveSections(rig), rig.Settings.CurrentSectionId);

                scroller.Offset = new Vector(0, TopOfSection(window, "HotkeysSection") - 20);
                UiHarness.Pump(4);
                var hotkeys = (ActiveSections(rig), rig.Settings.CurrentSectionId);

                scroller.Offset = new Vector(0, scroller.Extent.Height);
                UiHarness.Pump(4);
                var bottom = (ActiveSections(rig), rig.Settings.CurrentSectionId);

                scroller.Offset = default;
                UiHarness.Pump(4);
                var back = (ActiveSections(rig), rig.Settings.CurrentSectionId);
                window.Close();
                return (titles, atTop, devices, hotkeys, bottom, back);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["Models", "Devices", "Storage", "Hotkeys", "About", "Preferences"], snapshot.titles);
        Assert.Empty(snapshot.atTop.Item1);
        Assert.Null(snapshot.atTop.Item2);
        Assert.Equal(["Devices"], snapshot.devices.Item1);
        Assert.Equal(SettingsViewModel.DevicesSectionId, snapshot.devices.Item2);
        Assert.Equal(["Hotkeys"], snapshot.hotkeys.Item1);
        Assert.Equal(["Preferences"], snapshot.bottom.Item1);
        Assert.Equal(SettingsViewModel.AppearanceSectionId, snapshot.bottom.Item2);
        Assert.Empty(snapshot.back.Item1);
    }

    [Fact]
    public async Task ClickingASection_ScrollsItToTheTop_AndKeepsItHighlightedUntilThePageMoves()
    {
        var snapshot = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new PageRig();
                var window = rig.Show(height: 700);
                var scroller = Named<ScrollViewer>(window, "Scroller");
                Button Link(string title) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("sub") && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == title));

                // Hotkeys sits near the bottom: the page cannot scroll it all the way up, and it is still the marked one.
                Link("Hotkeys").Command!.Execute(null);
                UiHarness.Pump(6);
                var hotkeys = (Offset: scroller.Offset.Y, Top: TopOfSection(window, "HotkeysSection"), Active: ActiveSections(rig));

                Link("Storage").Command!.Execute(null);
                UiHarness.Pump(6);
                var storage = (Offset: scroller.Offset.Y, Top: TopOfSection(window, "StorageSection"), Active: ActiveSections(rig), Max: scroller.Extent.Height - scroller.Viewport.Height);

                scroller.Offset = new Vector(0, scroller.Offset.Y - 300);
                UiHarness.Pump(4);
                var moved = ActiveSections(rig);

                // Showing the page again starts at the top with nothing marked.
                rig.Settings.Refresh();
                var refreshed = rig.Settings.CurrentSectionId;
                window.Close();
                return (hotkeys, storage, moved, refreshed);
            },
            TestContext.Current.CancellationToken);

        Assert.True(snapshot.hotkeys.Offset > 0);
        Assert.Equal(["Hotkeys"], snapshot.hotkeys.Active);
        Assert.InRange(snapshot.storage.Top - snapshot.storage.Offset, 20, 28); // 24 px of air above the section title
        Assert.Equal(["Storage"], snapshot.storage.Active);
        Assert.Single(snapshot.moved);
        Assert.NotEqual("Storage", snapshot.moved[0]);
        Assert.Null(snapshot.refreshed);
    }

    [Fact]
    public async Task InstalledRows_ShowTheirActionsOnlyWhilePointerOrFocusIsInTheRow()
    {
        var snapshot = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new PageRig();
                rig.Manager.SetInstalled(FakeModelManager.AsrId);
                var window = rig.Show(height: 1800);
                rig.Settings.Refresh();
                UiHarness.Pump(4);

                var remove = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Remove"));
                var actions = remove.GetVisualAncestors().OfType<StackPanel>().First(p => p.Classes.Contains("settingsActions"));
                var rest = actions.GetVisualParent()!.GetVisualChildren().OfType<StackPanel>().Single(p => p.Classes.Contains("settingsRest"));
                var atRest = (Actions: actions.Opacity, Idle: rest.Opacity);
                var hitAtRest = actions.IsHitTestVisible;

                var row = remove.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("settingsModel"));
                var point = row.TranslatePoint(new Point(40, 20), window)!.Value;
                window.MouseMove(point);
                UiHarness.Pump(4);
                var hovered = (Actions: actions.Opacity, Idle: rest.Opacity);
                var hitHovered = actions.IsHitTestVisible;

                window.MouseMove(new Point(point.X, 4));
                UiHarness.Pump(4);
                var left = (Actions: actions.Opacity, Idle: rest.Opacity);

                // The keyboard reaches the hidden button too, and the row then shows its actions.
                remove.Focus();
                UiHarness.Pump(4);
                var focused = (Actions: actions.Opacity, Idle: rest.Opacity);
                window.Close();
                return (atRest, hovered, left, focused, hitAtRest, hitHovered);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal((0.0, 1.0), snapshot.atRest);
        Assert.Equal((1.0, 0.0), snapshot.hovered);
        Assert.Equal((0.0, 1.0), snapshot.left);
        Assert.Equal((1.0, 0.0), snapshot.focused);
        Assert.False(snapshot.hitAtRest); // a touch cannot tap the invisible button
        Assert.True(snapshot.hitHovered);
    }

    [Fact]
    public async Task AccelerationCard_DisablesAnUnavailableOption_WithItsReason_AndDownloadsAMissingRuntimeWithProgress()
    {
        var snapshot = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new PageRig(null, AiAcceleration.Cpu, AiAcceleration.Vulkan);
                var window = rig.Show(height: 1800);
                var segments = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("settingsSegment")).ToArray();
                var labels = segments.Select(b => b.GetVisualDescendants().OfType<TextBlock>().Single().Text).ToArray();
                var cuda = segments[2];
                var unavailable = (cuda.IsEnabled, ToolTip.GetTip(cuda), ToolTip.GetShowOnDisabled(cuda), segments[0].Classes.Contains("on"));

                segments[1].Command!.Execute(null);
                rig.Manager.Progress(FakeModelManager.VulkanRuntimeId, 16_000_000, 32_000_000);
                UiHarness.Pump(4);
                var card = Named<Border>(window, "AccelerationCard");
                var bars = card.GetVisualDescendants().OfType<ProgressBar>().Where(b => b.IsEffectivelyVisible).ToArray();
                var downloading = (Bars: bars.Select(b => b.Value).ToArray(), Texts: card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text!).ToArray(), VulkanOn: segments[1].Classes.Contains("on"));

                rig.Manager.Complete(FakeModelManager.VulkanRuntimeId);
                UiHarness.Pump(6);
                rig.Status.Set(new AccelerationStatus(AiAcceleration.Vulkan, AiAcceleration.Cpu, AccelerationFallback.StartFailed, "no suitable GPU"));
                UiHarness.Pump(4);
                var fallback = card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text!).ToArray();
                window.Close();
                return (labels, unavailable, downloading, fallback);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["CPU", "Vulkan", "CUDA"], snapshot.labels);
        Assert.False(snapshot.unavailable.IsEnabled);
        Assert.Equal(Strings.Get("Accel_UnavailableCuda", "en"), snapshot.unavailable.Item2);
        Assert.True(snapshot.unavailable.Item3);
        Assert.True(snapshot.unavailable.Item4);
        Assert.Equal([50.0], snapshot.downloading.Bars);
        Assert.Contains("50%", snapshot.downloading.Texts);
        Assert.Contains("Downloading the Vulkan build…", snapshot.downloading.Texts);
        Assert.True(snapshot.downloading.VulkanOn);
        Assert.Contains("GPU unavailable: no suitable GPU. The CPU build runs instead.", snapshot.fallback);
    }
}
