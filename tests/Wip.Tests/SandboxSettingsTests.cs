using Wip.Configuration;
using Wip.Yaml;

namespace Wip.Tests;

public class SandboxSettingsTests
{
    private const string Sample = """
        version: 1
        resource_namespace: example
        volumes:
          - name: project
            persistent: true
            mount: /workspace
          - name: private-state
            persistent: true
            mount: /home/user/state
          - name: scratch
            persistent: false
            mount: /tmp/work
        sandboxes:
          - name: first
            image: example/tool:1
            volumes: [project, private-state, scratch]
          - name: second
            image: example/tool:1
            volumes: [project]
        """;

    private static Config Load(string yaml) => new(YamlLoader.LoadText(yaml, allowAliases: false));

    [Fact]
    public void GenericResourcesShareReferencesWithoutAssigningRoles()
    {
        var resources = Load(Sample).SandboxResources;
        Assert.Equal(3, resources.Volumes.Count);
        Assert.False(resources.Volumes.Single(v => v.Name == "scratch").Persistent);
        Assert.Equal("/workspace", resources.Volumes[0].Mount);
        Assert.Equal(["project", "private-state", "scratch"], resources.Sandboxes[0].Volumes);
        Assert.Equal(["project"], resources.Sandboxes[1].Volumes);
    }

    [Fact]
    public void NormalizedOutputPreservesResourcesAcrossYamlRoundTrip()
    {
        var original = Load(Sample);
        // Legacy effective output includes null compose/sync entries and is not an input
        // document. Round-trip the additive resource projection, not those legacy defaults.
        var projected = RubyValue.NewMapping();
        foreach (var key in new[] { "resource_namespace", "volumes", "sandboxes" }) projected[key] = original.ToMapping()[key];
        var restored = Load(YamlWriter.Dump(projected));
        Assert.Equal(original.SandboxResources.Volumes, restored.SandboxResources.Volumes);
        Assert.Equal(original.SandboxResources.Sandboxes[0].Volumes, restored.SandboxResources.Sandboxes[0].Volumes);
        Assert.Equal(original.SandboxResources.Sandboxes[1].Name, restored.SandboxResources.Sandboxes[1].Name);
    }

    [Theory]
    [InlineData("volumes: [{name: data, persistent: true, mount: /a}, {name: data, persistent: false, mount: /b}]")]
    [InlineData("sandboxes: [{name: box, image: tool}, {name: box, image: tool}]")]
    [InlineData("sandboxes: [{name: box, image: tool, volumes: [missing]}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /a}]\nsandboxes: [{name: box, image: tool, volumes: [data, data]}]")]
    [InlineData("volumes: [{name: a, persistent: true, mount: /data}, {name: b, persistent: false, mount: /data}]\nsandboxes: [{name: box, image: tool, volumes: [a, b]}]")]
    [InlineData("volumes: [{name: data, mount: /data}]")]
    [InlineData("volumes: [{name: data, persistent: 'false', mount: /data}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: relative}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /a/../b}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /a//b}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /a/}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: '/data,readonly'}]")]
    [InlineData("volumes: [{name: Invalid, persistent: true, mount: /a}]")]
    [InlineData("sandboxes: [{name: box}]")]
    [InlineData("sandboxes: [{name: box, image: tool, mode: shared}]")]
    [InlineData("volumes: null")]
    [InlineData("sandboxes: {box: {image: tool}}")]
    [InlineData("volumes: [data]")]
    [InlineData("sandboxes: [{name: box, image: tool, volumes: data}]")]
    public void InvalidResourcesFailAtConfigLoad(string yaml) => Assert.Throws<ConfigException>(() => Load("resource_namespace: example\n" + yaml));

    [Fact]
    public void EqualMountPathsAreValidWhenNeverCombinedInOneSandbox()
    {
        var config = Load("""
            resource_namespace: example
            volumes: [{name: a, persistent: true, mount: /data}, {name: b, persistent: true, mount: /data}]
            sandboxes: [{name: first, image: tool, volumes: [a]}, {name: second, image: tool, volumes: [b]}]
            """);
        Assert.Equal(2, config.SandboxResources.Sandboxes.Count);
    }

    [Fact]
    public void ExistingDependenciesAndTheirBindMountsRemainSeparate()
    {
        var config = Load(Sample + "\ncontainer: app\ndependencies:\n  app:\n    image: tool\n    volumes: ['.:/app']\n");
        Assert.Equal([".:/app"], RubyValue.AsSequence(config.Dependency("app")!["volumes"])!.Cast<string>());
        Assert.Equal(3, config.SandboxResources.Volumes.Count);
    }

    [Fact]
    public void LegacyConfigDoesNotGainResourceKeysOrChangeMode()
    {
        var config = Load("commands: {}\n");
        Assert.Empty(config.SandboxResources.Volumes);
        Assert.Empty(config.SandboxResources.Sandboxes);
        Assert.Equal("container", config.Mode);
        Assert.False(config.ToMapping().ContainsKey("sandboxes"));
        Assert.False(config.ToMapping().ContainsKey("volumes"));
    }

    [Fact]
    public void ResourceIdentityRequiresExplicitNamespaceRatherThanGuessingFromDirectory()
    {
        var error = Assert.Throws<ConfigException>(() => Load("sandboxes: [{name: box, image: tool}]"));
        Assert.Contains("resource_namespace must", error.Message);
        Assert.DoesNotContain("resources.resource_namespace", error.Message);
        Assert.Equal("example", Load(Sample).SandboxResources.ResourceNamespace);
    }
}
