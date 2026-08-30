using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wip.Ai;
using Wip.Build;
using Wip.Compose;
using Wip.Configuration;
using Wip.Diagnostics;
using Wip.Execution;
using Wip.Platform;
using Wip.Yaml;

namespace Wip.Cli;

/// <summary>
/// Shared services and the operational logic behind each command.
/// </summary>
/// <remarks>
/// Kept apart from the command tree in <see cref="Program"/> so the wiring stays readable:
/// System.CommandLine owns parsing, this owns what wip actually does.
/// </remarks>
internal sealed partial class CliContext
{
    /// <summary>
    /// Real Compose values that trigger a restart when a container has exited. <c>no</c> (the
    /// default) and anything unrecognised both mean "leave it alone". Exact match only — a
    /// typo'd value like "always-invalid" must stay inert rather than match a loose prefix.
    /// </summary>
    private static readonly string[] AutoRestartPolicies = ["always", "unless-stopped"];

    /// <summary>
    /// WSLC's container states, confirmed against microsoft/WSL's ContainerModel.h: 0
    /// invalid, 1 created, 2 running, 3 exited, 4 deleted. Unlike Docker there is no separate
    /// "dead" state — only <c>exited</c> is a live, restartable exit; <c>deleted</c> means the
    /// container itself is gone and needs <c>wip up</c> to recreate it, not <c>start</c>.
    /// </summary>
    private const int WslcContainerStateCreated = 1;

    private const int WslcContainerStateRunning = 2;

    private const int WslcContainerStateExited = 3;

    private const int WslcContainerStateDeleted = 4;

    /// <summary>What <c>wip up</c> should do about a container that is already listed.</summary>
    internal enum ContainerAction
    {
        /// <summary>No such container: create it.</summary>
        Create,

        /// <summary>Listed and startable: start it.</summary>
        Start,

        /// <summary>Already running: there is nothing to do.</summary>
        AlreadyRunning,
    }

    /// <summary>
    /// Decides from a container's listed state alone, so the rule is testable without a wslc.
    /// <c>wslc list --all</c> reports containers in every state, so existence is not the same
    /// question as startability: <c>start</c> on a running or deleted container fails with
    /// ERROR_INVALID_STATE, which used to surface as a bare "not in an appropriate state"
    /// from a plain <c>wip up</c>.
    /// </summary>
    /// <remarks>
    /// An unreadable state falls through to <see cref="ContainerAction.Start"/> — the
    /// behaviour before this existed. A state wslc reports in a shape wip does not understand
    /// is a reason to keep doing what used to work, not to start deleting and recreating
    /// containers on a guess.
    /// </remarks>
    internal static ContainerAction DecideContainerAction(bool exists, int? state) => (exists, state) switch
    {
        (false, _) => ContainerAction.Create,
        (true, WslcContainerStateRunning) => ContainerAction.AlreadyRunning,

        // Listed but gone. Starting it cannot work; recreating is the only way forward.
        (true, WslcContainerStateDeleted) => ContainerAction.Create,
        _ => ContainerAction.Start,
    };

    private readonly CliOptions options;
    private Config? config;
    private OrderedDictionary<string, string>? dotenv;
    private DebugReporter? reporter;
    private ComposeBridge? composeBridge;

    internal CliContext(CliOptions options) => this.options = options;

    internal IEnvironment Environment { get; } = new WindowsEnvironment();

    internal bool Debug =>
        options.Debug || !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("WIP_DEBUG"));

    internal ConfigLoader Loader => new(path: options.ConfigPath, envFile: options.EnvFile);

    internal Config Config => config ??= Loader.Load();

    internal DebugReporter Reporter => reporter ??= new DebugReporter(Debug, log: options.DebugLog);

    private OrderedDictionary<string, string> Dotenv => dotenv ??= new DotenvLoader(DotenvPath()).Load();

    private CommandResolver Resolver => new();

    private CommandBuilder Builder =>
        new(Resolver.Resolve(Config.WslcCommand), Config, Environment, Dotenv);

    private ComposeBridge Bridge => composeBridge ??= new ComposeBridge(
        new CommandResolver([], "compose command", ComposeBridge.InstallHint).Resolve(Config.ComposeCommand ?? "auto"),
        ComposeBridge.FilePath(Config.Path ?? ".", Config.ComposeFilePath),
        Config.ComposeProject);

    private ErrorInterpreter Interpreter => new(Environment.Architecture);

    private string DotenvPath() => options.EnvFile ?? Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(Config.Path!)) ?? ".", ".env");

    private bool Tty(bool requested) => requested && Environment.IsInteractive;

    // ---------------------------------------------------------------- commands

    internal int Version()
    {
        Console.WriteLine($"wip {WipVersion.Current}");
        try
        {
            return Execute([Resolver.Resolve(Config.WslcCommand), "version"], exitOnFailure: false);
        }
        catch (WipException)
        {
            // `wip version` has to report wip's own version even with no wip.yml in sight,
            // or on a machine where wslc is not installed yet.
            return 0;
        }
    }

    internal int Init(bool force, string? template, bool ai = false, string? url = null)
    {
        var path = Path.GetFullPath(options.ConfigPath ?? ConfigLoader.Filename);
        if (ai)
        {
            if (template is not null)
            {
                throw new WipException("--template cannot be combined with --ai");
            }

            return InitWithAi(path, url);
        }

        if (url is not null)
        {
            throw new WipException("--url requires --ai");
        }

        if (File.Exists(path) && !force)
        {
            throw new WipException($"{path} already exists (use --force to overwrite)");
        }

        var initializer = new Initializer(Path.GetDirectoryName(path), template);
        File.WriteAllText(path, initializer.Call());
        Console.Error.WriteLine(
            $"wip: wrote {path} (mode: {(initializer.IsCompose ? "compose-native" : "container")})");
        return 0;
    }

    private static int InitWithAi(string path, string? url)
    {
        var baseUrl = LocalAiProvider.ResolveBaseUrl(url);
        if (!LocalAiProvider.IsAvailable(baseUrl))
        {
            throw new WipException(
                $"{LocalAiProvider.NotFoundMessage(baseUrl)} Run `wip doctor` to check again.");
        }

        var model = LocalAiProvider.ResolveModel() ?? LocalAiProvider.DiscoverModel(baseUrl);

        var directory = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        Console.Error.WriteLine(
            "Describe the development environment wip should run, then press Enter twice " +
            "(once after your text, once more on a blank line) to finish:");
        var lines = new List<string>();
        string? line;
        while ((line = Console.ReadLine()) is not null && line.Length > 0)
        {
            lines.Add(line);
        }

        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        Console.Error.WriteLine($"wip: analyzing {directory}");
        var project = new ProjectAnalyzer(directory).Analyze();
        Console.Error.WriteLine($"wip: sending {project.Files.Count} selected project files to {model} at {baseUrl}");
        var candidate = new WipAiGenerator(new LocalAiProvider(baseUrl, model)).Generate(
            string.Join(System.Environment.NewLine, lines), project, existing, path);

        Console.WriteLine(candidate);
        Console.Error.Write(existing is null
            ? "Save this wip.yml? [y/N] "
            : "Replace the existing wip.yml with this update? [y/N] ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("wip: not saved");
            return 0;
        }

        File.WriteAllText(path, candidate);
        Console.Error.WriteLine($"wip: wrote {path}");
        return 0;
    }

    internal int HelpAi(string? url, IReadOnlyList<string> question)
    {
        var baseUrl = LocalAiProvider.ResolveBaseUrl(url);
        if (!LocalAiProvider.IsAvailable(baseUrl))
        {
            throw new WipException(
                $"{LocalAiProvider.NotFoundMessage(baseUrl)} Run `wip doctor` to check again.");
        }

        var model = LocalAiProvider.ResolveModel() ?? LocalAiProvider.DiscoverModel(baseUrl);
        var asked = question.Count > 0 ? string.Join(' ', question) : ReadQuestion();
        if (string.IsNullOrWhiteSpace(asked))
        {
            throw new WipException("A question is required");
        }

        Console.Error.WriteLine($"wip: asking {model} at {baseUrl}");
        var answer = new LocalAiProvider(baseUrl, model).Generate(HelpAiPrompt(asked));
        Console.WriteLine(answer);
        return 0;
    }

    private static string ReadQuestion()
    {
        Console.Error.WriteLine(
            "Ask wip how to do something, then press Enter twice (once after your question, " +
            "once more on a blank line) to finish:");
        var lines = new List<string>();
        string? line;
        while ((line = Console.ReadLine()) is not null && line.Length > 0)
        {
            lines.Add(line);
        }

        return string.Join(System.Environment.NewLine, lines);
    }

    private static string HelpAiPrompt(string question) => $$"""
        You answer questions about how to use the wip CLI, a developer-friendly wrapper around
        Microsoft WSLC. Answer only from the reference below; say plainly that it is not covered
        there rather than guessing.

        <wip-help>
        {{Program.HelpText()}}
        </wip-help>

        Question:
        {{question}}
        """;

    internal int Doctor(string? url = null)
    {
        var results = new Doctor(Loader, Environment).Call(url);
        foreach (var result in results)
        {
            Console.WriteLine($"[{result.Level.ToString().ToUpperInvariant()}] {result.Message}");
        }

        return results.Any(result => result.Level == Diagnostics.Doctor.Level.Fail) ? 1 : 0;
    }

    internal int ShowConfig()
    {
        Console.Write(YamlWriter.Dump(Config.ToMapping()));
        return 0;
    }

    internal int Build(IReadOnlyList<string> extra, bool noCache)
    {
        var arguments = WithNoCache(StripSeparator(extra), noCache);
        var settings = Config.Command("build") ?? RubyValue.NewMapping();
        var context = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(Config.Path!))!,
            RubyValue.Presence(settings.GetValueOrDefault("context")) ?? "."));

        Console.Error.WriteLine($"wip: staging build context ({context})");
        return StageAndBuild(context, settings, arguments);
    }

    internal int Up(bool detach, bool sync, bool noCache, bool watch, double interval)
    {
        WarnWslProject();

        if (Config.IsCompose)
        {
            return UpViaComposeBridge(detach, sync, watch);
        }

        // Validated up front, before any startup side effect (image build, network or
        // container creation) — otherwise a bad --interval would only surface after
        // everything was already running.
        if (watch && interval <= 0)
        {
            throw new ConfigException("--interval must be a positive number");
        }

        EnsureComposeImages(noCache);
        EnsureNetwork();
        foreach (var name in SidecarNames())
        {
            EnsureDependency(name);
        }

        if (sync)
        {
            SyncBeforeBoot();
        }

        // --watch polls in a loop after boot, which cannot share this one thread with an
        // attached primary container, so it forces the same behaviour -d gives.
        EnsureContainer(detach || watch);

        if (watch)
        {
            WatchRestarts(interval);
        }

        return 0;
    }

    internal int Sync(bool watch, double? interval)
    {
        var settings = RequiredSync();
        WarnShadowedCommand("sync");
        EnsureSyncImage(settings);

        if (!watch)
        {
            return RunSync();
        }

        var seconds = interval ?? settings.Interval;
        if (seconds <= 0)
        {
            throw new ConfigException("--interval must be a positive number");
        }

        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"wip: syncing {settings.Source} -> {settings.Volume}:{settings.Target} every {seconds}s (Ctrl-C to stop)"));

        return Loop(seconds, () => RunSync(exitOnFailure: false), "sync stopped");
    }

    internal int Stop()
    {
        if (Config.IsCompose)
        {
            return Execute(Bridge.Stop(), exitOnFailure: false);
        }

        Execute(Builder.Stop(), exitOnFailure: false);
        foreach (var name in SidecarNames())
        {
            Execute(Builder.DependencyStop(name), exitOnFailure: false);
        }

        return 0;
    }

    internal int Down()
    {
        if (Config.IsCompose)
        {
            return Execute(Bridge.Down(), exitOnFailure: false);
        }

        Execute(Builder.Remove(), exitOnFailure: false);
        foreach (var name in SidecarNames())
        {
            Execute(Builder.DependencyRemove(name), exitOnFailure: false);
        }

        return 0;
    }

    /// <summary>
    /// Restarts without a rebuild: stop, then let the same create-vs-start decision
    /// <see cref="EnsureContainer"/> already makes for <c>wip up</c> bring it back — a freshly
    /// stopped container is listed as <c>created</c>, not <c>deleted</c>, so it starts rather
    /// than gets recreated. Always detached: this is a lifecycle operation, not a new
    /// foreground attach.
    /// </summary>
    internal int Restart()
    {
        if (Config.IsCompose)
        {
            var stopCode = Execute(Bridge.Stop(), exitOnFailure: false);
            if (stopCode != 0)
            {
                return stopCode;
            }

            return Execute(Bridge.Up(detach: true), interactive: false);
        }

        // A failed stop is not swallowed like the sidecar stops below are: if the primary
        // container is still running afterwards, EnsureContainer sees AlreadyRunning and does
        // nothing, so returning 0 unconditionally here would report success without the
        // container having actually restarted.
        var primaryStopCode = Execute(Builder.Stop(), exitOnFailure: false);
        if (primaryStopCode != 0)
        {
            return primaryStopCode;
        }

        foreach (var name in SidecarNames())
        {
            Execute(Builder.DependencyStop(name), exitOnFailure: false);
        }

        foreach (var name in SidecarNames())
        {
            EnsureDependency(name);
        }

        EnsureContainer(detach: true);
        return 0;
    }

    internal int Ps()
    {
        if (Config.IsCompose)
        {
            return Execute(Bridge.Ps(), interactive: true);
        }

        PrintStatusLine(
            Config.Container ?? throw new ConfigException("container: must be set in wip.yml"),
            Builder.Find());
        foreach (var name in SidecarNames())
        {
            PrintStatusLine(name, Builder.DependencyFind(name));
        }

        return 0;
    }

    internal int Exec(IReadOnlyList<string> command, bool interactive) =>
        Execute(ExecTarget(command, interactive), interactive: Tty(interactive));

    internal int Run(IReadOnlyList<string> command, bool interactive)
    {
        WarnWslProject();

        if (Config.IsCompose)
        {
            Console.Error.WriteLine(
                "wip: compose mode has no ephemeral 'run'; executing in the running " +
                $"'{Config.ComposeService}' service instead");
            return Execute(ExecTarget(command, interactive), interactive: Tty(interactive));
        }

        return Execute(Builder.Run(command, interactive: interactive), interactive: Tty(interactive));
    }

    internal int Shell()
    {
        if (Config.Command("shell") is not null)
        {
            return Dispatch("shell", []);
        }

        var code = Execute(ExecTarget(["bash"], true), interactive: Tty(true), exitOnFailure: false);
        return code == 0 ? 0 : Execute(ExecTarget(["sh"], true), interactive: Tty(true));
    }

    internal int Logs(IReadOnlyList<string> services, bool follow)
    {
        if (Config.IsCompose)
        {
            return Execute(Bridge.Logs(services, follow), interactive: true);
        }

        // wslc has no compose-style multi-service log aggregation, so exactly one SERVICE is
        // allowed, defaulting to the configured container (compose.service, under
        // mode: compose-native) when none is given.
        if (services.Count > 1)
        {
            throw new ConfigException(
                "`wip logs` outside mode: compose takes at most one SERVICE " +
                "(wslc, unlike a real compose tool, only follows one container at a time)");
        }

        var name = services.Count == 0
            ? Config.Container ?? throw new ConfigException("container: must be set in wip.yml")
            : services[0];

        return Execute(Builder.Logs(name, follow), interactive: true);
    }

    internal int Dispatch(string name, IReadOnlyList<string> arguments)
    {
        var values = Config.Command(name) ?? throw new ConfigException($"Unknown command: {name}");
        if (Config.IsCompose)
        {
            return DispatchCompose(name, values, arguments);
        }

        var interactive = RubyValue.IsTruthy(values.GetValueOrDefault("interactive"));
        return Execute(Builder.Custom(name, arguments), interactive: Tty(interactive));
    }

    // ---------------------------------------------------------------- internals

    private int DispatchCompose(string name, OrderedDictionary<string, object?> values, IReadOnlyList<string> arguments)
    {
        var type = RubyValue.Presence(values.GetValueOrDefault("type")) ?? "exec";
        if (type != "exec")
        {
            throw new ConfigException(
                $"commands.{name}: type '{type}' is not supported in compose mode " +
                "(use `wslc-compose build`/`up --build` directly)");
        }

        var command = Shellwords.Split(RubyValue.ToStringValue(values.GetValueOrDefault("command"))).ToList();
        command.AddRange(arguments);
        var interactive = RubyValue.IsTruthy(values.GetValueOrDefault("interactive"));
        return Execute(ExecTarget(command, interactive), interactive: Tty(interactive));
    }

    private IReadOnlyList<string> ExecTarget(IReadOnlyList<string> arguments, bool interactive) =>
        Config.IsCompose
            ? Bridge.Exec(Config.ComposeService!, arguments, interactive)
            : Builder.Exec(arguments, interactive: interactive);

    private int Execute(
        IReadOnlyList<string> command,
        bool interactive = false,
        bool exitOnFailure = true,
        string? workingDirectory = null)
    {
        var runner = new CommandRunner(Interpreter, debug: Debug);
        var code = Reporter.Step(
            $"running: {CommandDisplay.ForDebug(command)}",
            () => runner.Run(command, interactive: interactive, workingDirectory: workingDirectory),
            live: !interactive);

        if (exitOnFailure && code != 0)
        {
            throw new ExitException(code);
        }

        return code;
    }

    private (int Code, string Output) Probe(IReadOnlyList<string> command, TimeSpan? timeout = null)
    {
        var output = new StringWriter();
        var runner = new CommandRunner(Interpreter, output, new StringWriter(), Debug);
        var code = Reporter.Step(
            $"checking: {CommandDisplay.ForDebug(command)}",
            () => runner.Run(command, timeout: timeout));

        return (code, output.ToString());
    }

    /// <summary>
    /// Parses wslc's <c>--format json</c> output, or returns null when it is not a JSON array.
    /// </summary>
    /// <remarks>
    /// Catching JsonException alone is not enough: <c>{}</c> and <c>null</c> parse fine, and
    /// it is the array operation that then throws InvalidOperationException. A probe exists
    /// to answer a question, so anything unrecognisable is "no" rather than a crash.
    /// </remarks>
    private static JsonDocument? ParseArray(string output)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            return document;
        }

        document.Dispose();
        return null;
    }

    private void EnsureNetwork()
    {
        if (Config.Network is not { } network)
        {
            return;
        }

        if (NetworkExists(network))
        {
            return;
        }

        Console.Error.WriteLine($"wip: creating network '{network}'");
        Execute(Builder.NetworkCreate(), exitOnFailure: false);
    }

    private bool NetworkExists(string network)
    {
        var (code, output) = Probe(Builder.NetworkList());
        if (code != 0)
        {
            return false;
        }

        using var document = ParseArray(output);
        return document is not null && document.RootElement.EnumerateArray().Any(entry =>
            entry.ValueKind == JsonValueKind.Object &&
            entry.TryGetProperty("Name", out var name) &&
            name.ValueKind == JsonValueKind.String &&
            name.GetString() == network);
    }

    /// <summary>
    /// dependencies: holds every container uniformly, including the primary one
    /// <c>container:</c> points at. That one gets its own handling in
    /// <see cref="EnsureContainer"/>, so it is excluded here to avoid double-starting it.
    /// </summary>
    private IReadOnlyList<string> SidecarNames() =>
        Config.Dependencies.Keys.Where(name => name != Config.Container).ToList();

    private void EnsureDependency(string name)
    {
        var (exists, state) = ContainerEntry(Builder.DependencyFind(name), name);
        switch (DecideContainerAction(exists, state))
        {
            case ContainerAction.AlreadyRunning:
                Console.Error.WriteLine($"wip: dependency '{name}' is already running");
                break;

            case ContainerAction.Start:
                Console.Error.WriteLine($"wip: starting existing dependency '{name}'");
                Execute(Builder.DependencyStart(name));
                break;

            default:
                Console.Error.WriteLine($"wip: dependency '{name}' not found, creating it");
                Execute(Builder.DependencyUp(name));
                break;
        }

        WaitForHealthy(name);
    }

    /// <summary>
    /// Polls <c>dependencies.&lt;name&gt;.healthcheck</c> — set directly under
    /// <c>mode: container</c>, or read from compose.yml's own <c>healthcheck:</c> under
    /// <c>mode: compose-native</c> — the way <c>docker compose up</c> blocks a
    /// <c>depends_on: condition: service_healthy</c> dependent. A no-op when the dependency
    /// declares no healthcheck at all, which keeps every project unaffected until it opts in.
    /// </summary>
    /// <remarks>
    /// Waits on any healthcheck it finds, regardless of which depends_on condition (if any)
    /// named it: <see cref="SidecarNames"/> already starts sidecars before the primary
    /// container, and compose-native's dependency order already starts a dependency before
    /// anything depends_on says depends on it, so there is no separate per-edge condition to
    /// gate on selectively here.
    /// </remarks>
    private void WaitForHealthy(string name)
    {
        if (RubyValue.AsMapping(Config.Dependency(name)?.GetValueOrDefault("healthcheck")) is not { } healthcheck)
        {
            return;
        }

        var test = RubyValue.AsArray(healthcheck.GetValueOrDefault("test")).Select(RubyValue.ToStringValue).ToList();
        var interval = Convert.ToDouble(healthcheck.GetValueOrDefault("interval"), CultureInfo.InvariantCulture);
        var timeout = Convert.ToDouble(healthcheck.GetValueOrDefault("timeout"), CultureInfo.InvariantCulture);
        var retries = Convert.ToInt32(healthcheck.GetValueOrDefault("retries"), CultureInfo.InvariantCulture);
        var startPeriod = Convert.ToDouble(healthcheck.GetValueOrDefault("start_period"), CultureInfo.InvariantCulture);

        Console.Error.WriteLine($"wip: waiting for dependency '{name}' to become healthy");
        var startPeriodEnds = DateTime.UtcNow.AddSeconds(startPeriod);
        var failures = 0;

        while (true)
        {
            var (code, checkOutput) = Probe(Builder.DependencyExec(name, test), TimeSpan.FromSeconds(timeout));
            if (code == 0)
            {
                Console.Error.WriteLine($"wip: dependency '{name}' is healthy");
                return;
            }

            // Failures during start_period don't count against retries — the same grace real
            // Compose gives a slow-booting service before it starts judging health at all.
            if (DateTime.UtcNow >= startPeriodEnds && ++failures > retries)
            {
                throw new WipException(HealthCheckFailureMessage(name, failures, code, checkOutput));
            }

            Thread.Sleep(TimeSpan.FromSeconds(interval));
        }
    }

    internal static string HealthCheckFailureMessage(string name, int failures, int code, string output)
    {
        var reason = code == CommandRunner.TimeoutExitCode ? "timed out" : $"exited {code}";
        var detail = LastLine(output) is { } line ? $": {line}" : "";
        return $"dependency '{name}' did not become healthy after {failures} attempt(s) (last check {reason}){detail}";
    }

    internal static string? LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    private void EnsureContainer(bool detach)
    {
        var interactive = Tty(!detach);
        // Builder.Find() rather than the raw name: it goes through the builder's required-value
        // check, so a config with no container: still fails the same way it always did.
        var (exists, state) = ContainerEntry(Builder.Find(), Config.Container ?? "");
        switch (DecideContainerAction(exists, state))
        {
            case ContainerAction.AlreadyRunning:
                Console.Error.WriteLine($"wip: container '{Config.Container}' is already running");
                break;

            case ContainerAction.Start:
                Console.Error.WriteLine($"wip: starting existing container '{Config.Container}'");
                Execute(Builder.Start(detach), interactive);
                break;

            default:
                Console.Error.WriteLine($"wip: container '{Config.Container}' not found, creating it");
                Execute(Builder.Up(detach), interactive);
                break;
        }
    }

    private int UpViaComposeBridge(bool detach, bool sync, bool watch)
    {
        if (watch)
        {
            throw new ConfigException(
                "`wip up --watch` is not supported under mode: compose (wip never parses a " +
                "compose.yml service list in that mode, so there is nothing to poll)");
        }

        if (sync)
        {
            SyncBeforeBoot();
        }

        return Execute(Bridge.Up(detach), interactive: Tty(!detach));
    }

    private SyncSettings RequiredSync() =>
        Config.Sync ?? throw new ConfigException("`wip sync` needs a sync: block in wip.yml");

    private int RunSync(bool exitOnFailure = true)
    {
        var settings = RequiredSync();
        return Execute(settings.IsExec ? Builder.SyncExec() : Builder.SyncRun(), exitOnFailure: exitOnFailure);
    }

    private void SyncBeforeBoot()
    {
        if (Config.Sync is not { } settings)
        {
            return;
        }

        EnsureSyncImage(settings);
        Console.Error.WriteLine($"wip: syncing {settings.Source} -> {settings.Volume}:{settings.Target}");
        Execute(Builder.SyncRun());
        Console.Error.WriteLine(
            $"wip: run `wip sync --watch` in another terminal to keep {settings.Target} up to date");
    }

    /// <summary>
    /// Builds sync.build's image once per invocation rather than once per --watch tick, which
    /// would pay build-cache-lookup overhead on every mirror for no reason. A no-op unless
    /// sync.build is configured.
    /// </summary>
    private void EnsureSyncImage(SyncSettings settings)
    {
        if (settings.Build is null)
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("wip-sync-build-");
        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "Dockerfile"),
                RubyValue.ToStringValue(settings.Build["dockerfile"]));

            // wslc build crashes when handed an absolute context path; running from inside
            // the context and passing "." avoids it.
            Execute(Builder.SyncBuild("."), workingDirectory: directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Builds every compose-native service with a <c>build:</c> once per <c>wip up</c>. A
    /// no-op for mode: container and mode: compose, and for build-less services.
    /// </summary>
    private void EnsureComposeImages(bool noCache)
    {
        foreach (var (name, spec) in Config.ComposeBuildSpecs)
        {
            BuildComposeImage(name, RubyValue.AsMapping(spec)!, noCache);
        }
    }

    private void BuildComposeImage(string name, OrderedDictionary<string, object?> spec, bool noCache)
    {
        var extra = new List<string>();
        if (RubyValue.Presence(spec.GetValueOrDefault("dockerfile")) is { } dockerfile)
        {
            extra.Add("-f");
            extra.Add(dockerfile);
        }

        if (RubyValue.AsMapping(spec.GetValueOrDefault("args")) is { } args)
        {
            foreach (var (key, value) in args)
            {
                extra.Add("--build-arg");
                extra.Add($"{key}={RubyValue.ToStringValue(value)}");
            }
        }

        var settings = RubyValue.NewMapping();
        settings["tag"] = spec.GetValueOrDefault("tag");

        var context = RubyValue.ToStringValue(spec.GetValueOrDefault("context"));
        Console.Error.WriteLine(
            $"wip: building service '{name}' (tag: {RubyValue.ToStringValue(spec.GetValueOrDefault("tag"))}) " +
            $"from {context}");

        StageAndBuild(context, settings, WithNoCache(extra, noCache));
    }

    /// <summary>
    /// A project on the WSL filesystem is a bind mount wslc cannot serve: it resolves a
    /// <c>-v</c> source as a Windows path and mounts an empty directory rather than failing
    /// when that path does not exist. <c>sync.source</c> refuses to resolve at all, but
    /// <c>volumes:</c> entries reach wslc exactly as they were written — <c>.:/app</c>
    /// resolved against wip's own UNC working directory — so this warning is the only thing
    /// standing between such a project and a container that comes up empty in silence.
    /// <c>WIP_WSL_PATH</c> deliberately does not silence it: that variable only changes what
    /// <c>sync.source</c> resolves to, and rewrites no <c>volumes:</c> entry.
    /// </summary>
    private void WarnWslProject()
    {
        if (Config.Path is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(Config.Path));
        if (directory is null || !WslPath.IsWslPath(directory))
        {
            return;
        }

        Console.Error.WriteLine(
            $"wip: warning: this project is on the WSL filesystem ({directory}); wslc " +
            "resolves bind-mount sources as Windows paths, so volumes: entries can mount " +
            "empty instead of failing. Move the project onto the Windows filesystem " +
            "(C:\\src\\myproject, say) if a container starts up without your files.");
    }

    private int StageAndBuild(string context, OrderedDictionary<string, object?> settings, IReadOnlyList<string> extra)
    {
        using var progress = new StagingProgress();
        var buildContext = new BuildContext(context);
        var code = 0;

        buildContext.Stage(
            staged =>
            {
                progress.Finish();
                if (buildContext.UsesCache)
                {
                    Console.Error.WriteLine($"wip: using cached build context at {staged}");
                }

                // wslc build crashes when handed an absolute context path; running from
                // inside the staged directory and passing "." avoids it.
                var withLocalContext = RubyValue.Merge(settings, null);
                withLocalContext["context"] = ".";
                code = Execute(Builder.Build(withLocalContext, extra), interactive: Tty(true),
                    workingDirectory: staged);
            },
            progress.Tick);

        return code;
    }

    private static IReadOnlyList<string> StripSeparator(IReadOnlyList<string> extra) =>
        extra.Count > 0 && extra[0] == "--" ? extra.Skip(1).ToList() : extra;

    private static IReadOnlyList<string> WithNoCache(IReadOnlyList<string> extra, bool noCache)
    {
        if (!noCache || extra.Contains("--no-cache"))
        {
            return extra;
        }

        var result = new List<string> { "--no-cache" };
        result.AddRange(extra);
        return result;
    }

    /// <summary>
    /// A built-in command wins over a <c>commands:</c> entry of the same name, so point at
    /// <c>wip dispatch</c> rather than letting the custom one vanish silently.
    /// </summary>
    private void WarnShadowedCommand(string name)
    {
        if (!Config.Commands.ContainsKey(name))
        {
            return;
        }

        Console.Error.WriteLine(
            $"wip: commands.{name} in wip.yml is shadowed by the built-in `wip {name}`; " +
            $"run it with `wip dispatch {name}`");
    }

    /// <summary>
    /// Approximates Compose's <c>restart:</c> policy with a foreground poll loop, not a
    /// background service — the same opt-in, keep-a-terminal-open shape as
    /// <c>wip sync --watch</c>.
    /// </summary>
    private void WatchRestarts(double interval)
    {
        var names = Config.Dependencies.Keys.ToList();
        var joined = string.Join(", ", names);
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"wip: watching {joined} for exited restart: containers every {interval}s (running detached; Ctrl-C to stop)"));

        Loop(interval, () =>
        {
            foreach (var name in names)
            {
                RestartIfExited(name);
            }

            return 0;
        }, "watch stopped");
    }

    /// <summary>
    /// Status-based, not transition-based: each tick checks the current state, not whether it
    /// <em>just</em> exited. It cannot distinguish "crashed on its own" from "you ran
    /// `wip stop` in another terminal" — stop this loop first if you are about to do either.
    /// </summary>
    private void RestartIfExited(string name)
    {
        var policy = RubyValue.ToStringValue(Config.Dependency(name)?.GetValueOrDefault("restart"));
        if (!AutoRestart(policy) || ContainerStatus(name) != WslcContainerStateExited)
        {
            return;
        }

        Console.Error.WriteLine($"wip: '{name}' has exited, restarting it (restart: {policy})");
        Execute(Builder.DependencyStart(name), exitOnFailure: false);
    }

    private static bool AutoRestart(string policy) =>
        AutoRestartPolicies.Contains(policy) || OnFailurePolicy().IsMatch(policy);

    /// <summary>
    /// Isolated to one method: if a future wslc release changes this shape, fixing it here is
    /// a one-line change. The raw entry is logged under --debug so that is immediately
    /// visible instead of silently doing nothing forever.
    /// </summary>
    private int? ContainerStatus(string name) => ContainerEntry(Builder.DependencyFind(name), name).State;

    /// <summary>
    /// Prints one <c>wip ps</c>/<c>wip status</c> line. Image and ports come from the
    /// configured <c>wip.yml</c> values rather than the wslc listing: only <c>Name</c> and
    /// <c>State</c> are confirmed fields of that JSON anywhere else in this codebase.
    /// </summary>
    private void PrintStatusLine(string name, IReadOnlyList<string> findCommand)
    {
        var (exists, state) = ContainerEntry(findCommand, name);
        var dependency = Config.Dependency(name);
        var image = RubyValue.Presence(dependency?.GetValueOrDefault("image")) ?? "-";
        var ports = RubyValue.AsArray(dependency?.GetValueOrDefault("ports"))
            .Select(RubyValue.ToStringValue).ToList();

        Console.WriteLine(string.Join('\t',
            name, StatusLabel(exists, state), image, ports.Count > 0 ? string.Join(", ", ports) : "-"));
    }

    internal static string StatusLabel(bool exists, int? state) => (exists, state) switch
    {
        (false, _) => "not found",
        (true, WslcContainerStateCreated) => "created",
        (true, WslcContainerStateRunning) => "running",
        (true, WslcContainerStateExited) => "exited",
        (true, WslcContainerStateDeleted) => "deleted",
        _ => "unknown",
    };

    /// <summary>
    /// One probe answering both questions, because they come from the same listing. Splitting
    /// them across two calls would run <c>wslc list</c> twice and leave room for the answers
    /// to disagree, which is exactly the gap this is here to close.
    /// </summary>
    /// <returns>
    /// Whether the container is listed at all, and the state it reports. A null state on a
    /// listed container means wslc reported it in a shape wip does not understand.
    /// </returns>
    private (bool Exists, int? State) ContainerEntry(IReadOnlyList<string> findCommand, string name)
    {
        var (code, output) = Probe(findCommand);
        if (code != 0)
        {
            return (false, null);
        }

        using var document = ParseArray(output);
        if (document is null)
        {
            return (false, null);
        }

        var first = document.RootElement.EnumerateArray().FirstOrDefault();
        if (Debug)
        {
            Console.Error.WriteLine($"wip: [debug] '{name}': {first}");
        }

        if (first.ValueKind != JsonValueKind.Object)
        {
            return (false, null);
        }

        // TryGetInt32 rather than GetInt32: a future wslc could report State as a string, and
        // a watch loop should keep polling instead of taking the whole command down.
        return first.TryGetProperty("State", out var state) && state.TryGetInt32(out var value)
            ? (true, value)
            : (true, null);
    }

    /// <summary>Runs <paramref name="body"/> every <paramref name="interval"/> seconds until Ctrl-C.</summary>
    private static int Loop(double interval, Func<int> body, string stoppedMessage)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += handler;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                body();
                cancellation.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(interval));
            }
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"wip: {stoppedMessage}");
        return 0;
    }

    [GeneratedRegex(@"\Aon-failure(?::\d+)?\z")]
    private static partial Regex OnFailurePolicy();
}

/// <summary>The options every command shares, parsed once by <see cref="Program"/>.</summary>
internal sealed record CliOptions(string? ConfigPath, string? EnvFile, bool Debug, string? DebugLog);
