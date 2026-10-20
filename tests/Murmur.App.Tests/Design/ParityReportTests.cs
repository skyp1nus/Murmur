namespace Murmur.App.Tests.Design;

/// <summary>The result files and the markdown report of the parity run.</summary>
[Trait("Category", "DesignParity")]
public sealed class ParityReportTests
{
    [Fact]
    public void AResult_SurvivesTheRoundTripThroughItsFile()
    {
        var result = new BoardResult("Main", 1440, 1406, 2.5, 2.75, 2.25, 49.5, 51.5, 48.5, 30.5, true, 1234, false, 3.0, ["a note", "another"]);

        var parsed = BoardResult.Parse(result.Serialize());

        Assert.Equal(result.Name, parsed.Name);
        Assert.Equal(result.Total, parsed.Total);
        Assert.Equal(result.Sidebar, parsed.Sidebar);
        Assert.Equal(result.Content, parsed.Content);
        Assert.Equal(result.Drawn, parsed.Drawn);
        Assert.Equal(result.DrawnSidebar, parsed.DrawnSidebar);
        Assert.Equal(result.DrawnContent, parsed.DrawnContent);
        Assert.Equal(result.DrawnLenient, parsed.DrawnLenient);
        Assert.Equal(result.Max, parsed.Max);
        Assert.Equal(result.IgnoredPixels, parsed.IgnoredPixels);
        Assert.Equal(result.Notes, parsed.Notes);
    }

    [Fact]
    public void TheReport_ListsEveryBoardInTheOrderOfTheTable_AndMarksTheMissingOnes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "murmur-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Chat.result.txt"), new BoardResult("Chat", 1440, 1080, 1.5, 2, 1, 40, 41, 39, 20, true, 0, false, null, []).Serialize());
            File.WriteAllText(Path.Combine(directory, "Old.result.txt"), "name=Old\n");

            var report = ParityReport.BuildReport(directory);

            var rows = report.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| Board", StringComparison.Ordinal) && !l.StartsWith("| ---", StringComparison.Ordinal)).ToArray();
            Assert.Equal(DesignBoards.All.Count, rows.Length);
            Assert.StartsWith("| Main | 1440x1406 | not rendered", rows[0], StringComparison.Ordinal);
            Assert.Contains(rows, r => r.StartsWith("| Chat | 1440x1080 | 40.00 | 41.00 | 39.00 | 20.00 | 1.50 | 2.00 | 1.00 |", StringComparison.Ordinal));
            Assert.Contains("OnbComponents", report, StringComparison.Ordinal);
            Assert.Contains("[VERSION] placeholder", report, StringComparison.Ordinal);
            Assert.Contains("Sidebar 'Recent' list", report, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
