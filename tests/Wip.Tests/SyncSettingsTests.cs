using Wip.Configuration;
using Wip.Yaml;

namespace Wip.Tests;

public class SyncSettingsTests
{
    [Theory]
    [InlineData("{mount: '/host:src'}", "sync.mount")]
    [InlineData("{target: '/app:src'}", "sync.target")]
    [InlineData("{volume: 'app:src'}", "sync.volume")]
    public void ColonInBindSpecificationFieldsFailsAtConfigLoad(string sync, string path)
    {
        var error = Assert.Throws<ConfigException>(() =>
            new Config(YamlLoader.LoadText("sync: " + sync, allowAliases: false)));

        Assert.Contains(path + " must not contain colons", error.Message);
    }
}
