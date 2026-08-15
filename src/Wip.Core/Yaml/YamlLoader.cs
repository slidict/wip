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
    public static object? LoadFile(string path, bool allowAliases)
    {
        using var reader = new StreamReader(path);
        return Load(reader, allowAliases, path);
    }

    public static object? LoadText(string text, bool allowAliases, string path = "<text>")
    {
        using var reader = new StringReader(text);
        return Load(reader, allowAliases, path);
    }

    private static object? Load(TextReader reader, bool allowAliases, string path)
    {
        var parser = new Parser(reader);
        var anchors = new Dictionary<string, object?>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            parser.Consume<StreamStart>();
            if (parser.Accept<StreamEnd>(out _))
            {
                return null;
            }

            parser.Consume<DocumentStart>();
            var value = ReadNode(parser, allowAliases, anchors, pending, path);
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
        string path)
    {
        if (parser.Accept<AnchorAlias>(out var alias))
        {
            parser.MoveNext();
            return ResolveAlias(alias!, allowAliases, anchors, pending, path);
        }

        if (parser.Accept<Scalar>(out var scalar))
        {
            parser.MoveNext();
            var value = YamlScalarResolver.Resolve(scalar!.Value, scalar.Style);
            Remember(scalar.Anchor, value, anchors);
            return value;
        }

        if (parser.Accept<SequenceStart>(out var sequenceStart))
        {
            return ReadSequence(parser, allowAliases, anchors, pending, path, sequenceStart!.Anchor);
        }

        if (parser.Accept<MappingStart>(out var mappingStart))
        {
            return ReadMapping(parser, allowAliases, anchors, pending, path, mappingStart!.Anchor);
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
        string path,
        AnchorName anchor)
    {
        parser.Consume<SequenceStart>();
        var items = new List<object?>();
        Track(anchor, pending);

        while (!parser.Accept<SequenceEnd>(out _))
        {
            items.Add(ReadNode(parser, allowAliases, anchors, pending, path));
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
        string path,
        AnchorName anchor)
    {
        parser.Consume<MappingStart>();
        var mapping = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        Track(anchor, pending);

        while (!parser.Accept<MappingEnd>(out _))
        {
            var key = ReadNode(parser, allowAliases, anchors, pending, path);
            var value = ReadNode(parser, allowAliases, anchors, pending, path);
            var name = RubyValue.ToStringValue(key);

            if (name == MergeKey && TryMerge(mapping, value))
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
    private static bool TryMerge(OrderedDictionary<string, object?> target, object? value)
    {
        if (RubyValue.AsMapping(value) is { } single)
        {
            MergeInto(target, single);
            return true;
        }

        if (RubyValue.AsSequence(value) is not { } sequence ||
            sequence.Any(item => RubyValue.AsMapping(item) is null))
        {
            return false;
        }

        for (var index = sequence.Count - 1; index >= 0; index--)
        {
            MergeInto(target, RubyValue.AsMapping(sequence[index])!);
        }

        return true;
    }

    /// <summary>
    /// An existing key keeps its position but takes the new value; a new key is appended.
    /// Position matters because it becomes the order of the generated command line.
    /// </summary>
    private static void MergeInto(OrderedDictionary<string, object?> target, OrderedDictionary<string, object?> source)
    {
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
}
