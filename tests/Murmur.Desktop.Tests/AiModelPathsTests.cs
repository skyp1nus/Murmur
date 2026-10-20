namespace Murmur.Desktop.Tests;

public sealed class AiModelPathsTests : IDisposable
{
    private readonly string _models = Path.Combine(Path.GetTempPath(), "murmur-aipaths-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_models, recursive: true);

    [Fact]
    public void ReadManifest_WorksWhileAnotherHandleHasTheFileOpenForWriting()
    {
        var gguf = Directory.CreateDirectory(Path.Combine(_models, AiModelPaths.GgufDirectoryName)).FullName;
        var manifest = Path.Combine(gguf, AiModelPaths.ManifestFileName);
        File.WriteAllText(manifest, """{ "chat": "/m/chat.gguf", "embed": "/m/embed.gguf" }""");

        // The model manager keeps the manifest open while it rewrites it; reading must neither fail nor block it.
        using var writer = new FileStream(manifest, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var read = AiModelPaths.ReadManifest(manifest);

        Assert.Equal("/m/chat.gguf", read?.Chat);
        Assert.Equal("/m/embed.gguf", read?.Embed);
    }

    [Fact]
    public void Resolve_FollowsTheManifestAtEveryCall()
    {
        var gguf = Directory.CreateDirectory(Path.Combine(_models, AiModelPaths.GgufDirectoryName)).FullName;
        var manifest = Path.Combine(gguf, AiModelPaths.ManifestFileName);
        File.WriteAllText(manifest, """{ "chat": "/m/e4b.gguf" }""");
        Assert.Equal("/m/e4b.gguf", AiModelPaths.Resolve(_models, _ => null).ChatModelPath);

        File.WriteAllText(manifest, """{ "chat": "/m/26b.gguf" }""");

        Assert.Equal("/m/26b.gguf", AiModelPaths.Resolve(_models, _ => null).ChatModelPath);
    }
}
