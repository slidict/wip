namespace Wip.Ai;

/// <summary>Turns a bounded project description and user request into a wip.yml candidate.</summary>
public interface IWipAiProvider
{
    string Generate(string prompt, CancellationToken cancellationToken = default);
}
