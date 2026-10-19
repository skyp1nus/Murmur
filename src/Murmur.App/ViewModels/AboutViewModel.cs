using System.Reflection;
using Murmur.App.Resources;

namespace Murmur.App.ViewModels;

/// <summary>The "Про програму" section: version, the project licence and the attributions the licences of the bundled models require.</summary>
public sealed class AboutViewModel
{
    /// <summary>The project licence (see the LICENSE file at the repository root).</summary>
    public const string LicenseName = "Apache 2.0";

    public AboutViewModel(string? version = null)
    {
        Version = version ?? ReadVersion();
        License = UiText.Format(Strings.About_License, LicenseName);

        // Licence names follow THIRD-PARTY-NOTICES.md. Parakeet's CC-BY-4.0 requires the attribution shown here.
        Credits =
        [
            Credit("Parakeet TDT v3", "NVIDIA, CC-BY-4.0"),
            Credit("Gemma 4", "Google, Apache 2.0"),
            Credit("EmbeddingGemma", "Google, Gemma Terms of Use"),
            Credit("llama.cpp", "MIT"),
            Credit("sherpa-onnx", "Apache 2.0"),
            Credit("Silero VAD", "MIT"),
        ];
    }

    public string Version { get; }

    public string License { get; }

    public IReadOnlyList<string> Credits { get; }

    private static string Credit(string component, string licence) => UiText.Format(Strings.About_Credit, component, licence);

    /// <summary>The informational version of the running program without the source revision suffix (<c>1.2.3+abcdef</c> becomes <c>1.2.3</c>).</summary>
    private static string ReadVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AboutViewModel).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? string.Empty;
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }
}
