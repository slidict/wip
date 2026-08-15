using Wip.Configuration;
using Wip.Yaml;

namespace Wip.Tests;

public class ConfigRedactionTests
{
    [Theory]
    [InlineData("API_KEY")]
    [InlineData("AWS_ACCESS_KEY_ID")]
    [InlineData("SSH_KEY")]
    [InlineData("SIGNING-KEY")]
    [InlineData("PASSPHRASE")]
    [InlineData("PWD")]
    public void RedactsCommonCredentialNames(string name)
    {
        var config = ConfigWithEnvironment(name, "sensitive-value");
        var environment = EnvironmentFrom(config.ToMapping());

        Assert.Equal("[REDACTED]", environment[name]);
    }

    [Fact]
    public void DoesNotRedactPublicKeys()
    {
        var config = ConfigWithEnvironment("PUBLIC_KEY", "safe-to-display");
        var environment = EnvironmentFrom(config.ToMapping());

        Assert.Equal("safe-to-display", environment["PUBLIC_KEY"]);
    }

    private static Config ConfigWithEnvironment(string name, string value)
    {
        var yaml = $"""
                   version: 1
                   mode: container
                   container: app
                   dependencies:
                     app:
                       image: example
                       env:
                         {name}: {value}
                   """;

        return new Config(YamlLoader.LoadText(yaml, allowAliases: false));
    }

    private static OrderedDictionary<string, object?> EnvironmentFrom(
        OrderedDictionary<string, object?> mapping)
    {
        var dependencies = (OrderedDictionary<string, object?>)mapping["dependencies"]!;
        var app = (OrderedDictionary<string, object?>)dependencies["app"]!;
        return (OrderedDictionary<string, object?>)app["env"]!;
    }
}
