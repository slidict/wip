using System.Text.Json.Nodes;
using Wip.Platform;

namespace Wip.Tests;

public class ShellwordsTests
{
    public static IEnumerable<object[]> Cases() => GoldenCorpus.UnitCases("shellwords", arrayProperty: null);

    [Theory]
    [MemberData(nameof(Cases))]
    public void SplitsTheSameWayRubyDid(string caseJson)
    {
        var entry = JsonNode.Parse(caseJson)!.AsObject();
        var input = entry["input"]!.GetValue<string>();
        var result = entry["result"]!.AsObject();

        if (result.TryGetPropertyValue("error", out var error))
        {
            var thrown = Assert.Throws<ConfigException>(() => Shellwords.Split(input));
            Assert.Equal(error!.GetValue<string>(), thrown.Message);
            return;
        }

        var expected = result["ok"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        Assert.Equal(expected, Shellwords.Split(input));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("with space")]
    [InlineData("")]
    [InlineData("quote\"inside")]
    [InlineData("single'quote")]
    [InlineData("dollar$sign")]
    [InlineData("back\\slash")]
    public void JoinRoundTripsThroughSplit(string word)
    {
        Assert.Equal([word], Shellwords.Split(Shellwords.Join([word])));
    }
}
