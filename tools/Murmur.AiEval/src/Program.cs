using System.Text;
using Murmur.AiEval;
using Murmur.Intelligence;

Console.OutputEncoding = new UTF8Encoding(false);
var parsed = CommandLine.Parse(args);
if (parsed.Help)
{
    Console.WriteLine(CommandLine.Usage);
    return 0;
}

if (parsed.Error is { } error)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.Usage);
    return 2;
}

if (parsed.GenerateFixtureDir is { } fixtureDir)
{
    var (fixturePath, questionsPath) = FixtureGenerator.WriteFiles(fixtureDir);
    Console.WriteLine($"wrote {fixturePath}");
    Console.WriteLine($"wrote {questionsPath}");
    return 0;
}

var options = parsed.Eval!;
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

var llmOptions = LocalLlmOptions.FromEnvironment();
if (options.Threads is { } threads)
{
    llmOptions = llmOptions with { Threads = threads };
}

await using var llm = new LocalLlm(llmOptions, isRecordingActive: static () => false);
EvalReport report;
try
{
    report = await EvalRunner.RunAsync(
        options, llm, LlmAvailability.From(llmOptions), LlmAvailability.Describe(llmOptions), Console.Error, cancel.Token);
}
catch (EvalSetupException ex)
{
    Console.Error.WriteLine($"[ai-eval] {ex.Message}");
    return 2;
}

var json = ReportWriter.ToJson(report);
var markdown = ReportWriter.ToMarkdown(report);
WriteFile(options.OutPath, json);
if (options.MarkdownPath is { } markdownPath)
{
    WriteFile(markdownPath, markdown);
}

Console.WriteLine(markdown);
return report.Success ? 0 : 1;

static void WriteFile(string path, string content)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    File.WriteAllText(path, content, new UTF8Encoding(false));
}
