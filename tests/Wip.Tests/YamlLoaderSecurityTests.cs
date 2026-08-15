using Wip.Yaml;

namespace Wip.Tests;

public class YamlLoaderSecurityTests
{
    [Fact]
    public void RejectsExcessiveNesting()
    {
        var yaml = $"{new string('[', 102)}null{new string(']', 102)}";

        var exception = Assert.Throws<ConfigException>(
            () => YamlLoader.LoadText(yaml, allowAliases: false, path: "nested.yml"));

        Assert.Equal(
            "Could not parse nested.yml: YAML nesting exceeds the limit of 100",
            exception.Message);
    }

    [Fact]
    public void AcceptsNestingAtTheLimit()
    {
        var yaml = $"{new string('[', 100)}null{new string(']', 100)}";

        Assert.NotNull(YamlLoader.LoadText(yaml, allowAliases: false));
    }
}
