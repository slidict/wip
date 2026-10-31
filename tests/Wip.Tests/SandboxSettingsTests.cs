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
    [InlineData("sleep")]
    [InlineData("/bin/sleep")]
    public void EntrypointRoundTripsAsOneExecutable(string executable)
    {
        var settings = Load("resource_namespace: example\nsandboxes: [{name: box, image: tool, entrypoint: " + executable + ", command: [infinity]}]").SandboxResources;
        Assert.Equal(executable, settings.Sandboxes[0].Entrypoint);
        Assert.Equal(executable, Load(YamlWriter.Dump(settings.ToMapping())).SandboxResources.Sandboxes[0].Entrypoint);
    }

    // Loading runs for every command, so refusing here would also block sandbox destroy on a
    // config that already declares this. Sandbox create refuses it instead.
    [Fact]
    public void EntrypointWithoutCommandStillLoads()
    {
        var sandbox = Load("resource_namespace: example\nsandboxes: [{name: box, image: tool, entrypoint: sleep}]").SandboxResources.Sandboxes[0];
        Assert.Equal("sleep", sandbox.Entrypoint);
        Assert.Null(sandbox.Command);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[sleep]")]
    [InlineData("{}")]
    [InlineData("true")]
    [InlineData("123")]
    [InlineData("''")]
    [InlineData("'sleep infinity'")]
    [InlineData("'-sleep'")]
    [InlineData("\"sleep\\nbreak\"")]
    public void InvalidEntrypointsFailDuringLoading(string entrypoint)
    {
        var error = Assert.Throws<ConfigException>(() => Load("resource_namespace: example\nsandboxes: [{name: box, image: tool, entrypoint: " + entrypoint + "}]"));
        Assert.Contains("sandboxes.box.entrypoint", error.Message);
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
    [InlineData("sandboxes: [{name: box, image: tool, report_relay: 'true'}]")]
    [InlineData("sandboxes: [{name: box, image: tool, report_relay: null}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {source: .}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: m, target: /x}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: [image]}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: ''}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: '-rm'}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: m, mount: /data}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: m, mount: /data/host}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /host-src/data, sync: {image: m}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: m, mount: relative}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: m, mount: /a/../b}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: m, mount: '/host-src,ro'}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: m, source: 'a,b'}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: m, delete: 'false'}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: m, exclude: [.git, null]}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: m, exclude: ['--delete']}}]")]
    [InlineData("volumes: [{name: data, persistent: true, mount: /data, sync: {image: m, exclude: .git}}]")]
    public void InvalidResourcesFailAtConfigLoad(string yaml) => Assert.Throws<ConfigException>(() => Load("resource_namespace: example\n" + yaml));

    [Fact]
    public void DeclaredSyncKeepsItsSourceRawAndDefaultsTheRest()
    {
        var config = Load("""
            resource_namespace: example
            volumes:
              - name: src
                persistent: true
                mount: /workspace/src
                sync: {image: mirror:1}
              - name: data
                persistent: true
                mount: /data
            """);
        var sync = config.SandboxResources.Volumes[0].Sync!;
        Assert.Equal(".", sync.Source);
        Assert.Equal("/host-src", sync.Mount);
        Assert.Equal("mirror:1", sync.Image);
        Assert.True(sync.Delete);
        Assert.Empty(sync.Exclude);
        Assert.Null(config.SandboxResources.Volumes[1].Sync);
    }

    [Fact]
    public void NormalizedOutputCarriesSyncOnlyForVolumesThatDeclareIt()
    {
        var config = Load("""
            resource_namespace: example
            volumes:
              - name: src
                persistent: true
                mount: /workspace/src
                sync: {source: ../tree, image: mirror:1, exclude: [.git], delete: false}
              - name: data
                persistent: true
                mount: /data
            """);
        var volumes = config.SandboxResources.ToMapping()["volumes"] as List<object?>;
        var emitted = RubyValue.AsMapping(RubyValue.AsMapping(volumes![0])!["sync"])!;
        Assert.Equal("../tree", emitted["source"]);
        Assert.Equal(false, emitted["delete"]);
        Assert.False(RubyValue.AsMapping(volumes[1])!.ContainsKey("sync"));

        // Round-tripping the projection must not change the declaration. Compared field by
        // field: the record's Exclude is a list, so record equality would compare references.
        var projected = RubyValue.NewMapping();
        foreach (var key in new[] { "resource_namespace", "volumes", "sandboxes" }) projected[key] = config.ToMapping()[key];
        var restored = Load(YamlWriter.Dump(projected)).SandboxResources.Volumes;
        Assert.Equal(config.SandboxResources.Volumes.Select(v => v.Name), restored.Select(v => v.Name));
        var original = config.SandboxResources.Volumes[0].Sync!;
        var roundTripped = restored[0].Sync!;
        Assert.Equal(original with { Exclude = [] }, roundTripped with { Exclude = [] });
        Assert.Equal(original.Exclude, roundTripped.Exclude);
        Assert.Null(restored[1].Sync);
        Assert.DoesNotContain(Load(Sample).SandboxResources.Volumes, v => v.Sync is not null);
    }

    [Fact]
    public void DeclaredCommandIsATypedArgvAndRoundTripsWithoutShellInterpretation()
    {
        var config = Load("""
            resource_namespace: example
            sandboxes:
              - name: box
                image: tool
                command: [sleep, infinity, 'a b; $(literal)']
            """);

        Assert.Equal(["sleep", "infinity", "a b; $(literal)"], config.SandboxResources.Sandboxes[0].Command);
        var mapping = config.SandboxResources.ToMapping();
        var sandbox = RubyValue.AsMapping(RubyValue.AsSequence(mapping["sandboxes"])![0])!;
        Assert.Equal(["sleep", "infinity", "a b; $(literal)"], RubyValue.AsSequence(sandbox["command"])!.Cast<string>());
        var restored = Load(YamlWriter.Dump(mapping));
        var restoredSandbox = RubyValue.AsMapping(RubyValue.AsSequence(restored.SandboxResources.ToMapping()["sandboxes"])![0])!;
        Assert.Equal(RubyValue.AsSequence(sandbox["command"]), RubyValue.AsSequence(restoredSandbox["command"]));
    }

    [Theory]
    [InlineData("sleep infinity", "sequence")]
    [InlineData("null", "sequence")]
    [InlineData("{}", "sequence")]
    [InlineData("[]", "empty")]
    [InlineData("['']", "nonempty")]
    [InlineData("['   ']", "whitespace")]
    [InlineData("[sleep, null]", "nonempty")]
    [InlineData("[sleep, 123]", "nonempty")]
    [InlineData("[sleep, true]", "nonempty")]
    [InlineData("[sleep, \"line\\nbreak\"]", "control")]
    public void InvalidCommandsFailDuringConfigurationLoading(string command, string reason)
    {
        var error = Assert.Throws<ConfigException>(() => Load(
            "resource_namespace: example\nsandboxes: [{name: box, image: tool, command: " + command + "}]"));

        Assert.Contains("sandboxes.box.command", error.Message);
        Assert.Contains(reason, error.Message);
    }

    [Theory]
    [InlineData("[tail, -f, /dev/null]", "tail", "-f", "/dev/null")]
    [InlineData("[nginx, -g, 'daemon off;']", "nginx", "-g", "daemon off;")]
    [InlineData("[sh, -c, 'exec zap.sh -daemon -host 0.0.0.0']", "sh", "-c", "exec zap.sh -daemon -host 0.0.0.0")]
    public void CommandFlagsAndLaterWhitespaceAreLiteralArguments(string yaml, string executable, string flag, string argument)
    {
        var settings = Load("resource_namespace: example\nsandboxes: [{name: box, image: tool, command: " + yaml + "}]").SandboxResources;

        Assert.Equal([executable, flag, argument], settings.Sandboxes[0].Command);
    }

    [Theory]
    [InlineData("-daemon")]
    [InlineData("--")]
    public void CommandCanStartWithAnEntrypointFlag(string argument)
    {
        var settings = Load("resource_namespace: example\nsandboxes: [{name: box, image: tool, command: ['" + argument + "']}]").SandboxResources;

        Assert.Equal([argument], settings.Sandboxes[0].Command);
    }

    [Fact]
    public void LaterCommandArgumentsCanBeOnlySpaces()
    {
        var settings = Load("resource_namespace: example\nsandboxes: [{name: box, image: tool, command: [printf, '   ']}]").SandboxResources;

        Assert.Equal(["printf", "   "], settings.Sandboxes[0].Command);
    }

    [Theory]
    [InlineData("[sleep 600]")]
    [InlineData("['sleep 600']")]
    [InlineData("[' sleep']")]
    [InlineData("['sleep ']")]
    [InlineData("['sleep\u00a0600']")]
    public void WhitespaceInTheFirstCommandArgumentFailsDuringConfigurationLoading(string command)
    {
        var error = Assert.Throws<ConfigException>(() => Load(
            "resource_namespace: example\nsandboxes: [{name: box, image: tool, command: " + command + "}]"));

        Assert.Contains("sandboxes.box.command", error.Message);
        Assert.Contains("first argument", error.Message);
        Assert.Contains("whitespace", error.Message);
    }

    [Fact]
    public void AbsentCommandDoesNotChangeTheNormalizedSandbox()
    {
        var settings = Load(Sample).SandboxResources;
        Assert.All(settings.Sandboxes, sandbox => Assert.Null(sandbox.Command));
        var sandboxes = settings.ToMapping()["sandboxes"];

        Assert.All(RubyValue.AsSequence(sandboxes)!, sandbox =>
            Assert.False(RubyValue.AsMapping(sandbox)!.ContainsKey("command")));
    }

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

    [Fact]
    public void ReportRelayIsOptInAndRoundTripsOnlyWhenEnabled()
    {
        var config = Load("""
            resource_namespace: example
            volumes: [{name: state, persistent: true, mount: /root/.claude}, {name: runtime, persistent: false, mount: /run/wip/other}]
            sandboxes:
              - {name: first, image: tool, volumes: [state, runtime], report_relay: true}
              - {name: second, image: tool, report_relay: false}
              - {name: third, image: tool}
            """);
        Assert.Equal([true, false, false], config.SandboxResources.Sandboxes.Select(s => s.ReportRelay));
        var mappings = config.SandboxResources.ToMapping()["sandboxes"] as List<object?>;
        Assert.Equal(true, RubyValue.AsMapping(mappings![0])!["report_relay"]);
        Assert.False(RubyValue.AsMapping(mappings[1])!.ContainsKey("report_relay"));
        Assert.False(RubyValue.AsMapping(mappings[2])!.ContainsKey("report_relay"));
        Assert.DoesNotContain(Load(Sample).SandboxResources.Sandboxes, s => s.ReportRelay);
    }
}
