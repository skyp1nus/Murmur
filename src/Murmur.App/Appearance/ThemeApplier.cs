using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using Murmur.Core.Settings;

namespace Murmur.App.Appearance;

/// <summary>
/// Keeps <see cref="Application.RequestedThemeVariant"/> in step with <see cref="AppSettings.Theme"/>: applied once when
/// created (start-up) and again whenever the settings store reports a new theme. The store may raise
/// <see cref="ISettingsStore.Changed"/> on any thread, so the change is posted to the UI thread.
/// </summary>
/// <remarks>
/// <see cref="ThemePreference.System"/> maps to <see cref="ThemeVariant.Default"/>, which makes Avalonia follow the OS
/// light/dark setting, including later changes of it. Light and Dark pin the variant. Everything that must follow the
/// variant uses <c>DynamicResource</c> (or re-resolves on <c>ActualThemeVariantChanged</c>), so no view needs a reload.
/// </remarks>
internal sealed class ThemeApplier : IDisposable
{
    private readonly Application _application;
    private readonly ISettingsStore _settings;
    private readonly Action<Action> _post;
    private bool _disposed;

    /// <param name="application">The application whose theme variant is set.</param>
    /// <param name="settings">Source of the theme preference.</param>
    /// <param name="post">Runs an action on the UI thread; <c>null</c> = the dispatcher of the calling thread (create the applier on the UI thread).</param>
    public ThemeApplier(Application application, ISettingsStore settings, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(settings);
        _application = application;
        _settings = settings;
        if (post is null)
        {
            // Captured here, on the UI thread: the store raises Changed on other threads, which must reach this dispatcher.
            var dispatcher = Dispatcher.UIThread;
            post = action => dispatcher.Post(action);
        }

        _post = post;

        Apply(settings.Current.Theme);
        settings.Changed += OnChanged;
    }

    public static ThemeVariant ToVariant(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    public void Dispose()
    {
        _disposed = true;
        _settings.Changed -= OnChanged;
    }

    private void OnChanged(AppSettings settings)
    {
        var theme = settings.Theme;
        _post(() =>
        {
            if (!_disposed)
            {
                Apply(theme);
            }
        });
    }

    private void Apply(ThemePreference preference) => _application.RequestedThemeVariant = ToVariant(preference);
}
