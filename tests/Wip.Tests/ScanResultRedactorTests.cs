using Wip.Security;

namespace Wip.Tests;

public sealed class ScanResultRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer header-secret", "header-secret")]
    [InlineData("Cookie: sid=cookie-secret", "cookie-secret")]
    [InlineData("Set-Cookie: session=cookie-secret; HttpOnly", "cookie-secret")]
    [InlineData("Proxy-Authorization: Basic basic-secret", "basic-secret")]
    [InlineData("https://target/path?access_token=query-secret&safe=yes", "query-secret")]
    [InlineData("api_key=key-secret", "key-secret")]
    [InlineData("password: password-secret", "password-secret")]
    [InlineData("{\"token\":\"json-secret\"}", "json-secret")]
    [InlineData("Bearer standalone-secret", "standalone-secret")]
    [InlineData("https://target/path?q=0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef")]
    public void SecretShapesAreReplacedWithAFixedMarker(string evidence, string secret)
    {
        var redacted = ScanResultRedactor.Redact(evidence);
        Assert.Contains(ScanResultRedactor.Marker, redacted);
        Assert.DoesNotContain(secret, redacted);
    }

    [Theory]
    [InlineData("access_token", "aStandaloneSecretTokenValue")]
    [InlineData("Authorization", "short-credential")]
    [InlineData("Cookie", "short-cookie")]
    [InlineData("q", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature")]
    [InlineData("q", "aStandaloneSecretTokenValueWithMixedCase")]
    public void EvidenceUsesTheParameterContextAndRecognizesStandaloneTokens(string parameter, string evidence)
    {
        Assert.Equal(ScanResultRedactor.Marker, ScanResultRedactor.RedactEvidence(evidence, parameter));
    }

    [Theory]
    [InlineData("tracking=0123456789abcdef0123456789abcdef&plain=ok")]
    [InlineData("request tracking=0123456789abcdef0123456789abcdef plain=ok")]
    [InlineData("?tracking=0123456789abcdef0123456789abcdef&plain=ok")]
    public void TokenShapedAssignmentValuesAreMaskedWithoutMaskingOrdinaryValues(string evidence)
    {
        var redacted = ScanResultRedactor.RedactEvidence(evidence, "tracking");
        Assert.Contains("tracking=" + ScanResultRedactor.Marker, redacted);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", redacted);
        Assert.Contains("plain=ok", redacted);
    }

    [Fact]
    public void OrdinaryEvidenceIsPreserved()
    {
        const string evidence = "<p>missing header</p> GET /page?q=hello";
        Assert.Equal(evidence, ScanResultRedactor.Redact(evidence));
    }
}
