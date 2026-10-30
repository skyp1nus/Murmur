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
        ParakeetCredit = Credit("Parakeet TDT v3", "NVIDIA, CC-BY-4.0");
        var gemma = Credit("Gemma 4", "Google, Apache 2.0");
        var embedding = Credit("EmbeddingGemma", "Google, Gemma Terms of Use");
        GoogleCredit = UiText.Format(Strings.About_Google, gemma, embedding);
        string[] others = [Credit("llama.cpp", "MIT"), Credit("sherpa-onnx", "Apache 2.0"), Credit("Silero VAD", "MIT")];
        OtherCredits = string.Join(" · ", others);
        Credits = [ParakeetCredit, gemma, embedding, .. others];
    }

    public string Version { get; }

    public string License { get; }

    public IReadOnlyList<string> Credits { get; }

    /// <summary>The credit the CC-BY licence requires on the card.</summary>
    public string ParakeetCredit { get; }

    /// <summary>Both Google models in one line, each with its own licence (they differ).</summary>
    public string GoogleCredit { get; }

    /// <summary>The remaining components, one line under the card.</summary>
    public string OtherCredits { get; }

    private static string Credit(string component, string licence) => UiText.Format(Strings.About_Credit, component, licence);
}
