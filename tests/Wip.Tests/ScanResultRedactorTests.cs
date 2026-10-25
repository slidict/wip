using Wip.Security;

namespace Wip.Tests;

public sealed class ScanResultRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer header-secret", "header-secret")]
    [InlineData("Cookie: sid=cookie-secret", "cookie-secret")]
    [InlineData("{\"Cookie\":\"sid=json-cookie-secret\"}", "json-cookie-secret")]
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

    [Theory]
    [InlineData("http://auth:8080/marker.txt", "http://auth:8080/marker.txt")]
    [InlineData("http://my-auth:8080/app", "http://my-auth:8080/app")]
    [InlineData("http://session:3000/x", "http://session:3000/x")]
    [InlineData("http://api-secret:9000/", "http://api-secret:9000/")]
    [InlineData("http://app:8080/marker.txt", "http://app:8080/marker.txt")]
    [InlineData("Cookie: sid=abc123", "Cookie: [REDACTED]")]
    [InlineData("http://cookie:8080/x", "http://cookie:8080/x")]
    [InlineData("http://authorization:8080/x", "http://authorization:8080/x")]
    public void AliasAuthoritiesRemainIntactWhileCookieHeadersAreRedacted(string input, string expected)
    {
        Assert.Equal(expected, ScanResultRedactor.Redact(input));
    }

    [Theory]
    [InlineData("http://target/reset/0123456789abcdef0123456789abcdef", "http://target/reset/[REDACTED]")]
    [InlineData("/reset/0123456789abcdef0123456789abcdef", "/reset/[REDACTED]")]
    [InlineData("http://auth:8080/reset/0123456789abcdef0123456789abcdef?plain=ok", "http://auth:8080/reset/[REDACTED]?plain=ok")]
    [InlineData("GET /reset/0123456789abcdef0123456789abcdef HTTP/1.1", "GET /reset/[REDACTED] HTTP/1.1")]
    public void TokensInUrlAndLocationPathSegmentsAreRedacted(string input, string expected)
    {
        Assert.Equal(expected, ScanResultRedactor.Redact(input));
    }

    [Theory]
    [InlineData("http://target/reset/account/verification")]
    [InlineData("/reset/account/verification")]
    [InlineData("http://0123456789abcdef0123456789abcdef:8080/ordinary")]
    [InlineData("http://0123456789abcdef0123456789abcdef")]
    [InlineData("image/tool@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    public void OrdinaryPathsAndNonPathIdentifiersArePreserved(string input)
    {
        Assert.Equal(input, ScanResultRedactor.Redact(input));
    }

    [Theory]
    [InlineData("/blog/2026-10-26-how-we-fixed-the-thing")]
    [InlineData("/products/widget-2000-blue-large")]
    [InlineData("/very-long-descriptive-section-name/x")]
    [InlineData("/static/js/main.9b7c2d1e.chunk.js")]
    [InlineData("/assets/index-a3f9b2c1d4e5.js")]
    [InlineData("http://app:8080/2026-10-26-release-notes")]
    [InlineData("auth:8080")]
    [InlineData("auth: 8080")]
    [InlineData("/docs/averylongdescriptivewordwithoutdigits")]
    [InlineData("/t/bWFpbg.cGF5bG9hZA.c2lnbmF0dXJl")]
    public void DescriptivePathsAndNumericAliasesRemainComparable(string input)
    {
        Assert.Equal(input, ScanResultRedactor.Redact(input));
    }

    [Theory]
    [InlineData("/reset/0123456789abcdef0123456789abcdef", "/reset/[REDACTED]")]
    [InlineData("/v/s3cr3tvalue0123456789abcdefgh", "/v/[REDACTED]")]
    [InlineData("/t/eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc", "/t/[REDACTED]")]
    public void PathsWithOpaqueTokenEvidenceStayRedacted(string input, string expected)
    {
        Assert.Equal(expected, ScanResultRedactor.Redact(input));
    }

    [Theory]
    [InlineData("?code=2026-10-26-release-notes", "?code=[REDACTED]")]
    [InlineData("?q=very-long-descriptive-section-name", "?q=[REDACTED]")]
    public void QueryValuesRetainTheirExistingBroaderTokenHeuristic(string input, string expected)
    {
        Assert.Equal(expected, ScanResultRedactor.Redact(input));
        Assert.Equal(expected, ScanResultRedactor.RedactMetadata(input));
    }

    [Fact]
    public void MetadataKeepsTheCallerPathButStillRedactsItsSecretQuery()
    {
        const string target = "http://app:8080/reset/0123456789abcdef0123456789abcdef?token=query-secret&plain=ok";
        Assert.Equal("http://app:8080/reset/0123456789abcdef0123456789abcdef?token=[REDACTED]&plain=ok",
            ScanResultRedactor.RedactMetadata(target));
    }

    /// <summary>
    /// The alias exemption is for a service name and a port, nothing else. Exempting every
    /// numeric value once let these through, and a short digit string is still a credential.
    /// </summary>
    [Theory]
    [InlineData("password: 123456")]
    [InlineData("token: 987654321")]
    [InlineData("session: 20481234")]
    [InlineData("secret: 42")]
    [InlineData("passwd: 0000")]
    [InlineData("auth: 123456")]
    [InlineData("auth: 70000")]
    public void ANumericSecretValueIsStillRedacted(string input)
    {
        var redacted = ScanResultRedactor.Redact(input);
        Assert.EndsWith(ScanResultRedactor.Marker, redacted);
        Assert.DoesNotContain(input.Split(':')[1].Trim(), redacted);
    }

    [Fact]
    public void OrdinaryEvidenceIsPreserved()
    {
        const string evidence = "<p>missing header</p> GET /page?q=hello";
        Assert.Equal(evidence, ScanResultRedactor.Redact(evidence));
    }
}
