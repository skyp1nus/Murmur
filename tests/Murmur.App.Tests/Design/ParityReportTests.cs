namespace Murmur.App.Tests.Design;

/// <summary>The result files and the markdown report of the parity run.</summary>
[Trait("Category", "DesignParity")]
public sealed class ParityReportTests
{
    private static BoardResult Result(string name, double shape = 12.5) =>
        new(name, 1440, 1080, true, shape, 20, 10, 40, 30, 10, 4, 7, 40, 41, 39, 20, 1.5, 2, 1, 0, false, 5, 80, 3, ["a note", "another"]);

    [Fact]
    public void AResult_SurvivesTheRoundTripThroughItsFile()
    {
        var result = Result("Main");

        var parsed = BoardResult.Parse(result.Serialize());

        Assert.Equal(result.Name, parsed.Name);
        Assert.Equal(result.ShapeDrawn, parsed.ShapeDrawn);
        Assert.Equal(result.ShapeDrawnSidebar, parsed.ShapeDrawnSidebar);
        Assert.Equal(result.ShapeDrawnContent, parsed.ShapeDrawnContent);
        Assert.Equal((40, 30, 10, 4, 7), (parsed.TextReference, parsed.TextMatched, parsed.TextMissing, parsed.TextExtra, parsed.TextErrors));
        Assert.Equal(75, parsed.TextMatchedPercent);
        Assert.Equal(result.PixelDrawn, parsed.PixelDrawn);
        Assert.Equal(result.PixelDrawnLenient, parsed.PixelDrawnLenient);
        Assert.Equal((5, 80, 3), (parsed.MaxShape, parsed.MinText, parsed.MaxErrors));
        Assert.Equal(result.Notes, parsed.Notes);
    }

    [Fact]
    public void NotEnforcedGates_AreStoredAsNull()
    {
        var result = Result("Main") with { MaxShape = null, MinText = null, MaxErrors = null };

        var parsed = BoardResult.Parse(result.Serialize());

        Assert.Equal((null, null, null), (parsed.MaxShape, parsed.MinText, parsed.MaxErrors));
    }

    [Fact]
    public void TheReport_ListsEveryBoardInTheOrderOfTheTable_AndMarksTheMissingOnes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "murmur-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Chat.result.txt"), Result("Chat", shape: 33.25).Serialize());
            File.WriteAllText(Path.Combine(directory, "Old.result.txt"), "name=Old\n");

            var report = ParityReport.BuildReport(directory);

            var rows = report.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| Board", StringComparison.Ordinal) && !l.StartsWith("| ---", StringComparison.Ordinal)).ToArray();
            Assert.Equal(DesignBoards.All.Count, rows.Length);
            Assert.StartsWith("| Main | 1440x1406 | not rendered", rows[0], StringComparison.Ordinal);
            Assert.Contains(rows, r => r.StartsWith("| Chat | 1440x1080 | 33.25 | 20.00 | 10.00 | 75.0 (30/40) | 7 | 10 | 5.00 / 80.0 / 3 | 40.00 |", StringComparison.Ordinal));
            Assert.Contains("OnbComponents", report, StringComparison.Ordinal);
            Assert.Contains("[VERSION] placeholder", report, StringComparison.Ordinal);
            Assert.Contains("Sidebar 'Recent' list", report, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AResultOfAnEarlierRun_IsMarkedStale()
    {
        var directory = Path.Combine(Path.GetTempPath(), "murmur-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var old = Path.Combine(directory, "Main.result.txt");
            File.WriteAllText(old, (Result("Main") with { RunId = "old-run" }).Serialize());
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
            File.WriteAllText(Path.Combine(directory, "Chat.result.txt"), (Result("Chat") with { RunId = "new-run" }).Serialize());

            var report = ParityReport.BuildReport(directory);

            var main = report.Split('\n').Single(l => l.StartsWith("| Main |", StringComparison.Ordinal));
            var chat = report.Split('\n').Single(l => l.StartsWith("| Chat |", StringComparison.Ordinal));
            Assert.Contains("STALE", main, StringComparison.Ordinal);
            Assert.DoesNotContain("STALE", chat, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
