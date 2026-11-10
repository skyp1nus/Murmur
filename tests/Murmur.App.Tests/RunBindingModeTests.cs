using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Murmur.App.Tests;

/// <summary>
/// #102: <c>Run.Text</c> binds TwoWay by default. A Run bound with a StringFormat sent its formatted text back to the view model, which
/// raised the change, which formatted the text again, and so on until the stack overflowed (the clock of the recording details). Under the
/// JIT the generated accessor cannot call a private setter (MethodAccessException, swallowed by the binding), so only the Native AOT build
/// crashed, and no headless test sees the loop: this check of the markup is the JIT-side guard, the <c>--probe-open-latest</c> run of the
/// published binary in CI the AOT-side one.
/// </summary>
public sealed class RunBindingModeTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public void EveryRunBoundToTheViewModel_BindsOneWay()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.AppDirectory, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var run in document.Descendants(Avalonia + "Run"))
            {
                if (run.Attribute("Text")?.Value is { } text && IsBinding(text) && !BindsOneWay(text))
                {
                    var line = ((System.Xml.IXmlLineInfo)run).LineNumber;
                    offenders.Add($"{Path.GetRelativePath(RepoPaths.Root, file)}:{line}: {text}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Run.Text is TwoWay by default; add Mode=OneWay:\n" + string.Join('\n', offenders));
    }

    [Theory]
    [InlineData("{Binding PositionText, StringFormat='{}{0} '}", false)]
    [InlineData("{Binding KindText}", false)]
    [InlineData("{Binding PositionText, Mode=TwoWay}", false)]
    [InlineData("{Binding PositionText, Mode=OneWayToSource}", false)]
    [InlineData("{Binding PositionText, Mode=OneWay, StringFormat='{}{0} '}", true)]
    [InlineData("{CompiledBinding Title, Mode=OneTime}", true)]
    public void BindsOneWay_ReadsTheModeOfTheMarkup(string text, bool expected) => Assert.Equal(expected, IsBinding(text) && BindsOneWay(text));

    private static bool IsBinding(string text) =>
        text.StartsWith("{Binding", StringComparison.Ordinal) || text.StartsWith("{CompiledBinding", StringComparison.Ordinal);

    private static bool BindsOneWay(string text) => Regex.IsMatch(text, @"\bMode=One(Way|Time)(?![A-Za-z])", RegexOptions.CultureInvariant);
}
