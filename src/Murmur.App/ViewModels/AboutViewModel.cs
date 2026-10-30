using Murmur.App.Resources;

namespace Murmur.App.ViewModels;

/// <summary>The "Про програму" section: version, the project licence and the attributions the licences of the bundled models require.</summary>
public sealed class AboutViewModel
{
    /// <summary>The project licence (see the LICENSE file at the repository root).</summary>
    public const string LicenseName = "Apache 2.0";

    public AboutViewModel(string? version = null)
    {
        Version = version ?? BuildInfo.Version;
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

    /// <summary>The credit the CC-BY licence requires on the card.</summary>
    public string ParakeetCredit => Credits[0];

    /// <summary>Both Google models in one line, each with its own licence (they differ).</summary>
    public string GoogleCredit => UiText.Format(Strings.About_Google, Credits[1], Credits[2]);

    /// <summary>The remaining components, one line under the card.</summary>
    public string OtherCredits => string.Join(" · ", Credits.Skip(3));

    private static string Credit(string component, string licence) => UiText.Format(Strings.About_Credit, component, licence);
}
