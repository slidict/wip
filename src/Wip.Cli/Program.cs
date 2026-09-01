using System.CommandLine;
using Wip.Ai;
using Wip.Configuration;
using Wip.Diagnostics;

namespace Wip.Cli;

internal static class Program
{
    internal static int Main(string[] args)
    {
        var root = BuildRoot();

        // System.CommandLine installs its own top-level handler that prints a raw stack
        // trace. wip reports failures as a one-line message, so that handler is turned off
        // and the exceptions are caught here instead.
        var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false };

        try
        {
            return Parse(root, args).Invoke(invocation);
        }
        catch (ExitException exit)
        {
            return exit.Code;
        }
        catch (WipException exception)
        {
            Log.Error(exception.Message);
            return 1;
        }
    }

    /// <summary>
    /// Routes an unrecognised first word to <c>dispatch</c>, so a name defined under
    /// <c>commands:</c> in wip.yml runs as <c>wip test</c> and not only as
    /// <c>wip dispatch test</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both the decision and the rewrite avoid reading argv positionally, because argv
    /// cannot be interpreted without knowing each option's arity. Deciding comes from a real
    /// parse. Rewriting prepends rather than inserting: searching argv for the unmatched
    /// token finds the wrong occurrence when an option value happens to equal it, so
    /// <c>wip --config test test</c> would have turned into <c>--config dispatch</c> and
    /// swallowed the command. Prepending is safe because recursive options are
    /// position-independent, so they parse identically after <c>dispatch</c>.
    /// </para>
    /// <para>
    /// The Ruby CLI needed a second rewrite here, reordering global options so that
    /// <c>wip --config x up</c> reached Thor as <c>wip up --config x</c>. System.CommandLine's
    /// recursive options are already position-independent, so that half is simply gone.
    /// </para>
    /// </remarks>
    internal static ParseResult Parse(RootCommand root, string[] args)
    {
        var parsed = root.Parse(args);

        // A matched subcommand, or nothing left over, means there is no custom name to route.
        if (!ReferenceEquals(parsed.CommandResult.Command, root) || parsed.UnmatchedTokens.Count == 0)
        {
            return parsed;
        }

        // An unrecognised option is a usage error, not a command name; leave it to report
        // itself rather than dragging it into `dispatch`.
        if (parsed.UnmatchedTokens[0].StartsWith('-'))
        {
            return parsed;
        }

        return root.Parse([.. new[] { "dispatch" }.Concat(args)]);
    }

    /// <summary>Builds the command tree. Internal so <see cref="Parse"/> can be tested against it.</summary>
    internal static RootCommand BuildRoot()
    {
        var configOption = new Option<string?>("--config") { Description = "Path to wip.yml", Recursive = true };
        var envFileOption = new Option<string?>("--env-file")
        {
            Description = "Path to a dotenv file (default: .env next to wip.yml)",
            Recursive = true,
        };
        var debugOption = new Option<bool>("--debug")
        {
            Description = "Print progress and timing for each step",
            Recursive = true,
        };
        var debugLogOption = new Option<string?>("--debug-log")
        {
            Description = "Where --debug snapshots go: a file path, or \"-\" for inline",
            Recursive = true,
        };
        var quietOption = new Option<bool>("--quiet", "-q")
        {
            Description =
                "Hold back a shelled-out command's own output and print it only if that " +
                "command fails (--debug's own lines are unaffected)",
            Recursive = true,
        };

        var root = new RootCommand("A developer-friendly CLI wrapper for Microsoft WSLC.")
        {
            configOption,
            envFileOption,
            debugOption,
            debugLogOption,
            quietOption,
        };

        CliContext Context(ParseResult parsed) => new(new CliOptions(
            parsed.GetValue(configOption),
            parsed.GetValue(envFileOption),
            parsed.GetValue(debugOption),
            parsed.GetValue(debugLogOption),
            parsed.GetValue(quietOption)));

        foreach (var command in BuildCommands(Context))
        {
            root.Subcommands.Add(command);
        }

        return root;
    }

    private static IEnumerable<Command> BuildCommands(Func<ParseResult, CliContext> context)
    {
        yield return Simple("version", "Show wip and WSLC versions", context, ctx => ctx.Version());

        var force = new Option<bool>("--force") { Description = "Overwrite an existing wip.yml" };
        var template = new Option<string?>("--template")
        {
            Description = $"sync.exclude preset: {string.Join(", ", Initializer.TemplateLabels.Keys)} (default: none)",
        };
        var ai = new Option<bool>("--ai")
        {
            Description = "Generate or update wip.yml from a natural-language request using a local AI server",
        };
        var url = AiUrlOption();
        var allowRemoteAi = AllowRemoteAiOption();
        var init = new Command("init", "Create a starter wip.yml (detects an existing compose file)")
        {
            force,
            template,
            ai,
            url,
            allowRemoteAi,
        };
        init.SetAction(parsed => context(parsed).Init(
            parsed.GetValue(force), parsed.GetValue(template), parsed.GetValue(ai), parsed.GetValue(url),
            parsed.GetValue(allowRemoteAi)));
        yield return init;

        yield return DoctorCommand(context);
        yield return HelpCommand(context);
        yield return Simple("config", "Print the effective configuration", context, ctx => ctx.ShowConfig());

        yield return BuildCommand(context);
        yield return UpCommand(context);
        yield return SyncCommand(context);

        yield return Simple("ps", "Show the current state of the configured container or stack",
            context, ctx => ctx.Ps());
        yield return Simple("status", "Alias for `wip ps`", context, ctx => ctx.Ps());

        yield return Simple("stop", "Stop the configured container and its dependencies without removing them",
            context, ctx => ctx.Stop());
        yield return DownCommand(context);
        yield return Simple("restart", "Restart the configured container or stack (stop, then start — no rebuild)",
            context, ctx => ctx.Restart());

        yield return ExecCommand(context);
        yield return RunCommand(context);
        yield return Simple("shell", "Open a shell in the configured container", context, ctx => ctx.Shell());
        yield return LogsCommand(context);
        yield return DispatchCommand(context);
    }

    private static Option<string?> AiUrlOption() => new("--url")
    {
        Description = $"Local AI server base URL for --ai (default: {LocalAiProvider.BaseUrlEnvironmentVariable} or {LocalAiProvider.DefaultBaseUrl})",
    };

    private static Option<bool> AllowRemoteAiOption() => new("--allow-remote-ai")
    {
        Description = "Explicitly allow sending data to a non-loopback AI server (including insecure HTTP)",
    };

    private static Command DoctorCommand(Func<ParseResult, CliContext> context)
    {
        var url = AiUrlOption();
        var command = new Command("doctor", "Diagnose the development environment") { url };
        command.SetAction(parsed => context(parsed).Doctor(parsed.GetValue(url)));
        return command;
    }

    private static Command HelpCommand(Func<ParseResult, CliContext> context)
    {
        var ai = new Option<bool>("--ai")
        {
            Description = "Ask a local AI server how to use wip instead of printing --help",
        };
        var url = AiUrlOption();
        var allowRemoteAi = AllowRemoteAiOption();
        var question = new Argument<string[]>("question") { Arity = ArgumentArity.ZeroOrMore };
        var command = new Command("help", "Show usage help (add --ai to ask a local AI server instead)")
        {
            ai, url, allowRemoteAi, question,
        };

        command.SetAction(parsed =>
        {
            if (!parsed.GetValue(ai))
            {
                if (parsed.GetValue(url) is not null)
                {
                    throw new WipException("--url requires --ai");
                }
                if (parsed.GetValue(allowRemoteAi))
                {
                    throw new WipException("--allow-remote-ai requires --ai");
                }

                if ((parsed.GetValue(question) ?? []).Length > 0)
                {
                    throw new WipException("a question requires --ai");
                }

                return ShowHelp();
            }

            return context(parsed).HelpAi(
                parsed.GetValue(url), parsed.GetValue(question) ?? [], parsed.GetValue(allowRemoteAi));
        });

        return command;
    }

    /// <summary>Prints the same text as <c>wip --help</c>, so <c>wip help</c> needs no text of
    /// its own to keep in sync with the real command tree.</summary>
    private static int ShowHelp()
    {
        var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false };
        return BuildRoot().Parse(["--help"]).Invoke(invocation);
    }

    /// <summary>The same output as <see cref="ShowHelp"/>, captured as text instead of printed,
    /// so <c>wip help --ai</c> can hand it to the local AI server as grounding context.</summary>
    internal static string HelpText()
    {
        var writer = new StringWriter();
        var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false, Output = writer };
        BuildRoot().Parse(["--help"]).Invoke(invocation);
        return writer.ToString();
    }

    private static Command Simple(
        string name,
        string description,
        Func<ParseResult, CliContext> context,
        Func<CliContext, int> action)
    {
        var command = new Command(name, description);
        command.SetAction(parsed => action(context(parsed)));
        return command;
    }

    private static Command BuildCommand(Func<ParseResult, CliContext> context)
    {
        var noCache = new Option<bool>("--no-cache") { Description = "Build without using cached layers" };
        var extra = new Argument<string[]>("extra") { Arity = ArgumentArity.ZeroOrMore };
        var command = new Command("build", "Build the configured image") { noCache, extra };
        command.SetAction(parsed =>
            context(parsed).Build(parsed.GetValue(extra) ?? [], parsed.GetValue(noCache)));
        return command;
    }

    private static Command UpCommand(Func<ParseResult, CliContext> context)
    {
        var detach = new Option<bool>("--detach", "-d");
        var sync = new Option<bool>("--sync")
        {
            Description = "Mirror the source into the sync volume first (--no-sync skips)",
            DefaultValueFactory = _ => true,
        };
        var noSync = new Option<bool>("--no-sync") { Description = "Skip the pre-boot mirror" };
        var noCache = new Option<bool>("--no-cache")
        {
            Description = "Build compose-native images without cached layers",
        };
        var watch = new Option<bool>("--watch", "-w")
        {
            Description = "Poll dependencies and restart any exited one whose restart: allows it (implies -d)",
        };
        var interval = new Option<double>("--interval")
        {
            Description = "Seconds between --watch polls (default: 5)",
            DefaultValueFactory = _ => 5,
        };

        var command = new Command(
            "up",
            "Start the configured container and its dependencies, creating them if necessary")
        {
            detach, sync, noSync, noCache, watch, interval,
        };

        command.SetAction(parsed => context(parsed).Up(
            parsed.GetValue(detach),
            parsed.GetValue(sync) && !parsed.GetValue(noSync),
            parsed.GetValue(noCache),
            parsed.GetValue(watch),
            parsed.GetValue(interval)));

        return command;
    }

    private static Command SyncCommand(Func<ParseResult, CliContext> context)
    {
        var watch = new Option<bool>("--watch", "-w") { Description = "Keep re-syncing until interrupted" };
        var interval = new Option<double?>("--interval")
        {
            Description = "Seconds between syncs when watching (default: sync.interval)",
        };

        var command = new Command("sync", "Mirror the source tree into the sync volume") { watch, interval };
        command.SetAction(parsed => context(parsed).Sync(parsed.GetValue(watch), parsed.GetValue(interval)));
        return command;
    }

    private static Command DownCommand(Func<ParseResult, CliContext> context)
    {
        var terminateSession = new Option<bool>("--terminate-session")
        {
            Description =
                "Also run `wslc system session terminate` after removing containers — resets " +
                "the whole WSLC session (every project sharing it, not just this one), which " +
                "is otherwise a manual recovery step for a session-wide mounted-volume limit",
        };

        var command = new Command("down", "Stop and remove the configured container and its dependencies")
        {
            terminateSession,
        };
        command.SetAction(parsed => context(parsed).Down(parsed.GetValue(terminateSession)));
        return command;
    }

    private static Command ExecCommand(Func<ParseResult, CliContext> context)
    {
        var (interactive, noInteractive) = InteractiveOptions();
        var arguments = new Argument<string[]>("command") { Arity = ArgumentArity.ZeroOrMore };
        var command = new Command("exec", "Execute a command in the running container")
        {
            interactive, noInteractive, arguments,
        };

        command.SetAction(parsed => context(parsed).Exec(
            parsed.GetValue(arguments) ?? [],
            parsed.GetValue(interactive) && !parsed.GetValue(noInteractive)));

        return command;
    }

    private static Command RunCommand(Func<ParseResult, CliContext> context)
    {
        var (interactive, noInteractive) = InteractiveOptions();
        var arguments = new Argument<string[]>("command") { Arity = ArgumentArity.ZeroOrMore };
        var command = new Command("run", "Run a command in a new container")
        {
            interactive, noInteractive, arguments,
        };

        command.SetAction(parsed => context(parsed).Run(
            parsed.GetValue(arguments) ?? [],
            parsed.GetValue(interactive) && !parsed.GetValue(noInteractive)));

        return command;
    }

    private static Command LogsCommand(Func<ParseResult, CliContext> context)
    {
        var follow = new Option<bool>("--follow", "-f") { DefaultValueFactory = _ => true };
        var noFollow = new Option<bool>("--no-follow") { Description = "Print current logs and exit" };
        var services = new Argument<string[]>("services") { Arity = ArgumentArity.ZeroOrMore };

        var command = new Command("logs", "Follow logs from the configured container or compose services")
        {
            follow, noFollow, services,
        };

        command.SetAction(parsed => context(parsed).Logs(
            parsed.GetValue(services) ?? [],
            parsed.GetValue(follow) && !parsed.GetValue(noFollow)));

        return command;
    }

    private static Command DispatchCommand(Func<ParseResult, CliContext> context)
    {
        var name = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne };
        var arguments = new Argument<string[]>("args") { Arity = ArgumentArity.ZeroOrMore };
        var command = new Command("dispatch", "Run a command defined in wip.yml") { name, arguments };

        command.SetAction(parsed =>
        {
            var target = parsed.GetValue(name);
            if (target is null)
            {
                Console.WriteLine(parsed.CommandResult.Command.Description);
                return 0;
            }

            return context(parsed).Dispatch(target, parsed.GetValue(arguments) ?? []);
        });

        return command;
    }

    /// <summary>
    /// Interactivity defaults on and is switched off by an explicit --no-interactive, which
    /// is how the Ruby CLI's boolean option behaved.
    /// </summary>
    private static (Option<bool> Interactive, Option<bool> NoInteractive) InteractiveOptions() =>
        (new Option<bool>("--interactive") { DefaultValueFactory = _ => true },
            new Option<bool>("--no-interactive"));
}
