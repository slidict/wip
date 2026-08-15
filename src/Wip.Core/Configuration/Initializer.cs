using Wip.Compose;

namespace Wip.Configuration;

/// <summary>
/// Builds a starter wip.yml. Detects an existing compose file next to the target — the same
/// filenames <see cref="ComposeBridge"/> auto-detects — to choose between
/// <c>mode: compose-native</c> and <c>mode: container</c>, since that is the one decision
/// <c>wip init</c> cannot leave to a placeholder.
/// </summary>
public sealed class Initializer
{
    /// <summary>--template values accepted, and the label used in the exclude comment.</summary>
    public static readonly OrderedDictionary<string, string> TemplateLabels = new(StringComparer.Ordinal)
    {
        ["rails"] = "Rails",
        ["ruby"] = "Ruby",
        ["node"] = "Node.js",
        ["rust"] = "Rust",
        ["csharp"] = "C#",
    };

    /// <summary>
    /// sync.exclude patterns written for each --template. They mirror that stack's own
    /// github/gitignore template: directories either regenerated inside the container, or too
    /// large or irrelevant to mirror over rsync.
    /// </summary>
    private static readonly Dictionary<string, string[]> TemplateExcludes = new(StringComparer.Ordinal)
    {
        ["rails"] =
        [
            ".git", "log/", "tmp/", "storage/", "public/assets/", "public/packs/", ".bundle/",
            "vendor/bundle/", "coverage/", "node_modules/",
        ],
        // Plain Ruby, for projects that are not Rails: no storage/ or public/ to skip.
        ["ruby"] = [".git", "log/", "tmp/", ".bundle/", "vendor/bundle/", "coverage/"],
        ["node"] = [".git", "node_modules/", "dist/", "build/", ".next/", ".cache/", "coverage/"],
        ["rust"] = [".git", "target/"],
        ["csharp"] = [".git", "bin/", "obj/", ".vs/", "packages/"],
    };

    /// <summary>Used when --template is omitted — the same starting point the README uses.</summary>
    private static readonly string[] FallbackExcludes = [".git", "tmp/", "node_modules/"];

    /// <summary>
    /// Appended to both templates. <c>interaction:</c> itself stays commented, since an empty
    /// <c>interaction: {}</c> is indistinguishable from omitting the key.
    /// </summary>
    private const string CommandsExample = """
        # optional; custom subcommands, e.g. `wip test` — see README
        # (interaction: is the primary key; commands: is accepted as an alias — the two are
        # mutually exclusive, so pick one)
        # interaction:
        #   test:
        #     optional; exec (default, runs in the running container) | run (fresh container) | build
        #     type: exec

        #     command: ls
        """;

    private readonly string directory;
    private readonly string? template;
    private readonly string? composeFile;

    public Initializer(string? directory = null, string? template = null)
    {
        if (template is not null && !TemplateLabels.ContainsKey(template))
        {
            throw new WipException(
                $"unknown --template \"{template}\" (valid: {string.Join(", ", TemplateLabels.Keys)})");
        }

        this.directory = directory ?? Directory.GetCurrentDirectory();
        this.template = template;
        composeFile = ComposeBridge.Filenames.FirstOrDefault(
            name => File.Exists(Path.Combine(this.directory, name)));
    }

    public bool IsCompose => composeFile is not null;

    public string Call() => IsCompose ? ComposeTemplate() : ContainerTemplate();

    /// <summary>
    /// The directory name wip.yml would resolve as its default <c>compose.project</c>, and
    /// from that its network name, if left unset. Shown as a comment so it is discoverable
    /// without duplicating it as a live value that would go stale if the directory moved.
    /// </summary>
    private string ComposeProjectDefault => new DirectoryInfo(Path.GetFullPath(directory)).Name;

    private string ExcludeComment => template is null
        ? "# optional; rsync --exclude patterns, e.g. [\"node_modules\", \"*.log\"]"
        : $"# optional; rsync --exclude patterns picked for --template {template} ({TemplateLabels[template]})";

    private string ExcludeList => string.Join(
        "\n",
        (template is null ? FallbackExcludes : TemplateExcludes[template]).Select(pattern => $"  - {pattern}"));

    /// <summary>
    /// Appended to <c>sync:</c> in both templates. Every key here is either a plain constant
    /// default, safe to state literally, or — where commented — a value SyncSettings derives
    /// from another key (target from workdir, volume from the container name). Those stay
    /// unset so they keep tracking that key instead of going stale.
    /// </summary>
    private string SyncExtras()
    {
        var body = $"""
            # optional; host path to mirror (default: wip.yml directory)
            source: .

            # optional; in-container mount point for the mirror (default: the container's workdir, else /app)
            # target: /app

            # optional; read-only host mount inside the container
            mount: /host-src

            # optional; named volume holding the mirrored tree (default: "<container>-src")
            # volume: app-src

            # optional; rsync --delete so removals on the host are mirrored too
            delete: true

            {ExcludeComment}
            exclude:
            {ExcludeList}

            # optional; mirror binary to shell out to
            command: rsync

            # optional; extra flags appended to the mirror command
            options: []

            # optional; seconds between mirrors
            interval: 2

            # optional; exec (default, mirrors into the running container) | run (always a fresh container)
            mode: exec

            # sync.image/sync.build — required for mode: compose and used by sync.mode: run; see README "Source sync"
            """;

        // Only non-blank lines are indented, so the separators between keys stay real blank
        // lines rather than lines of trailing whitespace.
        return string.Join("\n", body.Split('\n').Select(line => line.Length == 0 ? line : $"  {line}"));
    }

    // $$ raises the interpolation delimiter to {{ }}, so the YAML's own single braces
    // (env: {}, the {FOO: bar} example) stay literal without doubling every one.
    private string ContainerTemplate() => $$"""
        version: 1

        # container | compose | compose-native (see README)
        mode: container

        # TODO: rename freely, as long as it matches a key under dependencies: below
        container: app

        wslc:
          # optional; wslc binary/path wip shells out to ("auto" resolves it for you)
          command: auto

        # optional; container network name (mode: container only)
        # network: my-network

        dependencies:
          # this is the container wip creates, execs into, and runs commands in
          app:
            # TODO: image to run
            image: your/image:tag

            # TODO: adjust to match your image, or delete this line
            workdir: /app

            # optional; keep stdin open / allocate a tty
            interactive: false

            # optional; remove the container after each run
            remove: true

            # optional; restart policy `wip up --watch` polls for — always/unless-stopped/on-failure
            # restart an exited container; "no" (the default) never does — see README
            restart: "no"

            # optional; environment variables passed to the container, e.g. {FOO: bar}
            env: {}

            # optional; published ports, e.g. ["3000:3000"]
            ports: []

            # optional; extra -v specs beyond the sync mounts below
            volumes: []

        {{CommandsExample}}

        # optional; mirrors the source into a named volume instead of bind-mounting it live
        sync:
        {{SyncExtras()}}

        """;

    private string ComposeTemplate() => $"""
        version: 1

        # wip parses {composeFile} itself and drives wslc directly — no external
        # compose-for-wslc binary needed. Prefer a real compose tool instead? Use
        # mode: compose (see README "Compose mode") and set compose.command.
        mode: compose-native

        wslc:
          # optional; wslc binary/path wip shells out to ("auto" resolves it for you)
          command: auto

        compose:
          # TODO: which service in {composeFile} wip run/exec/NAME target
          service: app

          # optional; override which compose file wip parses (default: auto-detected)
          # file: {composeFile}

          # optional; project/network name (default: this directory's name)
          # project: {ComposeProjectDefault}

          # only used under mode: compose (external compose-for-wslc binary); unused here under compose-native
          # command:

        # network: is derived from compose.project above (or this directory's name) — setting it
        # directly conflicts with compose: (ConfigError), so it's intentionally left out here.

        {CommandsExample}

        # optional; mirrors the source into a named volume instead of bind-mounting it live
        sync:
        {SyncExtras()}

        """;
}
