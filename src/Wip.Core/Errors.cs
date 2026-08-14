namespace Wip;

/// <summary>Base class for every error wip reports to the user by message alone.</summary>
public class WipException : Exception
{
    public WipException(string message) : base(message)
    {
    }

    public WipException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>A problem with wip.yml, compose.yml, or the options given on the command line.</summary>
public sealed class ConfigException : WipException
{
    public ConfigException(string message) : base(message)
    {
    }

    public ConfigException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>An executable wip needs (wslc, a compose binary) could not be located.</summary>
public sealed class CommandNotFoundException : WipException
{
    public CommandNotFoundException(string message) : base(message)
    {
    }
}
