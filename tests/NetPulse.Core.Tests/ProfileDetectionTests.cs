using System.Net;
using NetPulse.Core.Detection;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Tests;

public sealed class ProfileTests
{
    internal static Target T(string id, TargetRole role = TargetRole.External, string address = "192.0.2.1", string? provider = null, ProbeMethod? method = null) =>
        new(id, id + " 이름", IPAddress.Parse(address), role, provider ?? id, method ?? ProbeMethod.Icmp);

    internal static Profile P(params Target[] targets) =>
        new("p", "프로필", targets, Profile.DefaultTimeout, Profile.DefaultDuration, ExpectedConnection.Any);

    private static string[] Codes(Profile p) => ProfileValidator.Validate(p).Select(i => i.Code).ToArray();

    [Fact]
    public void GoodProfileHasNoIssues()
    {
        var p = P(T("router", TargetRole.Local, "192.0.2.254"), T("a", provider: "A"), T("b", provider: "B"));
        Assert.Empty(ProfileValidator.Validate(p));
    }

    [Fact]
    public void ErrorsAreReported()
    {
        Assert.Contains("no-targets", Codes(P()));
        Assert.Contains("too-many-targets", Codes(P(Enumerable.Range(0, 9).Select(i => T("t" + i)).ToArray())));
        Assert.Contains("bad-id", Codes(P(T("bad id"))));
        Assert.Contains("not-ipv4", Codes(P(new Target("v6", "v6", IPAddress.IPv6Loopback, TargetRole.External, "x", ProbeMethod.Icmp))));
        Assert.Contains("bad-port", Codes(P(T("tcp", method: ProbeMethod.Tcp(70000)))));
        Assert.Contains("no-name", Codes(P(new Target("n", " ", IPAddress.Loopback, TargetRole.External, "x", ProbeMethod.Icmp))));
        Assert.Contains("duplicate-id", Codes(P(T("a"), T("A"))));
        Assert.Contains("timeout-range", Codes(P(T("a")) with { Timeout = TimeSpan.FromMilliseconds(100) }));
        Assert.Contains("duration-range", Codes(P(T("a")) with { Duration = TimeSpan.FromHours(13) }));
    }

    [Fact]
    public void DisabledTargetsDoNotCountButAreStillValidated()
    {
        var off = T("off") with { Enabled = false };
        Assert.Contains("no-targets", Codes(P(off)));
        Assert.Contains("bad-id", Codes(P(T("a"), T("b b") with { Enabled = false })));
    }

    [Fact]
    public void WarningsDoNotBlock()
    {
        var issues = ProfileValidator.Validate(P(T("a", provider: "Same"), T("b", provider: "same")));
        Assert.False(ProfileValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Code == "external-diversity");
        Assert.Contains(issues, i => i.Code == "no-local");
    }

    [Fact]
    public void StoreRoundTripsAndHandlesDamage()
    {
        var dir = Directory.CreateTempSubdirectory("np-profile-").FullName;
        try
        {
            var now = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
            Assert.Equal(ProfileLoadStatus.Missing, ProfileStore.Load(dir, now).Status);

            var p = P(T("router", TargetRole.Local, "192.0.2.254"), T("tcp", method: ProbeMethod.Tcp(443)), T("off") with { Enabled = false });
            ProfileStore.Save(dir, new ProfileSet([p], "p"));
            var (set, status, moved) = ProfileStore.Load(dir, now);
            Assert.Equal(ProfileLoadStatus.Loaded, status);
            Assert.Null(moved);
            var back = Assert.Single(set!.Profiles);
            Assert.Equal(p.Targets, back.Targets);
            Assert.Equal(p.Timeout, back.Timeout);
            Assert.Equal("p", set.LastUsedId);

            File.WriteAllText(Path.Combine(dir, ProfileStore.FileName), "{ 망가진");
            var damaged = ProfileStore.Load(dir, now);
            Assert.Equal(ProfileLoadStatus.Unreadable, damaged.Status);
            Assert.True(File.Exists(Path.Combine(dir, damaged.MovedTo!)), "손상 파일은 지우지 않고 옮겨 둔다 (경로가 아닌 파일 이름만 돌려준다)");
            Assert.False(File.Exists(Path.Combine(dir, ProfileStore.FileName)));

            File.WriteAllText(Path.Combine(dir, ProfileStore.FileName), """{ "version": 99, "profiles": [] }""");
            Assert.Equal(ProfileLoadStatus.UnknownVersion, ProfileStore.Load(dir, now.AddSeconds(1)).Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("icmp", ProbeMethodKind.Icmp, 0)]
    [InlineData("tcp:443", ProbeMethodKind.Tcp, 443)]
    public void MethodTextParses(string text, ProbeMethodKind kind, int port)
    {
        var m = ProfileStore.ParseMethod(text);
        Assert.Equal(kind, m.Kind);
        if (kind == ProbeMethodKind.Tcp) Assert.Equal(port, m.Port);
        Assert.Equal(text, m.ToString());
    }
}

public sealed class DetectionTests
{
    private static readonly IPAddress Router = IPAddress.Parse("192.0.2.254");

    private static NetworkSnapshot Net(BestRoute? route, string? error = null, params InterfaceInfo[] nics) => new(nics, route, error);

    [Fact]
    public void OkProposesRouterAndTwoExternals()
    {
        var r = DetectionPlanner.Plan(Net(new BestRoute(5, Router), null, new InterfaceInfo(5, InterfaceKind.Wireless, Tristate.No, true)));
        Assert.Equal(DetectionStatus.Ok, r.Status);
        Assert.Null(r.Notice);
        Assert.Equal(InterfaceKind.Wireless, r.Kind);
        Assert.Equal(ExpectedConnection.Wireless, r.Proposal!.Expected);
        var router = Assert.Single(r.Proposal.Targets, t => t.Role == TargetRole.Local);
        Assert.Equal(Router, router.Address);
        Assert.Equal(2, r.Proposal.Targets.Count(t => t.Role == TargetRole.External));
        Assert.False(ProfileValidator.HasErrors(ProfileValidator.Validate(r.Proposal)));
        Assert.Empty(ProfileValidator.Validate(r.Proposal));
    }

    [Fact]
    public void WiredSetsExpectation()
    {
        var r = DetectionPlanner.Plan(Net(new BestRoute(3, Router), null, new InterfaceInfo(3, InterfaceKind.Wired, Tristate.No, true)));
        Assert.Equal(ExpectedConnection.Wired, r.Proposal!.Expected);
    }

    [Fact]
    public void NoNetwork()
    {
        var r = DetectionPlanner.Plan(Net(null));
        Assert.Equal(DetectionStatus.NoNetwork, r.Status);
        Assert.Null(r.Proposal);
    }

    [Fact]
    public void NoIpv4RouteStillProposesExternals()
    {
        var r = DetectionPlanner.Plan(Net(null, null, new InterfaceInfo(2, InterfaceKind.Wired, Tristate.No, false)));
        Assert.Equal(DetectionStatus.NoIpv4Route, r.Status);
        Assert.DoesNotContain(r.Proposal!.Targets, t => t.Role == TargetRole.Local);
        Assert.NotNull(r.Notice);
    }

    [Fact]
    public void RouteQueryFailureIsUnknown()
    {
        var r = DetectionPlanner.Plan(Net(null, "GetBestRoute_5", new InterfaceInfo(2, InterfaceKind.Wired, Tristate.No, true)));
        Assert.Equal(DetectionStatus.Unknown, r.Status);
        Assert.Contains("GetBestRoute_5", r.Notice);
    }

    [Fact]
    public void OnLinkRouteIsNoGateway()
    {
        var r = DetectionPlanner.Plan(Net(new BestRoute(9, null), null, new InterfaceInfo(9, InterfaceKind.Tunnel, Tristate.Yes, false)));
        Assert.Equal(DetectionStatus.NoGateway, r.Status);
        Assert.DoesNotContain(r.Proposal!.Targets, t => t.Role == TargetRole.Local);
        Assert.Equal(Tristate.Yes, r.Virtual);
    }

    [Theory]
    [InlineData(Tristate.Yes, true)]
    [InlineData(Tristate.No, false)]
    [InlineData(Tristate.Unknown, false)]
    public void VirtualRouteIsFlagged(Tristate virt, bool notice)
    {
        var r = DetectionPlanner.Plan(Net(new BestRoute(4, Router), null, new InterfaceInfo(4, InterfaceKind.Other, virt, true)));
        Assert.Equal(DetectionStatus.Ok, r.Status);
        Assert.Equal(virt, r.Virtual);
        Assert.Equal(notice, r.Notice is not null);
    }

    [Fact]
    public void OtherGatewaysAreCounted()
    {
        var r = DetectionPlanner.Plan(Net(new BestRoute(4, Router), null,
            new InterfaceInfo(4, InterfaceKind.Wired, Tristate.No, true), new InterfaceInfo(7, InterfaceKind.Wireless, Tristate.No, true),
            new InterfaceInfo(8, InterfaceKind.Other, Tristate.Yes, false)));
        Assert.Equal(1, r.OtherGateways);
        Assert.Contains("1개", r.Notice);
    }

    [Fact]
    public void RouteOnUnknownInterfaceIsOther()
    {
        var r = DetectionPlanner.Plan(Net(new BestRoute(42, Router), null, new InterfaceInfo(1, InterfaceKind.Wired, Tristate.No, false)));
        Assert.Equal(DetectionStatus.Ok, r.Status);
        Assert.Equal(InterfaceKind.Other, r.Kind);
        Assert.Equal(Tristate.Unknown, r.Virtual);
        Assert.Equal(ExpectedConnection.Any, r.Proposal!.Expected);
    }
}
