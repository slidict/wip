using System.Text.Json;
using Wip.Configuration;
using Wip.Execution;

namespace Wip.Security;

/// <param name="Tool">The registered name that was run.</param>
/// <param name="Sandbox">The declared sandbox it ran in.</param>
/// <param name="Image">The pinned image the plan named.</param>
/// <param name="RulesVersion">The rule set the plan pinned.</param>
/// <param name="Network">The backend name of the boundary the run was verified behind.</param>
/// <param name="ExitCode">The tool status, or the reserved timeout status when TimedOut is true.</param>
/// <param name="TimedOut">Whether the execution deadline expired; exit 124 alone is ambiguous.</param>
/// <param name="Report">The complete report, or null when collection did not produce one.</param>
/// <param name="ReportError">The collection failure reason, independent of the scanner status.</param>
/// <param name="UnperformedChecks">Unmodified tool diagnostics and profile omissions, when requested.</param>
/// <param name="UnperformedChecksError">Why requested diagnostics could not be collected.</param>
/// <param name="PersistenceError">Why artifacts could not be saved, without replacing the scan result.</param>
/// <param name="ResultDirectory">The published run directory, when artifacts were saved.</param>
/// <remarks>
/// A tool's non-zero status is not an error here: scanners use exit codes to report findings.
/// Distinguishing "ran and found things" from "did not run" belongs to the result format that
/// consumes this, so it carries the status and the report without judging either.
/// </remarks>
internal sealed record ScanOutcome(
    string Tool,
    string Sandbox,
    string Image,
    string RulesVersion,
    string Network,
    int ExitCode,
    string? Report,
    bool TimedOut = false,
    string? ReportError = null,
    string? UnperformedChecks = null,
    string? UnperformedChecksError = null,
    ScanResult? Result = null,
    string? PersistenceError = null,
    string? ResultDirectory = null)
{
    /// <remarks>
    /// An incomplete result outranks a failed save when the document exists, since its reason
    /// is there. When the save also failed there is no document, so 70 would send a consumer
    /// to a result.json that is not there; that case gets its own code. A scanner's own use
    /// of a reserved code folds to 1 rather than claiming a condition that did not occur.
    /// </remarks>
    internal int ProcessExitCode => Result?.Status switch
    {
        "incomplete" when PersistenceError is not null => ScanEngine.IncompleteUnsavedExitCode,
        "incomplete" => ScanEngine.IncompleteExitCode,
        "completed" when PersistenceError is not null => ScanEngine.PersistenceFailedExitCode,
        "completed" when ScanEngine.IsReservedExitCode(ExitCode) => 1,
        _ => ExitCode
    };
}

/// <summary>
/// Drives exactly one registered scanner inside one declared sandbox, behind the boundary
/// that sandbox declares.
/// </summary>
/// <remarks>
/// <para>
/// Preparation validates the plan and requests its image from the host-side backend.
/// Execution independently validates that an already-running sandbox uses that image and
/// the declared boundary. There is no preparation token: Run revalidates its inputs rather
/// than requiring proof that Prepare was called. The CLI calls both in order.
/// </para>
/// <para>
/// The engine builds every backend invocation. An adapter supplies a <see cref="ScanPlan"/>
/// and nothing else, so there is no path by which it can widen the boundary -- the property
/// required for isolation and the reason the plan type carries no backend arguments.
/// </para>
/// </remarks>
internal sealed class ScanEngine(
    SandboxSettings settings,
    SandboxLifecycle sandboxes,
    INetworkLifecycle networks,
    SandboxBackend backend,
    RulesFetcher? rulesFetcher = null,
    Func<string, string>? mapRulesSource = null)
{
    /// <summary>
    /// Allows a host-side registry request to finish. An existing runnable sandbox already
    /// has this immutable image locally; a pull need not download new layers.
    /// </summary>
    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(20);

    // Thousands of host files may take minutes to cross into the WSL VM. Give the whole
    // rules copy a fetch-sized budget; cheap setup and probes retain their one-minute bound.
    private static readonly TimeSpan RulesCopyTimeout = TimeSpan.FromMinutes(5);

    // The fast path is one find per tree; the effective-access fallback forks a test per entry,
    // twice for a directory. A tree staged under umask 027 misses the fast path on its first
    // entry, so a template set of thousands of files pays that cost in full -- and a timeout
    // would refuse a scan whose rules were readable all along. Give the fallback the copy's
    // budget rather than the single-probe minute.
    private static readonly TimeSpan RulesAccessProbeTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Long by the standards of the other deadlines in wip, because an active web scan is
    /// minutes to tens of minutes of work rather than a backend call that either answers or
    /// has hung. Still bounded: an unbounded scan is one that can hold a sandbox forever.
    /// </summary>
    internal static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Collection is a bounded read, not another scan-length operation.</summary>
    private static readonly TimeSpan CollectionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The complete report must fit this retained-text budget.</summary>
    internal const int MaxReportCharacters = 1024 * 1024;

    // Reserve EX_SOFTWARE (70), in the application's conventional sysexits 64..78 range.
    // Unlike 256 it stays nonzero through POSIX/WSL shells; 125..127 overlap timeout or
    // shell invocation failures. Completed scanners that return 70 map to 1 so only an
    // incomplete run returns 70; provenance and result.json retain the original tool code.
    internal const int IncompleteExitCode = 70;

    // EX_IOERR (74), from the same sysexits range, for a scan that finished but whose
    // artifacts did not reach the requested directory. Distinct from 70 because the two call
    // for different responses: an incomplete scan has to be run again, while a completed one
    // whose save failed has its report on stdout and needs the output path looked at. Exit 0
    // would be the hole this result format exists to close -- asking for --output and getting
    // neither a file nor a nonzero status is "nothing happened" presented as success.
    internal const int PersistenceFailedExitCode = 74;

    // EX_CANTCREAT (73), for an incomplete scan whose artifacts were not saved either. A third
    // code rather than a stderr marker: the exit code is the contract consumers branch on, and
    // 70 tells them to read result.json first. Here there is none, and the output path needs
    // fixing before the rerun, or the rerun loses its result the same way.
    internal const int IncompleteUnsavedExitCode = 73;

    internal static bool IsReservedExitCode(int code) =>
        code is IncompleteExitCode or PersistenceFailedExitCode or IncompleteUnsavedExitCode;

    private readonly RulesFetcher? stagedRules = rulesFetcher;
    private readonly Func<string, string>? mapStagedSource = mapRulesSource;

    private SandboxDefinition Definition(string sandbox) =>
        settings.Sandboxes.SingleOrDefault(s => s.Name == sandbox)
            ?? throw new ConfigException($"Unknown sandbox: {sandbox}");

    /// <summary>Registered tool names, so a caller can list the choices without resolving one.</summary>
    internal static IReadOnlyList<string> Tools => ScannerRegistry.Names;

    /// <summary>
    /// The one scanner a selector names, from the build-time table. Resolution lives here so
    /// the registry stays the only way to obtain a scanner, while the phases below take the
    /// scanner itself -- which is what makes "one tool per run" a property of the signature
    /// rather than a rule to remember, and what lets the engine be tested without adding a
    /// test-only entry to the registry (arbitrary external tools cannot extend it).
    /// </summary>
    internal static ISecurityScanner Select(string tool) => ScannerRegistry.Resolve(tool);

    /// <summary>
    /// Validates a plan and requests its image through the host-side backend, outside the
    /// isolated sandbox. This does not create or start a sandbox. A successful Run requires
    /// one already running from this image, so the image may already be present locally.
    /// </summary>
    /// <returns>The validated plan for the caller to pass to Run.</returns>
    internal ScanPlan Prepare(ISecurityScanner scanner, string sandbox, ScanTarget target)
    {
        target.Validate();
        var definition = Definition(sandbox);
        // Checked before the image is fetched: a sandbox that cannot be isolated will never be
        // scanned, so pulling for it would be work done for a run that cannot happen.
        RequireDeclaredNetwork(definition, sandbox);
        var plan = scanner.Plan(target);
        plan.Validate(scanner.Name);
        // Before the pull, so a sandbox that would run a different image is refused rather
        // than having an image fetched for it that nothing will use.
        RequirePinnedImage(definition, sandbox, plan);
        RequireRulesOutsideMounts(definition, plan.Rules);
        if (plan.Rules is { } declaredRules)
        {
            // Refuse an unusable cache location before any fetch or cache writes. Run
            // rechecks the same source guard before copying; the cache stays config-local.
            var source = RequireRulesFetcher().StagingPath(declaredRules);
            _ = mapStagedSource?.Invoke(source);
        }
        // This registry request cannot replace the digest-pinned image with different
        // content. It does not prove the sandbox is running or that rules were installed.
        var pulled = backend(["pull", plan.Image.Reference], PullTimeout, SandboxConsoleMode.Stream);
        if (pulled.Code != 0)
            throw new WipException(
                $"Scanner {scanner.Name}: pulling {plan.Image.Reference} failed (exit {pulled.Code}); " +
                "no scan was started, because an isolated sandbox cannot fetch it later");
        if (plan.Rules is { } rules)
        {
            RequireRulesFetcher().Fetch(rules);
            plan = plan with { RulesVersion = VersionWithPin(plan) };
        }
        return plan;
    }

    /// <summary>
    /// Runs a validated plan inside the sandbox, behind its declared boundary, and collects
    /// the report. Stops and cleans up whatever happens.
    /// </summary>
    /// <param name="plan">
    /// A plan, normally returned by Prepare. It is independently validated here; the type
    /// carries no preparation history and callers can supply a plan without calling Prepare.
    /// </param>
    internal ScanOutcome Run(ISecurityScanner scanner, string sandbox, ScanPlan plan, ScanTarget target, TimeSpan? timeout = null,
        string? outputDirectory = null, string? commit = null)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        target.Validate();
        if (outputDirectory is not null) ScanResultStore.RequireUsable(outputDirectory);
        var definition = Definition(sandbox);
        var declared = RequireDeclaredNetwork(definition, sandbox);
        plan.Validate(scanner.Name);
        // Re-checked here for the reason the plan is re-validated: a caller can reach Run with
        // a plan it built itself, so neither the plan nor the pairing is taken on trust.
        RequirePinnedImage(definition, sandbox, plan);
        RequireRulesOutsideMounts(definition, plan.Rules);

        var deadline = timeout ?? DefaultScanTimeout;
        // Both bounds here, not just the lower one: the upper is what Exec enforces, and
        // leaving it to that would reject the deadline only after the boundary checks below
        // had run, in a place that reads as a scan failure rather than a bad argument.
        if (deadline <= TimeSpan.Zero || deadline.TotalMilliseconds > int.MaxValue)
            throw new ConfigException("Scan timeout must be positive and at most 2147483 seconds");

        // The boundary is verified here, immediately before anything executes, and not taken
        // on trust from `sandbox create`. A container created before the network was declared
        // is still running on whatever it was given: create refuses it, but nothing else did
        // until now (slidict/wip#263). This is the check at the moment it matters.
        var network = networks.VerifiedBackendName(declared);
        // Set only after the owned/internal verification succeeds, and pass the attestation
        // to result construction instead of assuming that every finishing path verified it.
        var networkVerifiedInternal = true;
        var status = sandboxes.Status(sandbox);
        if (status.Id is null || status.State != "running")
            throw new WipException($"Sandbox {sandbox}: is not running; run sandbox create before a scan");
        if (status.Networks.Count != 1 || status.Networks[0] != network)
            throw new WipException(
                $"Sandbox {sandbox}: is not attached to exactly its declared network; no scan was started. " +
                "Run sandbox destroy, confirm absence, then sandbox create.");
        // The declaration is not enough. A container is created from whatever the declaration
        // said at the time, so editing it to a different pinned image afterwards leaves the old
        // image running while the plan -- and the outcome -- name the new one. Comparing what
        // is actually running is what keeps "which tool ran" answerable.
        if (status.Image is not { } running)
            throw new WipException(
                $"Sandbox {sandbox}: the running container's image could not be read, so the pinned tool is unverified; no scan was started");
        if (!string.Equals(running, plan.Image.Reference, StringComparison.Ordinal))
            throw new WipException(
                $"Sandbox {sandbox}: is running image '{running}', but the tool pins '{plan.Image.Reference}'. " +
                "The declaration changed after the container was created, so run sandbox destroy, confirm absence, then sandbox create.");

        try
        {
            if (plan.Rules is { } rules)
            {
                foreach (var mount in status.Mounts)
                    RequireRulesOutsideMount(rules, mount.Destination);
                CopyRules(status.Id, rules);
            }
            plan = plan with { RulesVersion = VersionWithPin(plan) };
            var execution = sandboxes.ExecWithResult(sandbox, plan.Argv, deadline);
            var code = execution.Code;
            if (execution.TimedOut)
            {
                // Do not collect a report that may still be changing. Persist only the
                // incomplete metadata before the finally block stops the container.
                var timedOut = new ScanOutcome(scanner.Name, sandbox, plan.Image.Reference, plan.RulesVersion, network, code, null, TimedOut: true,
                    UnperformedChecksError: "scan timed out before diagnostics could be collected");
                return FinishResult(scanner, target, timedOut, runId, startedAt, networkVerifiedInternal, outputDirectory, commit);
            }
            var collectionDeadline = deadline < CollectionTimeout ? deadline : CollectionTimeout;
            var (report, reportError) = ReadReport(sandbox, plan.ReportPath, collectionDeadline);
            var (unperformed, diagnosticsError) = ReadReport(sandbox, plan.UnperformedChecksPath, collectionDeadline);
            var outcome = new ScanOutcome(scanner.Name, sandbox, plan.Image.Reference, plan.RulesVersion, network, code, report,
                ReportError: reportError, UnperformedChecks: unperformed, UnperformedChecksError: diagnosticsError);
            return FinishResult(scanner, target, outcome, runId, startedAt, networkVerifiedInternal, outputDirectory, commit);
        }
        finally
        {
            // The container stop includes the tool's children. Removal remains sandbox
            // destroy's responsibility, so failed runs leave the container inspectable.
            Stop(sandbox);
        }
    }

    private RulesFetcher RequireRulesFetcher() => stagedRules
        ?? throw new WipException("Pinned rules require a host staging cache; no scan was started");

    private static string VersionWithPin(ScanPlan plan)
    {
        if (plan.Rules is not { } rules) return plan.RulesVersion;
        var suffix = ";rules-commit:" + rules.Commit;
        return plan.RulesVersion.EndsWith(suffix, StringComparison.Ordinal)
            ? plan.RulesVersion : plan.RulesVersion + suffix;
    }

    private void RequireRulesOutsideMounts(SandboxDefinition definition, PinnedRules? rules)
    {
        if (rules is null) return;
        // Mount knowledge belongs to the engine, not declaration validation. Neither rules
        // nor their derived staging path may delete through a declared or running mount.
        foreach (var name in definition.Volumes)
            RequireRulesOutsideMount(rules, settings.Volumes.Single(volume => volume.Name == name).Mount);
    }

    private static void RequireRulesOutsideMount(PinnedRules rules, string mount)
    {
        if (string.IsNullOrEmpty(mount) || !mount.StartsWith('/') || mount.Any(char.IsControl) ||
            (mount != "/" && mount[1..].Split('/').Any(part => part is "" or "." or "..")))
            throw new WipException("Pinned rules cannot be copied because a sandbox mount path is unverified");
        foreach (var path in new[] { rules.ContainerPath, rules.ContainerPath + ".staging" })
        {
            if (mount == "/" || path == mount || path.StartsWith(mount + "/", StringComparison.Ordinal) ||
                mount.StartsWith(path + "/", StringComparison.Ordinal))
                throw new ConfigException($"Pinned rules path {path} overlaps sandbox mount {mount}; no rules may be replaced through a mount");
        }
    }

    private void CopyRules(string container, PinnedRules rules)
    {
        var staged = RequireRulesFetcher().RequireStagedTree(rules);
        var source = mapStagedSource is null ? Path.GetFullPath(staged.Path) : mapStagedSource(staged.Path);
        var limit = TimeSpan.FromMinutes(1);
        // WSLC requires an existing destination and nests the source directory even when
        // the source ends in /.; stdin tar extracts contents directly but needs backend
        // stdin plumbing and host tar. Nest then move; the cache names the copied directory.
        // Appending to the final canonical segment makes staging a distinct sibling of
        // the rules path, never the rules path itself or one of its ancestors.
        var staging = rules.ContainerPath + ".staging";
        var ancestors = new List<string> { "/" };
        foreach (var segment in rules.ContainerPath[1..].Split('/').SkipLast(1))
        {
            var parent = ancestors[^1];
            ancestors.Add(parent == "/" ? parent + segment : parent + "/" + segment);
        }
        // A symlink ancestor could alias a mounted path and bypass lexical mount checks.
        // Refuse before removal; test returns 1 for a plain or absent ancestor, which mkdir
        // may create. Resolving a destination that does not yet exist is not reliable here.
        foreach (var path in ancestors)
        {
            var link = backend(["exec", container, "test", "-L", path], limit, SandboxConsoleMode.Capture);
            if (link.Code != 1 || link.TimedOut || link.OutputLimitExceeded)
                throw new WipException($"Pinned rules ancestor {path} is a symbolic link or its link status could not be verified; no scan was started");
        }
        RequireCopyStep(["exec", container, "rm", "-rf", "--", rules.ContainerPath, staging]);
        RequireCopyStep(["exec", container, "mkdir", "-p", "--", staging]);
        RequireCopyStep(["container", "cp", source, container + ":" + staging], RulesCopyTimeout);
        RequireCopyStep(["exec", container, "mv", "--", staging + "/" + RulesFetcher.TreeDirectoryName, rules.ContainerPath]);
        RequireCopyStep(["exec", container, "rmdir", "--", staging]);
        // A mode that grants every class what the entry needs -- read and traverse for a
        // directory (-555), read for a file (-444) -- is a fast acceptance path with no
        // per-entry processes: the answer holds whoever the scanner is. Other
        // bits alone would not -- Linux applies the owner class first and never falls back to
        // other, so mode 0004 is unreadable to its own owner. Any other mode falls through to
        // kernel access checks, which exec evaluates under the image user's credentials, the
        // same ones the scanner runs with, so owner/group access and root capabilities count.
        // Do not chmod image ancestors or add --user/extend ScanPlan. BusyBox 1.36.1
        // (Alpine 3.20) lacks -readable; portable -exec test handles root and non-root alike.
        foreach (var path in ancestors) RequireAncestor(path);
        RequirePermissions("d", "-555", "unreadable or untraversable directory");
        // NUL separates paths without miscounting names containing newlines. The count
        // preserves the empty-tree refusal and detects a successful but partial copy.
        var contents = backend(["exec", container, "find", rules.ContainerPath, "-type", "f", "-print0"],
            limit, SandboxConsoleMode.Capture);
        if (contents.Code != 0 || contents.TimedOut || contents.OutputLimitExceeded ||
            (contents.Output.Length > 0 && contents.Output[^1] != '\0'))
            throw new WipException("Pinned rules presence could not be verified; no scan was started");
        var actualCount = contents.Output.Count(character => character == '\0');
        if (actualCount == 0)
            throw new WipException($"Pinned rules are empty in the sandbox: expected {staged.FileCount} files, found 0; no scan was started");
        if (actualCount != staged.FileCount)
        {
            var problem = actualCount < staged.FileCount ? "is incomplete" : "contains extra files";
            throw new WipException($"Pinned rules copy {problem}: expected {staged.FileCount} files, found {actualCount}; no scan was started");
        }
        RequirePermissions("f", "-444", "unreadable file");

        void RequireAncestor(string path)
        {
            // An ancestor only has to be traversable: opening a file beneath it never lists it,
            // so requiring read as well refuses a 0711 image directory the scanner could use.
            // A trailing /. resolves a directory symlink instead of letting -type d skip it.
            var probePath = path.TrimEnd('/') + "/.";
            if (!PermissionsAllowed(["exec", container, "find", probePath, "-maxdepth", "0", "-type", "d"], "-111", false, "-x"))
                throw new WipException($"Pinned rules ancestor {path} is untraversable, or its permissions could not be verified; no scan was started");
        }

        void RequirePermissions(string type, string permission, string failure)
        {
            var kind = type == "d" ? "directory" : "file";
            if (!PermissionsAllowed(["exec", container, "find", rules.ContainerPath, "-type", type], permission, type == "d"))
                throw new WipException($"Pinned rules contain an {failure} or {kind} permissions could not be verified; no scan was started");
        }

        bool PermissionsAllowed(string[] prefix, string permission, bool directory, params string[] accesses)
        {
            var result = backend([.. prefix, "!", "-perm", permission, "-print", "-quit"], limit, SandboxConsoleMode.Capture);
            if (Failed(result)) return false;
            if (string.IsNullOrWhiteSpace(result.Output)) return true;
            foreach (var access in accesses.Length > 0 ? accesses : directory ? ["-r", "-x"] : ["-r"])
            {
                result = backend([.. prefix, "!", "-exec", "test", access, "{}", ";", "-print", "-quit"],
                    RulesAccessProbeTimeout, SandboxConsoleMode.Capture);
                if (Failed(result) || !string.IsNullOrWhiteSpace(result.Output)) return false;
            }
            return true;

            static bool Failed(SandboxCommandResult result) => result.Code != 0 || result.TimedOut || result.OutputLimitExceeded;
        }

        void RequireCopyStep(IReadOnlyList<string> arguments, TimeSpan? timeout = null)
        {
            var result = backend(arguments, timeout ?? limit, SandboxConsoleMode.Stream);
            if (result.Code != 0 || result.TimedOut)
                throw new WipException($"Pinned rules copy failed (exit {result.Code}); no scan was started");
        }
    }

    private static ScanOutcome FinishResult(ISecurityScanner scanner, ScanTarget target, ScanOutcome outcome,
        string runId, DateTimeOffset startedAt, bool networkVerifiedInternal, string? outputDirectory, string? commit)
    {
        string? reason = outcome.TimedOut ? "timeout"
            : outcome.ReportError is not null || outcome.Report is null ? "report-collection-failed"
            : string.IsNullOrWhiteSpace(outcome.Report) ? "report-empty" : null;
        IReadOnlyList<ScanFinding>? findings = null;
        if (reason is null)
        {
            try { findings = scanner.ConvertReport(outcome.Report!); }
            catch (JsonException) { reason = "report-unparseable"; }
            catch (FormatException) { reason = "report-unparseable"; }
        }
        var missingCoverage = outcome.UnperformedChecksError;
        if (missingCoverage is null && string.IsNullOrWhiteSpace(outcome.UnperformedChecks))
            missingCoverage = "The unperformed-check record was not collected or was empty";
        if (reason is null && missingCoverage is not null) reason = "unperformed-checks-lost";

        var run = new ScanRunMetadata(runId, startedAt, DateTimeOffset.UtcNow, outcome.Tool, outcome.Image,
            outcome.RulesVersion, outcome.Sandbox, outcome.Network, networkVerifiedInternal, target.Address, commit);
        var result = new ScanResult(run, reason is null ? "completed" : "incomplete",
            Findings: reason is null ? findings : null, IncompleteReason: reason,
            UnperformedChecks: missingCoverage is null ? outcome.UnperformedChecks : null,
            CoverageMissingReason: missingCoverage, ScannerExitCode: outcome.ExitCode);
        // The adapter only converts report text. The engine alone chooses the output path
        // and publishes all artifacts while the sandbox is still available, before teardown.
        var finished = outcome with { Result = result };
        if (outputDirectory is not null)
        {
            try { return finished with { ResultDirectory = new ScanResultStore(outputDirectory).Write(result, outcome.Report, outcome.UnperformedChecks) }; }
            catch (WipException error)
            {
                // Preflight cannot prevent a later disk failure. Keep the primary report,
                // scanner status and normalized result, and report storage failure separately.
                return finished with { PersistenceError = error.Message };
            }
        }
        return finished;
    }

    /// <summary>
    /// Refuses a sandbox whose declared image is not the one the plan pinned.
    /// </summary>
    /// <remarks>
    /// Without this the pin guarantees nothing. The scan runs as an exec inside the declared
    /// sandbox, so the tool that actually executes is whatever that sandbox's image provides;
    /// pulling the plan's image would leave it unused, and the outcome would name an image
    /// that had not run. Binding the two is what makes "which tool ran" answerable, so the
    /// scanner's immutable image belongs in the sandbox declaration.
    /// </remarks>
    private static void RequirePinnedImage(SandboxDefinition definition, string sandbox, ScanPlan plan)
    {
        if (string.Equals(definition.Image, plan.Image.Reference, StringComparison.Ordinal)) return;
        throw new ConfigException(
            $"Sandbox {sandbox}: declares image '{definition.Image}', but the tool pins " +
            $"'{plan.Image.Reference}'. The scan runs inside this sandbox, so its declaration must name " +
            "the pinned image -- otherwise the tool that runs is not the one that was pinned.");
    }

    /// <summary>
    /// The declared network's name, or a refusal: there is no unrestricted scan mode.
    /// </summary>
    private static string RequireDeclaredNetwork(SandboxDefinition definition, string sandbox) =>
        definition.Network
        ?? throw new ConfigException(
            $"Sandbox {sandbox}: declares no network; a scan requires an internal network, " +
            $"so add a networks: entry with internal: true and reference it from sandboxes.{sandbox}.network");

    /// <summary>
    /// The report as the tool left it, or null when there is none. Read as bytes through the
    /// sandbox rather than trusted as text: the content is a tool's output, which is treated
    /// as untrusted input downstream, and nothing here interprets it.
    /// </summary>
    private (string? Report, string? Error) ReadReport(string sandbox, string reportPath, TimeSpan timeout)
    {
        // `cat` with the path as its own argument, never a shell line: a report path is data,
        // and composing it into `sh -c` would make it executable.
        try
        {
            var captured = sandboxes.CaptureReport(sandbox, ["cat", reportPath], timeout);
            if (captured.TimedOut)
                return (null, $"report collection timed out after {timeout.TotalSeconds:g} seconds");
            if (captured.OutputLimitExceeded || captured.Output.Length > MaxReportCharacters)
                return (null, "report exceeded the 1 Mi-character retention limit; no partial report was returned");
            if (captured.Code != 0)
                return (null, $"report collection failed (exit {captured.Code})");
            return (captured.Output, null);
        }
        catch (WipException error)
        {
            // Collection failure must not erase the scanner's status and provenance.
            return (null, $"report collection failed: {error.Message}");
        }
    }

    private void Stop(string sandbox)
    {
        // A cleanup failure must not replace the scan's own outcome, so it is swallowed here
        // and left to `sandbox status` to show. Rethrowing from a finally would discard
        // whichever result or exception brought us here.
        // ConfigException derives from WipException, so the one clause covers both.
        try { sandboxes.Stop(sandbox); }
        catch (WipException) { }
    }
}
