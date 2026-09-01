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

    internal bool Quiet => options.Quiet;

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

    internal int Init(
        bool force,
        string? template,
        bool ai = false,
        string? url = null,
        bool allowRemoteAi = false)
    {
        var path = Path.GetFullPath(options.ConfigPath ?? ConfigLoader.Filename);
        if (ai)
        {
            if (template is not null)
            {
                throw new WipException("--template cannot be combined with --ai");
            }

            return InitWithAi(path, url, allowRemoteAi);
        }

        if (url is not null)
        {
            throw new WipException("--url requires --ai");
        }

        if (allowRemoteAi)
        {
            throw new WipException("--allow-remote-ai requires --ai");
        }

        if (File.Exists(path) && !force)
        {
            throw new WipException($"{path} already exists (use --force to overwrite)");
        }

        var initializer = new Initializer(Path.GetDirectoryName(path), template);
        File.WriteAllText(path, initializer.Call());
        Log.Info($"wrote {path} (mode: {(initializer.IsCompose ? "compose-native" : "container")})");
        return 0;
    }

    private static int InitWithAi(string path, string? url, bool allowRemoteAi)
    {
        var baseUrl = LocalAiProvider.ResolveBaseUrl(url);
        var endpoint = LocalAiProvider.ValidateBaseUrl(baseUrl, allowRemoteAi);
        RequireRemoteApproval(endpoint, allowRemoteAi);

        // Everything that decides what leaves this machine is settled — and disclosed — before
        // the first packet: the availability probe and model discovery below already talk to the
        // endpoint, so announcing afterwards would tell the user where their data went, not
        // where it is about to go.
        var directory = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        Log.Info($"analyzing {directory}");
        var project = new ProjectAnalyzer(directory).Analyze();
        var name = Path.GetFileName(path);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;

        // WipAiGenerator embeds the existing wip.yml in the prompt on its own, outside the
        // analyzer's snapshot, so it needs the same screening the snapshot's files get — and the
        // same disclosure, since it is sent just as literally as they are.
        var existingToSend = existing is not null && !ProjectAnalyzer.ContainsPossibleSecret(existing)
            ? existing
            : null;
        if (existing is not null && existingToSend is null)
        {
            Log.Warn($"{name} looks like it contains a secret — generating without sending it");
        }

        var sending = project.Files.Select(file => file.RelativePath).ToList();
        if (existingToSend is not null)
        {
            sending.Add(name);
        }

        AnnounceDestination(endpoint, sending);
        if (!LocalAiProvider.IsAvailable(baseUrl, allowRemoteAi))
        {
            throw new WipException(
                $"{LocalAiProvider.NotFoundMessage(baseUrl)} Run `wip doctor` to check again.");
        }

        var model = LocalAiProvider.ResolveModel() ?? LocalAiProvider.DiscoverModel(
            baseUrl, allowInsecureRemoteHttp: allowRemoteAi);

        Console.Error.WriteLine(
            "Describe the development environment wip should run, then press Enter twice " +
            "(once after your text, once more on a blank line) to finish:");
        var lines = new List<string>();
        string? line;
        while ((line = Console.ReadLine()) is not null && line.Length > 0)
        {
            lines.Add(line);
        }

        Log.Info($"sending {sending.Count} selected project files to {model} at {baseUrl}");
        var candidate = new WipAiGenerator(
            new LocalAiProvider(baseUrl, model, allowInsecureRemoteHttp: allowRemoteAi)).Generate(
            string.Join(System.Environment.NewLine, lines), project, existingToSend, path);

        Console.WriteLine(candidate);
        Console.Error.Write(existing is null
            ? "Save this wip.yml? [y/N] "
            : "Replace the existing wip.yml with this update? [y/N] ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("not saved");
            return 0;
        }

        File.WriteAllText(path, candidate);
        Log.Info($"wrote {path}");
        return 0;
    }

    internal int HelpAi(
        string? url, IReadOnlyList<string> question, bool allowRemoteAi = false, bool noCache = false)
    {
        var baseUrl = LocalAiProvider.ResolveBaseUrl(url);
        var endpoint = LocalAiProvider.ValidateBaseUrl(baseUrl, allowRemoteAi);
        RequireRemoteApproval(endpoint, allowRemoteAi);

        // `wip help --ai` sends the question and wip's own help text, never project files — but
        // the destination is still announced before the probe below opens a connection to it.
        AnnounceDestination(endpoint, []);
        if (!LocalAiProvider.IsAvailable(baseUrl, allowRemoteAi))
        {
            throw new WipException(
                $"{LocalAiProvider.NotFoundMessage(baseUrl)} Run `wip doctor` to check again.");
        }

        var model = LocalAiProvider.ResolveModel() ?? LocalAiProvider.DiscoverModel(
            baseUrl, allowInsecureRemoteHttp: allowRemoteAi);
        var asked = question.Count > 0 ? string.Join(' ', question) : ReadQuestion();
        if (string.IsNullOrWhiteSpace(asked))
        {
            throw new WipException("A question is required");
        }

        var (manual, manualSource) = ResolveManual(asked, noCache);
        Log.Info($"manual: {manualSource}");
        Log.Info($"asking {model} at {baseUrl}");
        var answer = new LocalAiProvider(baseUrl, model, allowInsecureRemoteHttp: allowRemoteAi)
            .Generate(HelpAiPrompt(asked, manual));
        Console.WriteLine(answer);
        return 0;
    }

    /// <summary>Names the host and transport the prompt is about to cross, and every file that
    /// travels with it, so the user can stop an unintended destination before it is contacted.</summary>
    private static void AnnounceDestination(Uri endpoint, IReadOnlyList<string> files)
    {
        Console.Error.WriteLine($"AI destination: {endpoint.Host} ({endpoint.Scheme.ToUpperInvariant()})");
        if (files.Count == 0)
        {
            Console.Error.WriteLine("Files to send: none");
            return;
        }

        Console.Error.WriteLine("Files to send:");
        foreach (var file in files)
        {
            Console.Error.WriteLine($"  - {file}");
        }
    }

    private static void RequireRemoteApproval(Uri endpoint, bool allowRemoteAi)
    {
        if (!endpoint.IsLoopback && !allowRemoteAi)
        {
            throw new WipException(
                $"Refusing to send data to remote AI host '{endpoint.Host}' without --allow-remote-ai");
        }
    }

    /// <summary>
    /// Selects the wiki manual page(s) relevant to <paramref name="question"/> (see <see
    /// cref="ManualSelector"/>), preferring an already-downloaded cache (<c>wip manual</c>) over
    /// a live wiki fetch, and falling back to no manual context at all when neither is
    /// available. A live-fetch failure is swallowed rather than thrown: <c>help --ai</c> worked
    /// without the manual before this existed, and a flaky network is not a reason to break it
    /// now. <paramref name="noCache"/> (<c>help --ai --no-cache</c>) skips a downloaded cache
    /// even when one exists, for a question about a page that changed on the wiki since the
    /// last <c>wip manual</c> run.
    /// </summary>
    private static (string Excerpt, string Source) ResolveManual(string question, bool noCache = false)
    {
        IReadOnlyList<ManualPage> cached = noCache ? [] : WikiManual.LoadCache(WikiManual.DefaultCacheDirectory());
        if (cached.Count > 0)
        {
            return (Join(ManualSelector.SelectRelevant(question, cached)), "cached");
        }

        if (!WikiManual.IsReachable())
        {
            return ("", "unavailable");
        }

        try
        {
            var wiki = new WikiManual();
            var candidates = ManualSelector.SelectCandidateNames(question, wiki.FetchPageNames());
            var pages = candidates.Count > 0 ? wiki.FetchPages(candidates) : [];
            return (Join(ManualSelector.SelectRelevant(question, pages)), "live");
        }
        catch (WipException)
        {
            return ("", "live (fetch failed)");
        }
    }

    private static string Join(IReadOnlyList<ManualPage> pages) =>
        string.Join("\n\n", pages.Select(page => $"### {page.Name}\n{page.Content}"));

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

    private static string HelpAiPrompt(string question, string manual) => $$"""
        You answer questions about how to use the wip CLI, a developer-friendly wrapper around
        Microsoft WSLC. Answer only from the reference below; say plainly that it is not covered
        there rather than guessing.

        <wip-help>
        {{Program.HelpText()}}
        </wip-help>
        {{(string.IsNullOrEmpty(manual) ? "" : $"\n<wip-manual>\n{manual}\n</wip-manual>\n")}}
        Question:
        {{question}}
        """;

    /// <summary>Downloads the whole wiki manual to <see
    /// cref="WikiManual.DefaultCacheDirectory"/> so <c>wip help --ai</c> can select from it
    /// offline instead of needing a live fetch per question.</summary>
    internal int ManualDownload()
    {
        if (!WikiManual.IsReachable())
        {
            throw new WipException("Could not reach the wip wiki (github.com). Check your network connection.");
        }

        var directory = WikiManual.DefaultCacheDirectory();
        var count = new WikiManual().Download(directory);
        Log.Info($"downloaded {count} manual page(s) to {directory}");
        return 0;
    }

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

        Log.Info($"staging build context ({context})");
        return StageAndBuild(context, settings, arguments);
    }

    /// <summary>
    /// Gives <c>wip up</c> an explicit final outcome line (issue #134) instead of leaving
    /// success or failure to be inferred from the tail of a build/boot log. The success line
    /// only fires for a detached or --watch run: an attached run instead ends when the primary
    /// container exits, and "up complete" printed after that would describe the wrong event.
    /// </summary>
    internal int Up(bool detach, bool sync, bool noCache, bool watch, double interval)
    {
        WarnWslProject();

        try
        {
            if (Config.IsCompose)
            {
                var code = UpViaComposeBridge(detach, sync, watch);
                if (detach)
                {
                    // No container count here, unlike the non-compose branch below: under
                    // mode: compose, wip delegates entirely to wslc-compose and deliberately
                    // never parses compose.yml's service list (see the ConfigException in
                    // UpViaComposeBridge for --watch), so it has nothing accurate to count.
                    Log.Info("up complete");
                }

                return code;
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

            if (detach || watch)
            {
                var count = SidecarNames().Count + 1;
                Log.Info($"up complete ({count} container(s) running)");
            }

            if (watch)
            {
                WatchRestarts(interval);
            }

            return 0;
        }
        catch (ExitException exit)
        {
            // The failure itself was already streamed by CommandRunner (raw child output,
            // plus ErrorInterpreter's hint on top); this is only the missing "it's over, and
            // it did not work" marker the issue asked for, not a second explanation of why.
            Log.Error($"up failed (exit code {exit.Code})");
            NoteIfDisplayLanguageIsNotEnglish();
            throw;
        }
    }

    /// <summary>
    /// The output just streamed above came straight from wslc/docker/rsync, unlike everything
    /// else wip printed around it — and unlike wip's own strings, it is not guaranteed to be in
    /// English if Windows' display language isn't (issue #134's "Windows / locale angle"; see
    /// <see cref="DisplayLanguage"/>). A quick note here is cheaper than the reader wondering
    /// whether a paragraph in another language part-way up the scrollback was a wip message
    /// they somehow can't parse.
    /// </summary>
    private static void NoteIfDisplayLanguageIsNotEnglish()
    {
        if (DisplayLanguage.IsEnglish())
        {
            return;
        }

        Log.Info(
            "the output above came from the command that failed, not from wip, and may not " +
            "be in English (your Windows display language isn't)");
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

        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"syncing {settings.Source} -> {settings.Volume}:{settings.Target} every {seconds}s (Ctrl-C to stop)"));

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

    /// <summary>
    /// <paramref name="terminateSession"/> is opt-in and never implied: <c>wslc system session
    /// terminate</c> resets the whole WSLC session, not anything scoped to this
    /// <c>wip.yml</c> — running it by default would silently take down unrelated containers
    /// from any other project currently using the same session. It runs regardless of which
    /// branch below removed this project's own containers.
    /// </summary>
    internal int Down(bool terminateSession = false)
    {
        var code = Config.IsCompose ? DownCompose() : DownContainer();

        if (terminateSession)
        {
            TerminateSession();
        }

        return code;
    }

    private int DownCompose() => Execute(Bridge.Down(), exitOnFailure: false);

    private int DownContainer()
    {
        Execute(Builder.Remove(), exitOnFailure: false);
        foreach (var name in SidecarNames())
        {
            Execute(Builder.DependencyRemove(name), exitOnFailure: false);
        }

        return 0;
    }

    /// <summary>
    /// Best-effort, like the removes above it: a session that was already idle is not a reason
    /// to fail <c>wip down</c> outright.
    /// </summary>
    private void TerminateSession()
    {
        Console.Error.WriteLine("wip: terminating WSLC session (--terminate-session)");
        Execute(Builder.SystemSessionTerminate(), exitOnFailure: false);
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
            Log.Info(
                "compose mode has no ephemeral 'run'; executing in the running " +
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
        var runner = new CommandRunner(Interpreter, debug: Debug, quiet: Quiet);
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

    /// <summary>
    /// <paramref name="captureStderr"/> defaults to false because <see cref="ContainerEntry"/>
    /// and <see cref="NetworkExists"/> parse this output as JSON — merging in stderr would risk
    /// breaking that parse the moment wslc ever writes a warning there. A readiness check has
    /// no such expectation and wants exactly the opposite: stderr is usually where the useful
    /// diagnostic is, so <see cref="WaitForHealthy"/> opts in.
    /// </summary>
    private (int Code, string Output) Probe(
        IReadOnlyList<string> command,
        TimeSpan? timeout = null,
        bool captureStderr = false)
    {
        var captured = new StringWriter();
        var output = captureStderr ? TextWriter.Synchronized(captured) : captured;
        var error = captureStderr ? output : new StringWriter();
        var runner = new CommandRunner(Interpreter, output, error, Debug);
        var code = Reporter.Step(
            $"checking: {CommandDisplay.ForDebug(command)}",
            () => runner.Run(command, timeout: timeout));

        return (code, captured.ToString());
    }

    /// <summary>
    /// Reads the records out of wslc's <c>--format json</c> output, whatever shape it arrives
    /// in: a JSON array, a single object, or one object per line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to accept an array and nothing else, which was wrong about the tool it was
    /// reading: <c>wslc list --all --filter name=x --format json</c> prints the record on its
    /// own, unwrapped —
    /// <c>{"CreatedAt":…,"Id":"…","Image":"…","Name":"x","Ports":[],"State":2,…}</c>. Every
    /// probe built on it therefore concluded "no such container", so <c>wip ps</c> reported a
    /// running container as <c>not found</c> and <c>wip up</c> recreated one that was already
    /// there. The end-to-end job against real WSLC is what surfaced it; nothing below this
    /// line had ever seen wslc's actual output.
    /// </para>
    /// <para>
    /// All three shapes are accepted rather than just the one measured, because the shape is
    /// wslc's to change and this is a probe: reading a record that is there matters, and
    /// which envelope it came in does not. Anything unparseable stays "no records" rather
    /// than an exception — a probe exists to answer a question, not to take the command down.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<JsonElement> ParseRecords(string output)
    {
        // The whole output first, which covers an array and a lone object in one step.
        if (TryReadRecords(output, out var records))
        {
            return records;
        }

        // Several values in sequence are trailing data to JsonDocument, so line-delimited
        // output only parses a line at a time.
        var lines = new List<JsonElement>();
        foreach (var line in output.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line) && TryReadRecords(line, out var parsed))
            {
                lines.AddRange(parsed);
            }
        }

        return lines;
    }

    /// <summary>
    /// Elements are cloned because they outlive the document they were read from: the caller
    /// gets a list it can keep, not a view into a disposed parser.
    /// </summary>
    private static bool TryReadRecords(string text, out IReadOnlyList<JsonElement> records)
    {
        records = new List<JsonElement>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var parsed = new List<JsonElement>();

            // Objects only: a record is what the callers read properties off, and
            // JsonElement.TryGetProperty throws rather than returning false when handed
            // anything else, so a stray scalar would take the probe down. Valid JSON that
            // carries no records at all (`null`, a bare number) leaves the list empty, which
            // still counts as parsed and stops the line-by-line retry re-reading it.
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object)
                    {
                        parsed.Add(entry.Clone());
                    }
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                parsed.Add(document.RootElement.Clone());
            }

            records = parsed;
        }

        return true;
    }

    /// <summary>
    /// Picks <paramref name="name"/>'s record out of a listing and reports whether it is
    /// there and what state it is in.
    /// </summary>
    /// <remarks>
    /// The name is matched rather than the first record taken: <c>--filter name=</c> narrows
    /// the listing but does not promise a single exact hit, so <c>app</c> could otherwise be
    /// answered with <c>app-worker</c>'s state. A listing whose records carry no <c>Name</c>
    /// at all falls back to the first record, which keeps a future rename of that field from
    /// turning every container into "not found".
    /// </remarks>
    internal static (bool Exists, int? State) ReadContainerEntry(string output, string name)
    {
        var records = ParseRecords(output);
        if (records.Count == 0)
        {
            return (false, null);
        }

        var named = records.Where(record => record.TryGetProperty("Name", out _)).ToList();
        var match = named.Count > 0
            ? named.FirstOrDefault(record =>
                record.TryGetProperty("Name", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString() == name)
            : records[0];

        if (match.ValueKind != JsonValueKind.Object)
        {
            return (false, null);
        }

        // The kind is checked before the read: TryGetInt32 only reports "no" for a number it
        // cannot fit, and throws for a String or a Bool. So a wslc that ever reported State
        // as "running" would take the command down here -- which is the opposite of what a
        // watch loop needs, and what the old comment on this line wrongly assumed was
        // already handled. An unreadable state leaves the container listed, state unknown.
        return match.TryGetProperty("State", out var state) &&
               state.ValueKind == JsonValueKind.Number &&
               state.TryGetInt32(out var reported)
            ? (true, reported)
            : (true, null);
    }

    /// <summary>Whether a network listing names <paramref name="network"/>.</summary>
    internal static bool ListsNetwork(string output, string network) =>
        ParseRecords(output).Any(record =>
            record.TryGetProperty("Name", out var name) &&
            name.ValueKind == JsonValueKind.String &&
            name.GetString() == network);

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

        Log.Info($"creating network '{network}'");
        Execute(Builder.NetworkCreate(), exitOnFailure: false);
    }

    private bool NetworkExists(string network)
    {
        var (code, output) = Probe(Builder.NetworkList());
        if (code != 0)
        {
            return false;
        }

        return ListsNetwork(output, network);
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
                Log.Info($"dependency '{name}' is already running");
                break;

            case ContainerAction.Start:
                Log.Info($"starting existing dependency '{name}'");
                Execute(Builder.DependencyStart(name));
                break;

            default:
                Log.Info($"dependency '{name}' not found, creating it");
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

        Log.Info($"waiting for dependency '{name}' to become healthy");
        var startPeriodEnds = DateTime.UtcNow.AddSeconds(startPeriod);
        var failures = 0;

        while (true)
        {
            var (code, checkOutput) =
                Probe(Builder.DependencyExec(name, test), TimeSpan.FromSeconds(timeout), captureStderr: true);
            if (code == 0)
            {
                Log.Info($"dependency '{name}' is healthy");
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
                Log.Info($"container '{Config.Container}' is already running");
                break;

            case ContainerAction.Start:
                Log.Info($"starting existing container '{Config.Container}'");
                Execute(Builder.Start(detach), interactive);
                break;

            default:
                Log.Info($"container '{Config.Container}' not found, creating it");
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
        Log.Info($"syncing {settings.Source} -> {settings.Volume}:{settings.Target}");
        Execute(Builder.SyncRun());
        Log.Info($"run `wip sync --watch` in another terminal to keep {settings.Target} up to date");
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
        Log.Info(
            $"building service '{name}' (tag: {RubyValue.ToStringValue(spec.GetValueOrDefault("tag"))}) " +
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

        Log.Warn(
            $"this project is on the WSL filesystem ({directory}); wslc " +
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
                    Log.Info($"using cached build context at {staged}");
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

        Log.Warn(
            $"commands.{name} in wip.yml is shadowed by the built-in `wip {name}`; " +
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
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"watching {joined} for exited restart: containers every {interval}s (running detached; Ctrl-C to stop)"));

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

        Log.Info($"'{name}' has exited, restarting it (restart: {policy})");
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

        if (Debug)
        {
            Console.Error.WriteLine($"wip: [debug] '{name}': {output.Trim()}");
        }

        return ReadContainerEntry(output, name);
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
        Log.Info(stoppedMessage);
        return 0;
    }

    [GeneratedRegex(@"\Aon-failure(?::\d+)?\z")]
    private static partial Regex OnFailurePolicy();
}

/// <summary>The options every command shares, parsed once by <see cref="Program"/>.</summary>
internal sealed record CliOptions(string? ConfigPath, string? EnvFile, bool Debug, string? DebugLog, bool Quiet);
