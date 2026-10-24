using Wip.Configuration;
using Wip.Execution;

namespace Wip.Security;

/// <param name="Tool">The registered name that was run.</param>
/// <param name="Sandbox">The declared sandbox it ran in.</param>
/// <param name="Image">The pinned image the plan named.</param>
/// <param name="RulesVersion">The rule set the plan pinned.</param>
/// <param name="Network">The backend name of the boundary the run was verified behind.</param>
/// <param name="ExitCode">The tool's own status, forwarded rather than interpreted.</param>
/// <param name="Report">The report as the tool wrote it, or null when it wrote none.</param>
/// <remarks>
/// A tool's non-zero status is not an error here: scanners use exit codes to report findings.
/// Distinguishing "ran and found things" from "did not run" belongs to the result format in
/// slidict/workspace#690, so this carries the status and the report without judging either.
/// </remarks>
internal sealed record ScanOutcome(
    string Tool,
    string Sandbox,
    string Image,
    string RulesVersion,
    string Network,
    int ExitCode,
    string? Report);

/// <summary>
/// Drives exactly one registered scanner inside one declared sandbox, behind the boundary
/// that sandbox declares.
/// </summary>
/// <remarks>
/// <para>
/// The phases #692 asks to be separated are separated here, and the separation is what makes
/// a fail-closed network workable at all: a pinned image and a pinned rule set have to be
/// fetched where there is egress, so <see cref="Prepare"/> runs outside the isolated sandbox
/// and <see cref="Run"/> only consumes what it left behind. Run refuses to start if Prepare
/// did not complete.
/// </para>
/// <para>
/// The engine builds every backend invocation. An adapter supplies a <see cref="ScanPlan"/>
/// and nothing else, so there is no path by which it can widen the boundary -- the property
/// #692 requires and the reason the plan type carries no backend arguments.
/// </para>
/// </remarks>
internal sealed class ScanEngine(
    SandboxSettings settings,
    SandboxLifecycle sandboxes,
    INetworkLifecycle networks,
    SandboxBackend backend)
{
    /// <summary>
    /// Generous because it is one image download, and the whole reason preparation is a phase
    /// of its own: this is the step that is allowed to reach the network, so it is the step
    /// that waits on one.
    /// </summary>
    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Long by the standards of the other deadlines in wip, because an active web scan is
    /// minutes to tens of minutes of work rather than a backend call that either answers or
    /// has hung. Still bounded: an unbounded scan is one that can hold a sandbox forever.
    /// </summary>
    internal static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromMinutes(30);

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
    /// test-only entry to the registry (#692 rules out extension by arbitrary tool).
    /// </summary>
    internal static ISecurityScanner Select(string tool) => ScannerRegistry.Resolve(tool);

    /// <summary>
    /// Everything that needs egress, done before the boundary matters: the plan is validated
    /// and its pinned image is pulled into the local image store. Nothing here runs inside the
    /// isolated sandbox, which is what makes the pull possible at all -- a scan sandbox cannot
    /// reach a registry, by design.
    /// </summary>
    /// <returns>The validated plan, which <see cref="Run"/> requires.</returns>
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
        // The pull is the preparation. A pinned reference is what makes it meaningful: pulling
        // a floating tag here and running it later would be two different images, which is why
        // Validate refuses one above.
        var pulled = backend(["pull", plan.Image.Reference], PullTimeout, SandboxConsoleMode.Stream);
        if (pulled.Code != 0)
            throw new WipException(
                $"Scanner {scanner.Name}: pulling {plan.Image.Reference} failed (exit {pulled.Code}); " +
                "no scan was started, because an isolated sandbox cannot fetch it later");
        return plan;
    }

    /// <summary>
    /// Runs the prepared plan inside the sandbox, behind its declared boundary, and collects
    /// the report. Stops and cleans up whatever happens.
    /// </summary>
    /// <param name="plan">
    /// The plan <see cref="Prepare"/> returned. Required rather than re-derived, so a caller
    /// cannot reach Run without having gone through preparation -- and re-validated here,
    /// because being handed a plan is not evidence that it is a valid one.
    /// </param>
    internal ScanOutcome Run(ISecurityScanner scanner, string sandbox, ScanPlan plan, TimeSpan? timeout = null)
    {
        var definition = Definition(sandbox);
        var declared = RequireDeclaredNetwork(definition, sandbox);
        plan.Validate(scanner.Name);
        // Re-checked here for the reason the plan is re-validated: a caller can reach Run with
        // a plan it built itself, so neither the plan nor the pairing is taken on trust.
        RequirePinnedImage(definition, sandbox, plan);

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
        var status = sandboxes.Status(sandbox);
        if (status.Id is null || status.State != "running")
            throw new WipException($"Sandbox {sandbox}: is not running; run sandbox create before a scan");
        if (status.Networks.Count != 1 || status.Networks[0] != network)
            throw new WipException(
                $"Sandbox {sandbox}: is not attached to exactly its declared network; no scan was started. " +
                "Run sandbox destroy, confirm absence, then sandbox create.");

        try
        {
            var code = sandboxes.Exec(sandbox, plan.Argv, deadline);
            var report = ReadReport(sandbox, plan.ReportPath);
            return new(scanner.Name, sandbox, plan.Image.Reference, plan.RulesVersion, network, code, report);
        }
        finally
        {
            // Always, and only the stop: the container's own stop takes the tool and whatever
            // it spawned with it, which is what "子プロセスを含む停止" needs. Removal is
            // `sandbox destroy`'s, so a failed scan leaves the sandbox inspectable.
            Stop(sandbox);
        }
    }

    /// <summary>
    /// The declared network's name, or a refusal. A sandbox with no boundary declared is not
    /// a sandbox a scan may run in: there is no unrestricted mode to fall back to.
    /// </summary>
    /// <summary>
    /// Refuses a sandbox whose declared image is not the one the plan pinned.
    /// </summary>
    /// <remarks>
    /// Without this the pin guarantees nothing. The scan runs as an exec inside the declared
    /// sandbox, so the tool that actually executes is whatever that sandbox's image provides;
    /// pulling the plan's image would leave it unused, and the outcome would name an image
    /// that had not run. Binding the two is what makes "which tool ran" answerable, so the
    /// scanner's image belongs in the sandbox declaration and the pull keeps it current.
    /// </remarks>
    private static void RequirePinnedImage(SandboxDefinition definition, string sandbox, ScanPlan plan)
    {
        if (string.Equals(definition.Image, plan.Image.Reference, StringComparison.Ordinal)) return;
        throw new ConfigException(
            $"Sandbox {sandbox}: declares image '{definition.Image}', but the tool pins " +
            $"'{plan.Image.Reference}'. The scan runs inside this sandbox, so its declaration must name " +
            "the pinned image -- otherwise the tool that runs is not the one that was pinned.");
    }

    private static string RequireDeclaredNetwork(SandboxDefinition definition, string sandbox) =>
        definition.Network
        ?? throw new ConfigException(
            $"Sandbox {sandbox}: declares no network; a scan requires an internal network, " +
            $"so add a networks: entry with internal: true and reference it from sandboxes.{sandbox}.network");

    /// <summary>
    /// The report as the tool left it, or null when there is none. Read as bytes through the
    /// sandbox rather than trusted as text: the content is a tool's output, which
    /// slidict/workspace#690 treats as untrusted input, and nothing here interprets it.
    /// </summary>
    private string? ReadReport(string sandbox, string reportPath)
    {
        // `cat` with the path as its own argument, never a shell line: a report path is data,
        // and composing it into `sh -c` would make it executable.
        var captured = sandboxes.Capture(sandbox, ["cat", reportPath], DefaultScanTimeout);
        return captured.Code == 0 ? captured.Output : null;
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
