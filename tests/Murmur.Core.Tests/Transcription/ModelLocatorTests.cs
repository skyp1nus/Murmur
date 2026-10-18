using Murmur.Core.Tests.Fakes;
using Murmur.Core.Transcription;

namespace Murmur.Core.Tests.Transcription;

public sealed class ModelLocatorTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>Creates empty model files so <see cref="ModelLocator.IsAsrInstalled"/> is true.</summary>
    internal static ModelLocator Install(string root)
    {
        var models = new ModelLocator(root);
        Directory.CreateDirectory(models.ParakeetDirectory);
        foreach (var name in ModelLocator.ParakeetFiles)
        {
            File.WriteAllBytes(Path.Combine(models.ParakeetDirectory, name), [0]);
        }

        File.WriteAllBytes(models.VadModel, [0]);
        return models;
    }

    [Fact]
    public void Paths_FollowTheModelLayout()
    {
        var models = new ModelLocator(_dir.Path);

        Assert.Equal(Path.Combine(_dir.Path, "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"), models.ParakeetDirectory);
        Assert.Equal(Path.Combine(_dir.Path, "silero_vad.onnx"), models.VadModel);
        Assert.Equal(["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"], ModelLocator.ParakeetFiles);
    }

    [Fact]
    public void IsAsrInstalled_RequiresEveryFile()
    {
        var models = Install(_dir.Path);
        Assert.True(models.IsAsrInstalled);
        Assert.Empty(models.MissingAsrFiles());

        File.Delete(Path.Combine(models.ParakeetDirectory, "joiner.int8.onnx"));
        File.Delete(models.VadModel);

        Assert.False(models.IsAsrInstalled);
        Assert.Equal([Path.Combine(models.ParakeetDirectory, "joiner.int8.onnx"), models.VadModel], models.MissingAsrFiles());
    }

    [Fact]
    public void EmptyDirectory_IsNotInstalled()
    {
        var models = new ModelLocator(_dir.File("nothing-here"));

        Assert.False(models.IsAsrInstalled);
        Assert.Equal(5, models.MissingAsrFiles().Count);
    }

    [Fact]
    public void DefaultModelsDirectory_IsUnderLocalApplicationData()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Murmur", "models");

        Assert.Equal(expected, ModelLocator.DefaultModelsDirectory());
    }

    [Fact]
    public void FromEnvironment_UsesTheVariableOrTheDefault()
    {
        var configured = Environment.GetEnvironmentVariable(ModelLocator.ModelsDirectoryVariable);
        var expected = string.IsNullOrEmpty(configured) ? ModelLocator.DefaultModelsDirectory() : configured;

        Assert.Equal(expected, ModelLocator.FromEnvironment().ModelsDirectory);
    }

    [Fact]
    public void Constructor_RejectsAnEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => new ModelLocator(string.Empty));
    }
}
