using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Wip.Yaml;

/// <summary>
/// Emits the loosely typed trees <see cref="YamlLoader"/> produces back as YAML, for
/// <c>wip config</c>.
/// </summary>
/// <remarks>
/// Driving the emitter with explicit events keeps this reflection-free, the same reason the
/// loader drives the parser directly. Quoting is decided by asking
/// <see cref="YamlScalarResolver"/> whether the text would read back as something other than
/// a string — so "no", "3000", and "" come out quoted, and reloading the output produces the
/// document that was printed.
/// </remarks>
public static class YamlWriter
{
    public static string Dump(object? value)
    {
        var builder = new StringBuilder();
        using (var writer = new StringWriter(builder))
        {
            var emitter = new Emitter(writer);
            emitter.Emit(new StreamStart());
            emitter.Emit(new DocumentStart(null, null, isImplicit: false));
            Write(emitter, value);
            emitter.Emit(new DocumentEnd(isImplicit: true));
            emitter.Emit(new StreamEnd());
        }

        return builder.ToString();
    }

    private static void Write(IEmitter emitter, object? value)
    {
        switch (value)
        {
            case OrderedDictionary<string, object?> mapping:
                emitter.Emit(new MappingStart(null, null, isImplicit: true, MappingStyle.Block));
                foreach (var (key, item) in mapping)
                {
                    WriteScalar(emitter, key);
                    Write(emitter, item);
                }

                emitter.Emit(new MappingEnd());
                return;

            case List<object?> sequence:
                emitter.Emit(new SequenceStart(null, null, isImplicit: true, SequenceStyle.Block));
                foreach (var item in sequence)
                {
                    Write(emitter, item);
                }

                emitter.Emit(new SequenceEnd());
                return;

            case null:
                // Written as '~' rather than as an empty scalar: the emitter renders an empty
                // plain scalar as '', which would read back as the empty string instead of
                // null and stop `wip config` output from round-tripping.
                Emit(emitter, "~", ScalarStyle.Plain);
                return;

            case bool flag:
                Emit(emitter, flag ? "true" : "false", ScalarStyle.Plain);
                return;

            case string text:
                WriteScalar(emitter, text);
                return;

            default:
                Emit(emitter, RubyValue.ToStringValue(value), ScalarStyle.Plain);
                return;
        }
    }

    private static void WriteScalar(IEmitter emitter, string text)
    {
        // If reading this text back plain would produce anything but the same string, it has
        // to be quoted for the document to round-trip.
        var resolved = YamlScalarResolver.Resolve(text, ScalarStyle.Plain);
        var needsQuotes = resolved is not string same || same != text;
        Emit(emitter, text, needsQuotes ? ScalarStyle.SingleQuoted : ScalarStyle.Any);
    }

    private static void Emit(IEmitter emitter, string value, ScalarStyle style) =>
        emitter.Emit(new Scalar(
            null,
            null,
            value,
            style,
            isPlainImplicit: style is ScalarStyle.Any or ScalarStyle.Plain,
            isQuotedImplicit: style is not (ScalarStyle.Any or ScalarStyle.Plain)));
}
