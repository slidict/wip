using Wip;

namespace Wip.Cli;

internal static class Program
{
    internal static int Main(string[] args)
    {
        try
        {
            Console.WriteLine($"wip {WipVersion.Current}");
            return 0;
        }
        catch (WipException exception)
        {
            Console.Error.WriteLine($"wip: {exception.Message}");
            return 1;
        }
    }
}
