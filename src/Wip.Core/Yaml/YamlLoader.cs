using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Wip.Yaml;

/// <summary>
/// Loads YAML into plain <see cref="OrderedDictionary{TKey,TValue}"/>, <see cref="List{T}"/>,
/// string, bool, long, double, and null values.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a deserializer. Mapping documents onto types is what drags reflection —
/// and with it the whole Native AOT problem — into a YAML library, and wip never needed it:
/// the Ruby implementation treated these documents as plain hashes throughout, so the port
/// reads them as dictionaries and keeps the same shape. Driving YamlDotNet's event parser
/// directly also means scalar style is visible, which is what lets quoted "3000" stay a
/// string (see <see cref="YamlScalarResolver"/>).
/// </para>
/// <para>
/// Mapping keys are stringified on the way in, which is what Ruby's <c>Config#stringify</c>
/// did in a separate pass.
/// </para>
/// </remarks>
public static class YamlLoader
{
    // Configuration files do not need arbitrary structural depth. Bounding recursion keeps
    // a malicious repository from turning `wip` startup into a stack-overflow crash.
    private const int MaxNestingDepth = 100;

    /// <summary>Largest accepted document, in UTF-8 bytes, for both file and text input.</summary>
    internal const int MaxInputSize = 1024 * 1024;

    /// <summary>Largest number of nodes one document may parse into.</summary>
    internal const int MaxTotalNodes = 150_000;

    /// <summary>Largest accepted scalar, in characters.</summary>
    internal const int MaxScalarLength = 256 * 1024;

    /// <summary>Largest number of elements one sequence or mapping may hold.</summary>
    internal const int MaxCollectionElements = 50_000;

    /// <summary>
    /// Largest number of entries <c>&lt;&lt;</c> merges may copy across one document.
    /// </summary>
    /// <remarks>
    /// Aliases make a merge source shareable, so the node limits alone do not bound merge work:
    /// <c>&lt;&lt;: [*big, *big, ...]</c> costs one node per copy but copies the whole source
    /// each time, and repeating that across mappings multiplies it again. The copies are what
    /// has to be budgeted.
    /// </remarks>
    internal const int MaxMergeEntries = 1_000_000;

    public static object? LoadFile(string path, bool allowAliases)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxInputSize)
        {
            throw LimitExceeded(path, "file size", MaxInputSize);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var limitedStream = new LimitedReadStream(stream, MaxInputSize, path);
        using var reader = new StreamReader(limitedStream);
        return Load(reader, allowAliases, path);
    }

    public static object? LoadText(string text, bool allowAliases, string path = "<text>")
    {
        // Measured in UTF-8 bytes rather than UTF-16 code units so text and file input are held
        // to the same bound: a document of non-ASCII characters is up to three times the size
        // its character count suggests.
        if (Encoding.UTF8.GetByteCount(text) > MaxInputSize)
        {
            throw LimitExceeded(path, "input size", MaxInputSize);
        }

        using var reader = new StringReader(text);
        return Load(reader, allowAliases, path);
    }

    private static object? Load(TextReader reader, bool allowAliases, string path)
    {
        var parser = new Parser(reader);
        var anchors = new Dictionary<string, object?>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var state = new LoadState();

        try
        {
            parser.Consume<StreamStart>();
            if (parser.Accept<StreamEnd>(out _))
            {
                return null;
            }

            parser.Consume<DocumentStart>();
            var value = ReadNode(parser, allowAliases, anchors, pending, state, path, depth: 0);
            parser.Consume<DocumentEnd>();
            return value;
        }
        catch (YamlException exception)
        {
            throw new ConfigException($"Could not parse {path}: {exception.Message}", exception);
        }
    }

    private static object? ReadNode(
        IParser parser,
        bool allowAliases,
        Dictionary<string, object?> anchors,
        HashSet<string> pending,
        LoadState state,
        string path,
        int depth)
    {
        if (depth > MaxNestingDepth)
        {
            throw new ConfigException(
                $"Could not parse {path}: YAML nesting exceeds the limit of {MaxNestingDepth}");
        }

        state.NodeCount++;
        if (state.NodeCount > MaxTotalNodes)
        {
            throw LimitExceeded(path, "total node count", MaxTotalNodes);
        }

        if (parser.Accept<AnchorAlias>(out var alias))
        {
            parser.MoveNext();
            return ResolveAlias(alias!, allowAliases, anchors, pending, path);
        }

        if (parser.Accept<Scalar>(out var scalar))
        {
            parser.MoveNext();
            if (scalar!.Value.Length > MaxScalarLength)
            {
                throw LimitExceeded(path, "scalar length", MaxScalarLength);
            }
            var value = YamlScalarResolver.Resolve(scalar.Value, scalar.Style);
            Remember(scalar.Anchor, value, anchors);
            return value;
        }

        if (parser.Accept<SequenceStart>(out var sequenceStart))
        {
            return ReadSequence(parser, allowAliases, anchors, pending, state, path, sequenceStart!.Anchor, depth);
        }

        if (parser.Accept<MappingStart>(out var mappingStart))
        {
            return ReadMapping(parser, allowAliases, anchors, pending, state, path, mappingStart!.Anchor, depth);
        }

        throw new ConfigException($"Could not parse {path}: unexpected {parser.Current?.GetType().Name}");
    }

    private static object? ResolveAlias(
        AnchorAlias alias,
        bool allowAliases,
        Dictionary<string, object?> anchors,
        HashSet<string> pending,
        string path)
    {
        var name = alias.Value.ToString();
        if (!allowAliases)
        {
            throw new ConfigException($"Could not parse {path}: YAML aliases are not allowed (found *{name})");
        }

        // An alias inside the node it points at would build a cycle, which every later
        // walk over this tree would have to defend against. Reject it at the source.
        if (pending.Contains(name))
        {
            throw new ConfigException($"{path} contains a self-referential YAML alias");
        }

        if (!anchors.TryGetValue(name, out var value))
        {
            throw new ConfigException($"Could not parse {path}: unknown YAML alias *{name}");
        }

        return value;
    }

    private static List<object?> ReadSequence(
        IParser parser,
        bool allowAliases,
        Dictionary<string, object?> anchors,
        HashSet<string> pending,
        LoadState state,
        string path,
        AnchorName anchor,
        int depth)
    {
        parser.Consume<SequenceStart>();
        var items = new List<object?>();
        Track(anchor, pending);

        while (!parser.Accept<SequenceEnd>(out _))
        {
            if (items.Count >= MaxCollectionElements)
            {
                throw LimitExceeded(path, "sequence element count", MaxCollectionElements);
            }
            items.Add(ReadNode(parser, allowAliases, anchors, pending, state, path, depth + 1));
        }

        parser.Consume<SequenceEnd>();
        Release(anchor, items, anchors, pending);
        return items;
    }

    private static OrderedDictionary<string, object?> ReadMapping(
        IParser parser,
        bool allowAliases,
        Dictionary<string, object?> anchors,
        HashSet<string> pending,
        LoadState state,
        string path,
        AnchorName anchor,
        int depth)
    {
        parser.Consume<MappingStart>();
        var mapping = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        Track(anchor, pending);
        var elementCount = 0;

        while (!parser.Accept<MappingEnd>(out _))
        {
            if (elementCount >= MaxCollectionElements)
            {
                throw LimitExceeded(path, "mapping element count", MaxCollectionElements);
            }
            elementCount++;
            var key = ReadNode(parser, allowAliases, anchors, pending, state, path, depth + 1);
            var value = ReadNode(parser, allowAliases, anchors, pending, state, path, depth + 1);
            var name = RubyValue.ToStringValue(key);

            if (name == MergeKey && TryMerge(mapping, value, state, path))
            {
                continue;
            }

            mapping[name] = value;
        }

        parser.Consume<MappingEnd>();
        Release(anchor, mapping, anchors, pending);
        return mapping;
    }

    /// <summary>YAML's merge key, which folds another mapping's entries into this one.</summary>
    private const string MergeKey = "<<";

    /// <summary>
    /// Applies a <c>&lt;&lt;</c> merge, returning false when the value is not something that can
    /// be merged — in which case it stays an ordinary key, as Psych also left it.
    /// </summary>
    /// <remarks>
    /// The precedence here is Psych's, measured rather than taken from the YAML spec, because
    /// compose.yml files in the wild were written against it. Psych implements the merge as
    /// <c>Hash#merge!</c>, so it is order-sensitive in a way the spec is not: keys written
    /// <em>before</em> the merge key are overwritten by it, while keys written after it win.
    /// A sequence of mappings is merged back to front, which is what makes earlier entries
    /// take precedence over later ones.
    /// </remarks>
    private static bool TryMerge(
        OrderedDictionary<string, object?> target,
        object? value,
        LoadState state,
        string path)
    {
        if (RubyValue.AsMapping(value) is { } single)
        {
            MergeInto(target, single, state, path);
            return true;
        }

        if (RubyValue.AsSequence(value) is not { } sequence ||
            sequence.Any(item => RubyValue.AsMapping(item) is null))
        {
            return false;
        }

        for (var index = sequence.Count - 1; index >= 0; index--)
        {
            MergeInto(target, RubyValue.AsMapping(sequence[index])!, state, path);
        }

        return true;
    }

    /// <summary>
    /// An existing key keeps its position but takes the new value; a new key is appended.
    /// Position matters because it becomes the order of the generated command line.
    /// </summary>
    private static void MergeInto(
        OrderedDictionary<string, object?> target,
        OrderedDictionary<string, object?> source,
        LoadState state,
        string path)
    {
        state.MergedEntries += source.Count;
        if (state.MergedEntries > MaxMergeEntries)
        {
            throw LimitExceeded(path, "merge expansion", MaxMergeEntries);
        }

        foreach (var (key, value) in source)
        {
            target[key] = value;
        }
    }

    private static void Track(AnchorName anchor, HashSet<string> pending)
    {
        if (!anchor.IsEmpty)
        {
            pending.Add(anchor.ToString());
        }
    }

    private static void Release(
        AnchorName anchor,
        object? value,
        Dictionary<string, object?> anchors,
        HashSet<string> pending)
    {
        if (anchor.IsEmpty)
        {
            return;
        }

        var name = anchor.ToString();
        pending.Remove(name);
        anchors[name] = value;
    }

    private static void Remember(AnchorName anchor, object? value, Dictionary<string, object?> anchors)
    {
        if (!anchor.IsEmpty)
        {
            anchors[anchor.ToString()] = value;
        }
    }

    /// <summary>Names the limit and its value, so a caller can say what to shrink.</summary>
    private static ConfigException LimitExceeded(string path, string limit, int maximum) =>
        new($"Could not parse {path}: YAML {limit} exceeds the limit of {maximum}");

    /// <summary>Counters that span a whole document rather than a single node.</summary>
    private sealed class LoadState
    {
        /// <summary>Nodes parsed so far, bounded by <see cref="MaxTotalNodes"/>.</summary>
        public int NodeCount { get; set; }

        /// <summary>Entries copied by merges so far, bounded by <see cref="MaxMergeEntries"/>.</summary>
        public int MergedEntries { get; set; }
    }

    /// <summary>
    /// Stops reading past the byte maximum it is given, so a file that grows between the
    /// <see cref="FileInfo.Length"/> check and the read still cannot exceed the bound.
    /// </summary>
    private sealed class LimitedReadStream(Stream inner, int maximum, string path) : Stream
    {
        private int bytesRead;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => Check(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Check(inner.Read(buffer));
        private int Check(int count)
        {
            bytesRead += count;
            if (bytesRead > maximum)
            {
                throw LimitExceeded(path, "file size", maximum);
            }
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
