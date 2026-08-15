using Wip.Execution;

namespace Wip.Tests;

public class CommandDisplayTests
{
    [Theory]
    [InlineData("--build-arg", "API_TOKEN=super-secret", "--build-arg API_TOKEN\\=\\*\\*\\*")]
    [InlineData("--secret", "id=signing-key,src=/private/key", "--secret id\\=\\*\\*\\*")]
    [InlineData("--env", "PASSWORD=hunter2", "--env PASSWORD\\=\\*\\*\\*")]
    public void RedactsSensitiveOptionValues(string option, string value, string expected)
    {
        var actual = CommandDisplay.ForDebug(["wslc.exe", "build", option, value, "."]);

        Assert.Equal($"wslc.exe build {expected} .", actual);
    }

    [Theory]
    [InlineData("--build-arg=API_TOKEN=super-secret", "--build-arg\\=\\*\\*\\*")]
    [InlineData("--secret=id=signing-key,src=/private/key", "--secret\\=\\*\\*\\*")]
    [InlineData("--env=PASSWORD=hunter2", "--env\\=\\*\\*\\*")]
    public void RedactsInlineSensitiveOptionValues(string argument, string expected)
    {
        var actual = CommandDisplay.ForDebug(["wslc.exe", "build", argument, "."]);

        Assert.Equal($"wslc.exe build {expected} .", actual);
    }
}
