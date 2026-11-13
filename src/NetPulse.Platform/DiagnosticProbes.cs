using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using NetPulse.Core.Detection;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;

namespace NetPulse.Platform;

/// <summary>
/// 빠른 진단 검사기 (M4 설계 C1). 제한시간은 조율자(QuickDiagnosis)가 건다. 여기서는 취소에 응하고 결과를 대상별 결과로 바꾼다.
/// HTTPS: HEAD, 리디렉션 안 따름, 쿠키 없음, TLS 1.2·1.3, 인증서 검증 유지.
/// </summary>
public sealed class DiagnosticProbes : IDiagnosticProbes, IDisposable
{
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, SslOptions = { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
        ConnectTimeout = TimeSpan.FromSeconds(5),
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ILinkLogSource log;

    public DiagnosticProbes(ILinkLogSource? log = null) => this.log = log ?? new LinkLogReader();

    public Task<AdapterFacts> AdapterAsync(CancellationToken ct) => Task.Run(() =>
    {
        var active = NetworkInfo.Interfaces();
        var (route, error) = NetworkInfo.BestRoute(DetectionPlanner.RouteProbeAddress);
        if (route is null) return new AdapterFacts(false, active.Count, false, false, false, null, false, error);
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
        {
            try { return n.GetIPProperties().GetIPv4Properties()?.Index == route.InterfaceIndex; }
            catch (NetworkInformationException) { return false; }
        });
        if (nic is null) return new AdapterFacts(true, active.Count, false, false, false, route.NextHop, false, "interface-not-found");
        var props = nic.GetIPProperties();
        var v4 = props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => u.Address).ToArray();
        bool apipa = v4.Length > 0 && v4.All(a => a.GetAddressBytes() is [169, 254, ..]);
        var gateway = route.NextHop ?? props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        bool dns = props.DnsAddresses.Count > 0;
        return new AdapterFacts(true, active.Count, nic.OperationalStatus == OperationalStatus.Up, v4.Length > 0, apipa, gateway, dns);
    }, ct);

    public async Task<ProbeOutcomeResult> PingAsync(IPAddress address, CancellationToken ct)
    {
        var r = await new IcmpProbe(TimeSpan.FromMilliseconds(900)).ProbeAsync(new ProbeTarget("gw", "local", address), 0, ct).ConfigureAwait(false);
        return Map(r);
    }

    public async Task<ProbeOutcomeResult> TcpAsync(IPAddress address, int port, CancellationToken ct)
    {
        var r = await new TcpProbe(TimeSpan.FromMilliseconds(2900), port).ProbeAsync(new ProbeTarget("tcp", "external", address), 0, ct).ConfigureAwait(false);
        return Map(r);
    }

    private static ProbeOutcomeResult Map(ProbeResult r) => r.Outcome switch
    {
        ProbeOutcome.Success => new(TargetOutcome.Ok),
        ProbeOutcome.Lost => new(TargetOutcome.Fail, r.Status),
        _ => new(TargetOutcome.Error, r.Status),
    };

    public async Task<ProbeOutcomeResult> DnsAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.Unspecified, ct).ConfigureAwait(false);
            return addresses.Length > 0 ? new(TargetOutcome.Ok) : new(TargetOutcome.Fail, "no-address");
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
        {
            return new(TargetOutcome.Fail, "dns-" + ex.SocketErrorCode);
        }
        catch (SocketException ex) { return new(TargetOutcome.Error, "dns-" + ex.SocketErrorCode); }
    }

    public async Task<ProbeOutcomeResult> HttpsAsync(string host, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, new Uri("https://" + host + "/"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            return status is >= 200 and < 500 && status != 407 ? new(TargetOutcome.Ok) : new(TargetOutcome.Fail, "http-" + status);
        }
        catch (HttpRequestException ex) { return HttpOutcome.Map(ex); }
    }

    public async Task<PortalResult> PortalAsync(Uri uri, string expected, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            var buffer = new byte[64];
            int read = 0;
            await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                while (read < buffer.Length)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }
            bool match = System.Text.Encoding.UTF8.GetString(buffer, 0, read).StartsWith(expected, StringComparison.Ordinal);
            return new PortalResult(TargetOutcome.Ok, status, match);
        }
        catch (HttpRequestException ex)
        {
            var mapped = HttpOutcome.Map(ex);
            return new PortalResult(mapped.Outcome, null, false, mapped.Code);
        }
    }

    public Task<LinkFacts> LinkAsync(CancellationToken ct) => Task.Run(() =>
    {
        var (route, _) = NetworkInfo.BestRoute(DetectionPlanner.RouteProbeAddress);
        if (route is null) return new LinkFacts(LinkState.Unknown, LinkCapability.Unsupported, false, "no-route");
        var (row, code) = IfEntry.Read(route.InterfaceIndex);
        if (row is null) return new LinkFacts(IfEntry.IsGone(code) ? LinkState.Removed : LinkState.Unknown, LinkCapability.Unsupported, false, IfEntry.CodeOf(code));
        var info = IfEntry.InfoOf(row);
        var nowState = IfEntry.StateOf(row);   // 지금 상태를 먼저 확보한다: 로그가 늦어도 잃지 않는다 (G4 1라운드 10)
        var now = DateTimeOffset.UtcNow;
        var reading = Task.Run(() => log.Read(now - TimeSpan.FromMinutes(30), now, [info], LogBudget), ct);
        if (!reading.Wait(LogBudget + TimeSpan.FromMilliseconds(200), ct))
        {
            _ = reading.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return new LinkFacts(nowState, LinkCapability.Unsupported, false, "log-timeout");
        }
        var result = reading.Result;
        var cap = result.Overall != LinkCapability.Supported ? result.Overall
            : result.PerAdapter.TryGetValue(info.Key, out var c) ? c.Capability : LinkCapability.Unsupported;
        bool recentDown = result.Events.Any(e => e.Match == LinkMatch.Linked && e.AdapterKey == info.Key && e.State == LinkState.Down);
        return new LinkFacts(nowState, cap, recentDown, result.OverallCode);
    }, ct);

    /// <summary>링크 검사의 로그 조회 예산 (검사 제한 3초 안에서 지금 상태를 돌려줄 여유를 남긴다).</summary>
    public static readonly TimeSpan LogBudget = TimeSpan.FromSeconds(2);

    public void Dispose() => http.Dispose();
}

/// <summary>
/// HTTP 예외 → 대상 결과 (G4 1라운드 11). 확정 실패(인증서·이름 없음·연결 거부·시간 초과·도달 불가)와
/// 검사 오류(그 밖의 소켓 오류·알 수 없는 예외)를 구분한다.
/// </summary>
public static class HttpOutcome
{
    public static ProbeOutcomeResult Map(HttpRequestException ex)
    {
        if (ex.InnerException is AuthenticationException || ex.HttpRequestError == HttpRequestError.SecureConnectionError)
            return new(TargetOutcome.Fail, "tls");
        if (ex.HttpRequestError == HttpRequestError.NameResolutionError) return new(TargetOutcome.Fail, "dns");
        if (FindSocket(ex) is { } se)
            return se.SocketErrorCode is SocketError.ConnectionRefused or SocketError.TimedOut or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable or SocketError.ConnectionReset or SocketError.HostNotFound
                ? new(TargetOutcome.Fail, "socket-" + se.SocketErrorCode)
                : new(TargetOutcome.Error, "socket-" + se.SocketErrorCode);
        return ex.HttpRequestError switch
        {
            HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded => new(TargetOutcome.Fail, "http-" + ex.HttpRequestError),
            _ => new(TargetOutcome.Error, "http-" + ex.HttpRequestError),
        };
    }

    private static SocketException? FindSocket(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is SocketException se) return se;
        return null;
    }
}
