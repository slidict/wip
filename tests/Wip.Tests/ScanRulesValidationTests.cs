using Wip.Security;

namespace Wip.Tests;

public class ScanRulesValidationTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private static ScanPlan Plan(PinnedRules? rules) => new(
        new PinnedImage("example.test/scanner@sha256:" + new string('a', 64)),
        ["scanner"], "/tmp/report.json", "rules-v1", "/tmp/coverage.txt", rules);

    [Fact]
    public void OmittedRulesKeepTheExistingPlanContract()
    {
        var plan = Plan(null);

        plan.Validate("fixture");

        Assert.Null(plan.Rules);
    }

    [Fact]
    public void ACompleteHttpsCommitPinAndCanonicalPathAreAccepted()
    {
        var rules = new PinnedRules("https://rules.test/catalog.git", Commit, "/opt/wip-rules");
        var plan = Plan(rules);

        plan.Validate("fixture");

        Assert.Equal(rules, plan.Rules);
    }

    [Theory]
    [InlineData("http://rules.test/catalog.git")]
    [InlineData("file:///tmp/catalog")]
    [InlineData("catalog.git")]
    [InlineData("https://user:password@rules.test/catalog.git")]
    [InlineData("https://rules.test/catalog.git\n")]
    [InlineData("")]
    public void InvalidRepositorySourcesAreRejectedByPlanValidation(string repositoryUrl)
    {
        var error = Assert.Throws<ConfigException>(() =>
            Plan(new PinnedRules(repositoryUrl, Commit, "/scanner/rules")).Validate("fixture"));

        Assert.Contains("rules repository", error.Message);
        Assert.Contains("HTTPS", error.Message);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release-1")]
    [InlineData("0123456")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("0123456789abcdef0123456789abcdef012345678")]
    [InlineData("")]
    public void OnlyAnExactLowercaseCommitIsAPin(string commit)
    {
        var error = Assert.Throws<ConfigException>(() =>
            Plan(new PinnedRules("https://rules.test/catalog.git", commit, "/scanner/rules")).Validate("fixture"));

        Assert.Contains("40 lowercase hexadecimal", error.Message);
        Assert.Contains("pin", error.Message);
    }

    [Fact]
    public void RulesStagingCannotReplaceTheContainerRoot()
    {
        var error = Assert.Throws<ConfigException>(() =>
            Plan(new PinnedRules("https://rules.test/catalog.git", Commit, "/")).Validate("fixture"));

        Assert.Contains("cannot replace the container root", error.Message);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/usr")]
    [InlineData("/rules")]
    public void RulesCannotReplaceATopLevelContainerDirectory(string containerPath)
    {
        var error = Assert.Throws<ConfigException>(() =>
            Plan(new PinnedRules("https://rules.test/catalog.git", Commit, containerPath)).Validate("fixture"));

        Assert.Contains("at least two", error.Message);
        Assert.Contains("top-level", error.Message);
    }

    [Theory]
    [InlineData("rules")]
    [InlineData("/scanner/../rules")]
    [InlineData("/scanner/./rules")]
    [InlineData("/scanner//rules")]
    [InlineData("/scanner/rules/")]
    [InlineData("/scanner/rules\n")]
    [InlineData("")]
    public void InvalidContainerPathsAreRejectedBeforeExecution(string containerPath)
    {
        var error = Assert.Throws<ConfigException>(() =>
            Plan(new PinnedRules("https://rules.test/catalog.git", Commit, containerPath)).Validate("fixture"));

        Assert.Contains("rules container path", error.Message);
        Assert.Contains("canonical absolute Linux", error.Message);
    }
}
