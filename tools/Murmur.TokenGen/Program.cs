using System.Text;

namespace Murmur.TokenGen;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: Murmur.TokenGen <tokens.json> <Tokens.axaml>");
            return 2;
        }

        var output = TokenGenerator.Generate(File.ReadAllText(args[0]));
        File.WriteAllBytes(args[1], new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(output));
        return 0;
    }
}
