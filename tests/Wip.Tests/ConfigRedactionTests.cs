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
    [InlineData("CONNECTION_STRING")]
    [InlineData("DATABASE_URL")]
    [InlineData("DSN")]
    [InlineData("COOKIE")]
    [InlineData("SESSION")]
    public void RedactsCommonCredentialNames(string name)
    {
        var config = ConfigWithEnvironment(name, "sensitive-value");
        var environment = EnvironmentFrom(config.ToMapping());

        Assert.Equal("[REDACTED]", environment[name]);
    }

    [Theory]
    [InlineData("connection_string")]
    [InlineData("database_url")]
    [InlineData("dsn")]
    [InlineData("cookie")]
    [InlineData("session")]
    public void RedactsSupplementalSecretNamesOutsideEnvironmentBlocks(string name)
    {
        var config = ConfigFrom($$"""
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: example
                {{name}}: sensitive-value
            """);

        Assert.Equal("[REDACTED]", DependencyFrom(config.ToMapping())[name]);
    }

    [Fact]
    public void DoesNotRedactPublicKeys()
    {
        var config = ConfigFrom("""
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: example
                public_key: safe-to-display
            """);

        Assert.Equal("safe-to-display", DependencyFrom(config.ToMapping())["public_key"]);
    }

    [Fact]
    public void RedactsEveryDependencyEnvironmentValueRegardlessOfVariableName()
    {
        var config = ConfigWithEnvironment("MY_UNUSUAL_SETTING", "sensitive-value");
        var mapping = config.ToMapping();
        var environment = EnvironmentFrom(mapping);

        Assert.Equal("[REDACTED]", environment["MY_UNUSUAL_SETTING"]);
        Assert.Equal("example", DependencyFrom(mapping)["image"]);
    }

    [Fact]
    public void RedactsEnvironmentInheritedByCommands()
    {
        var config = ConfigFrom("""
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: example
                workdir: /workspace
                env:
                  CUSTOM_NAME: inherited-secret
            commands:
              test:
                command: dotnet test
            """);

        var mapping = config.ToMapping();
        var commands = (OrderedDictionary<string, object?>)mapping["commands"]!;
        var test = (OrderedDictionary<string, object?>)commands["test"]!;
        var environment = (OrderedDictionary<string, object?>)test["env"]!;

        Assert.Equal("[REDACTED]", environment["CUSTOM_NAME"]);
        Assert.Equal("/workspace", test["workdir"]);
    }

    [Fact]
    public void PreservesNonSecretSettingsIncludingOrdinaryUrls()
    {
        var config = ConfigFrom("""
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: example/app:latest
                workdir: /workspace
                homepage_url: https://example.test/service
                env:
                  SERVICE_URL: https://example.test/api
            """);

        var dependency = DependencyFrom(config.ToMapping());

        Assert.Equal("example/app:latest", dependency["image"]);
        Assert.Equal("/workspace", dependency["workdir"]);
        Assert.Equal("https://example.test/service", dependency["homepage_url"]);
        Assert.Equal("[REDACTED]", EnvironmentFrom(config.ToMapping())["SERVICE_URL"]);
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

        return ConfigFrom(yaml);
    }

    private static Config ConfigFrom(string yaml) =>
        new(YamlLoader.LoadText(yaml, allowAliases: false));

    private static OrderedDictionary<string, object?> DependencyFrom(
        OrderedDictionary<string, object?> mapping)
    {
        var dependencies = (OrderedDictionary<string, object?>)mapping["dependencies"]!;
        return (OrderedDictionary<string, object?>)dependencies["app"]!;
    }

    private static OrderedDictionary<string, object?> EnvironmentFrom(
        OrderedDictionary<string, object?> mapping)
    {
        return (OrderedDictionary<string, object?>)DependencyFrom(mapping)["env"]!;
    }
}
