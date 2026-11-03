namespace Wip.Tests;

/// <summary>
/// Test classes that touch process-global <see cref="Console"/> state: they redirect
/// <c>Console.Out</c>, change <c>Console.OutputEncoding</c> (which replaces <c>Console.Out</c>),
/// or invoke a command without <c>InvocationConfiguration.Output</c>, so System.CommandLine
/// writes to <c>Console.Out</c>. Run in parallel, one class's text lands in another's capture,
/// which made <c>VersionOptionTests</c> fail intermittently. Keep them serialized.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleStateCollection
{
    public const string Name = "Console state";
}
