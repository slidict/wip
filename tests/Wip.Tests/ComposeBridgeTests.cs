using Wip.Compose;

namespace Wip.Tests;

/// <summary>
/// Covers <see cref="ComposeBridge.Ps"/>'s argv shape, added for <c>wip ps</c>/<c>wip status</c>
/// under <c>mode: compose</c>. <c>compose ps</c> is close to universal across compose-for-wslc
/// implementations, unlike <c>restart</c>/<c>pull</c>, which is why only this method got added.
/// </summary>
public class ComposeBridgeTests
{
    [Fact]
    public void PsIncludesFileAndProject()
    {
        var bridge = new ComposeBridge("wslc-compose", "compose.yml", "myproject");

        Assert.Equal(
            new[] { "wslc-compose", "-f", "compose.yml", "-p", "myproject", "ps" },
            bridge.Ps());
    }

    [Fact]
    public void PsOmitsProjectWhenNotConfigured()
    {
        var bridge = new ComposeBridge("wslc-compose", "compose.yml");

        Assert.Equal(new[] { "wslc-compose", "-f", "compose.yml", "ps" }, bridge.Ps());
    }
}
