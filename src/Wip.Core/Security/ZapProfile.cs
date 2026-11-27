namespace Wip.Security;

/// <summary>
/// The caller-selectable bounds of the ZAP profile. The defaults are the profile this
/// repository's own CI budget was sized for, so a caller who passes nothing gets exactly that.
/// </summary>
/// <remarks>
/// Every bound has a ceiling and none can be zero. ZAP reads zero as "unlimited" for these
/// limits, and an application with generated URLs (pagination, locale prefixes, per-record
/// pages) can keep an unlimited spider or scan walking effectively forever.
/// </remarks>
/// <param name="SpiderMinutes">Spider <c>maxDuration</c>, in minutes.</param>
/// <param name="SpiderDepth">Spider <c>maxDepth</c>.</param>
/// <param name="SpiderChildren">Spider <c>maxChildren</c> per node.</param>
/// <param name="PassiveWaitMinutes"><c>passiveScan-wait</c> <c>maxDuration</c>, in minutes.</param>
/// <param name="ActiveScan">Whether the <c>activeScan</c> job runs. Off unless asked for.</param>
/// <param name="ActiveScanMinutes">
/// <c>activeScan</c> <c>maxScanDurationInMins</c>. Validated even when the active scan is
/// off, so an out-of-range value is never accepted silently.
/// </param>
/// <param name="Login">The form login the spider and the active scan run as, or null for none.</param>
internal sealed record ZapProfile(
    int SpiderMinutes = ZapProfile.DefaultSpiderMinutes,
    int SpiderDepth = ZapProfile.DefaultSpiderDepth,
    int SpiderChildren = ZapProfile.DefaultSpiderChildren,
    int PassiveWaitMinutes = ZapProfile.DefaultPassiveWaitMinutes,
    bool ActiveScan = false,
    int ActiveScanMinutes = ZapProfile.DefaultActiveScanMinutes,
    ZapLogin? Login = null)
{
    internal const int DefaultSpiderMinutes = 1;
    internal const int DefaultSpiderDepth = 2;
    internal const int DefaultSpiderChildren = 10;
    internal const int DefaultPassiveWaitMinutes = 1;
    internal const int DefaultActiveScanMinutes = 5;

    internal const int MaxSpiderMinutes = 30;
    internal const int MaxSpiderDepth = 10;
    internal const int MaxSpiderChildren = 100;
    internal const int MaxPassiveWaitMinutes = 30;
    internal const int MaxActiveScanMinutes = 60;

    // Option names live here so that an error names the flag the caller typed.
    internal const string SpiderMinutesOption = "--zap-spider-minutes";
    internal const string SpiderDepthOption = "--zap-spider-depth";
    internal const string SpiderChildrenOption = "--zap-spider-children";
    internal const string PassiveWaitMinutesOption = "--zap-passive-wait-minutes";
    internal const string ActiveScanOption = "--zap-active-scan";
    internal const string ActiveScanMinutesOption = "--zap-active-scan-minutes";

    internal static ZapProfile Default { get; } = new();

    /// <summary>
    /// The rules identifier suffix: it names the profile, not its numeric bounds. A login gets
    /// its own names, so authenticated and unauthenticated results are never claimed comparable.
    /// </summary>
    internal string Identifier => (ActiveScan, Login is null) switch
    {
        (true, true) => "wip-active-v1",
        (true, false) => "wip-active-auth-v1",
        (false, true) => "wip-passive-v2",
        (false, false) => "wip-passive-auth-v1",
    };

    /// <summary>
    /// The longest the bounded jobs can take, in minutes, excluding ZAP start-up and the
    /// report. A scan deadline shorter than this ends the run before the report is written.
    /// </summary>
    internal int BoundedMinutes => SpiderMinutes + PassiveWaitMinutes + (ActiveScan ? ActiveScanMinutes : 0);

    public void Validate()
    {
        Require(SpiderMinutesOption, SpiderMinutes, MaxSpiderMinutes);
        Require(SpiderDepthOption, SpiderDepth, MaxSpiderDepth);
        Require(SpiderChildrenOption, SpiderChildren, MaxSpiderChildren);
        Require(PassiveWaitMinutesOption, PassiveWaitMinutes, MaxPassiveWaitMinutes);
        Require(ActiveScanMinutesOption, ActiveScanMinutes, MaxActiveScanMinutes);
        Login?.Validate();
    }

    private static void Require(string option, int value, int max)
    {
        if (value < 1 || value > max)
            throw new ConfigException($"{option} must be between 1 and {max}; got {value}");
    }
}
