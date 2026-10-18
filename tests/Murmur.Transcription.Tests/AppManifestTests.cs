using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml.Linq;

namespace Murmur.Transcription.Tests;

/// <summary>
/// The worker passes model paths to sherpa-onnx as UTF-8; on Windows that is only correct when the process ANSI
/// code page is UTF-8, which the embedded application manifest requests. Native AOT copies the Win32 resources of
/// the managed assembly into the win-* executable, so checking the managed assembly covers the published worker.
/// </summary>
public sealed class AppManifestTests
{
    private const string WindowsSettings2019 = "http://schemas.microsoft.com/SMI/2019/WindowsSettings";

    [Fact]
    public void WorkerAssembly_EmbedsManifestWithUtf8ActiveCodePage()
    {
        var manifest = ReadEmbeddedManifest(typeof(Program).Assembly.Location);

        var activeCodePage = Assert.Single(manifest.Descendants(XName.Get("activeCodePage", WindowsSettings2019)));
        Assert.Equal("UTF-8", activeCodePage.Value.Trim());
    }

    [Fact]
    public void WorkerAssembly_ManifestDeclaresWindows10Support()
    {
        var manifest = ReadEmbeddedManifest(typeof(Program).Assembly.Location);

        XNamespace compat = "urn:schemas-microsoft-com:compatibility.v1";
        Assert.Contains(
            manifest.Descendants(compat + "supportedOS"),
            e => (string?)e.Attribute("Id") == "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}");
    }

    /// <summary>Extracts the manifest XML from the PE resource section (RT_MANIFEST is stored as raw UTF-8).</summary>
    private static XDocument ReadEmbeddedManifest(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var directory = pe.PEHeaders.PEHeader!.ResourceTableDirectory;
        Assert.True(directory.Size > 0, "assembly has no Win32 resources");

        var block = pe.GetSectionData(directory.RelativeVirtualAddress);
        var resources = Encoding.UTF8.GetString(block.GetContent(0, directory.Size).AsSpan());

        var start = resources.IndexOf("<assembly", StringComparison.Ordinal);
        Assert.True(start >= 0, "no application manifest in the Win32 resources");
        const string end = "</assembly>";
        var stop = resources.IndexOf(end, start, StringComparison.Ordinal);
        Assert.True(stop > start, "truncated application manifest");

        return XDocument.Parse(resources[start..(stop + end.Length)]);
    }
}
