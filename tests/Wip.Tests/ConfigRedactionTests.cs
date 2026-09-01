using Wip.Configuration;
using Wip.Yaml;

namespace Wip.Tests;

/// <summary>
/// Pins what <c>wip config</c> is allowed to print. Two rules are at work and the tests keep
/// them apart: inside a dependency or command <c>env</c> block every value is masked, and
/// outside one only keys that name credential material are.
/// </summary>
public class ConfigRedactionTests
{
    /// <summary>
    /// Outside an <c>env</c> block redaction is name-based, so these are the names that have to
    /// match. Placing them on the dependency itself keeps the blanket <c>env</c> rule out of the
    /// way — inside <c>env</c> they would be masked whatever the pattern said.
    /// </summary>
    [Theory]
    [InlineData("api_key")]
    [InlineData("access_key")]
    [InlineData("ssh_key")]
    [InlineData("signing-key")]
    [InlineData("passphrase")]
    [InlineData("password")]
    [InlineData("token")]
    [InlineData("connection_string")]
    [InlineData("database_url")]
    [InlineData("dsn")]
    [InlineData("cookie")]
    [InlineData("session")]
    public void RedactsCredentialNamesOutsideEnvironmentBlocks(string name)
    {
        var config = ConfigFrom($"""
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: example
                {name}: sensitive-value
            """);

        Assert.Equal("[REDACTED]", DependencyFrom(config.ToMapping())[name]);
    }

    /// <summary>A key that merely contains "key" is not a secret; public_key stays readable.</summary>
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

    /// <summary>
    /// The point of the blanket rule: a variable holding a credential under a house-style name
    /// is masked too, while the variable names and the rest of the dependency stay printable.
    /// </summary>
    [Fact]
    public void RedactsEveryDependencyEnvironmentValueRegardlessOfVariableName()
    {
        var config = ConfigWithEnvironment("MY_UNUSUAL_SETTING", "sensitive-value");
        var mapping = config.ToMapping();
        var environment = EnvironmentFrom(mapping);

        Assert.True(environment.ContainsKey("MY_UNUSUAL_SETTING"));
        Assert.Equal("[REDACTED]", environment["MY_UNUSUAL_SETTING"]);
        Assert.Equal("example", DependencyFrom(mapping)["image"]);
    }

    /// <summary>Commands inherit the primary dependency's env, and inherit the masking with it.</summary>
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

    /// <summary>
    /// A URL is not a secret by virtue of being a URL: one outside <c>env</c> stays readable,
    /// and one inside is masked only because everything in <c>env</c> is.
    /// </summary>
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

        var mapping = config.ToMapping();
        var dependency = DependencyFrom(mapping);

        Assert.Equal("example/app:latest", dependency["image"]);
        Assert.Equal("/workspace", dependency["workdir"]);
        Assert.Equal("https://example.test/service", dependency["homepage_url"]);
        Assert.Equal("[REDACTED]", EnvironmentFrom(mapping)["SERVICE_URL"]);
    }

    /// <summary>Nothing is masked when the caller asks for the unredacted mapping.</summary>
    [Fact]
    public void KeepsEveryValueWhenRedactionIsDisabled()
    {
        var config = ConfigWithEnvironment("DATABASE_PASSWORD", "swordfish");
        var mapping = config.ToMapping(redact: false);

        Assert.Equal("swordfish", EnvironmentFrom(mapping)["DATABASE_PASSWORD"]);
    }

    /// <summary>Builds a single-dependency config whose env holds one variable.</summary>
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

    /// <summary>Loads a config from inline YAML, the way a wip.yml on disk would be read.</summary>
    private static Config ConfigFrom(string yaml) =>
        new(YamlLoader.LoadText(yaml, allowAliases: false));

    /// <summary>Digs out the <c>app</c> dependency the fixtures above all define.</summary>
    private static OrderedDictionary<string, object?> DependencyFrom(
        OrderedDictionary<string, object?> mapping)
    {
        var dependencies = (OrderedDictionary<string, object?>)mapping["dependencies"]!;
        return (OrderedDictionary<string, object?>)dependencies["app"]!;
    }

    /// <summary>Digs out that dependency's <c>env</c> block.</summary>
    private static OrderedDictionary<string, object?> EnvironmentFrom(
        OrderedDictionary<string, object?> mapping)
    {
        return (OrderedDictionary<string, object?>)DependencyFrom(mapping)["env"]!;
    }
}
