using System.CommandLine;
using Wip.Configuration;

namespace Wip.Cli;

internal static class Program
{
    internal static int Main(string[] args)
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

        var root = new RootCommand("A developer-friendly CLI wrapper for Microsoft WSLC.")
        {
            configOption,
            envFileOption,
            debugOption,
            debugLogOption,
        };

        CliContext Context(ParseResult parsed) => new(new CliOptions(
            parsed.GetValue(configOption),
            parsed.GetValue(envFileOption),
            parsed.GetValue(debugOption),
            parsed.GetValue(debugLogOption)));

        foreach (var command in BuildCommands(Context))
        {
            root.Subcommands.Add(command);
        }

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
            Console.Error.WriteLine($"wip: {exception.Message}");
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
    /// The decision is made from a real parse rather than by scanning argv, because argv
    /// cannot be read without knowing each option's arity: in <c>wip --config x config</c>
    /// the first word that does not start with a dash is x, the value of --config, not a
    /// command. Parsing first and reacting to what went unmatched sidesteps that entirely.
    /// </para>
    /// <para>
    /// The Ruby CLI needed a second rewrite here, reordering global options so that
    /// <c>wip --config x up</c> reached Thor as <c>wip up --config x</c>. System.CommandLine's
    /// recursive options are already position-independent, so that half is simply gone.
    /// </para>
    /// </remarks>
    private static ParseResult Parse(RootCommand root, string[] args)
    {
        var parsed = root.Parse(args);

        // A matched subcommand, or nothing left over, means there is no custom name to route.
        if (!ReferenceEquals(parsed.CommandResult.Command, root) || parsed.UnmatchedTokens.Count == 0)
        {
            return parsed;
        }

        var index = Array.IndexOf(args, parsed.UnmatchedTokens[0]);
        if (index < 0)
        {
            return parsed;
        }

        var rewritten = new List<string>(args.Length + 1);
        rewritten.AddRange(args[..index]);
        rewritten.Add("dispatch");
        rewritten.AddRange(args[index..]);
        return root.Parse([.. rewritten]);
    }

    private static IEnumerable<Command> BuildCommands(Func<ParseResult, CliContext> context)
    {
        yield return Simple("version", "Show wip and WSLC versions", context, ctx => ctx.Version());

        var force = new Option<bool>("--force") { Description = "Overwrite an existing wip.yml" };
        var template = new Option<string?>("--template")
        {
            Description = $"sync.exclude preset: {string.Join(", ", Initializer.TemplateLabels.Keys)} (default: none)",
        };
        var init = new Command("init", "Create a starter wip.yml (detects an existing compose file)")
        {
            force,
            template,
        };
        init.SetAction(parsed => context(parsed).Init(parsed.GetValue(force), parsed.GetValue(template)));
        yield return init;

        yield return Simple("doctor", "Diagnose the development environment", context, ctx => ctx.Doctor());
        yield return Simple("config", "Print the effective configuration", context, ctx => ctx.ShowConfig());

        yield return BuildCommand(context);
        yield return UpCommand(context);
        yield return SyncCommand(context);

        yield return Simple("stop", "Stop the configured container and its dependencies without removing them",
            context, ctx => ctx.Stop());
        yield return Simple("down", "Stop and remove the configured container and its dependencies",
            context, ctx => ctx.Down());

        yield return ExecCommand(context);
        yield return RunCommand(context);
        yield return Simple("shell", "Open a shell in the configured container", context, ctx => ctx.Shell());
        yield return LogsCommand(context);
        yield return DispatchCommand(context);
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

        var command = new Command("logs", "Follow logs from compose services (compose mode only)")
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
