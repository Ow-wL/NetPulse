using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetPulse
{
    internal enum CheckLevel { Pass, Review, Fail, Skipped }

    internal sealed class NetworkCheck
    {
        internal readonly string Name, Result;
        internal readonly CheckLevel Level;

        internal NetworkCheck(string name, CheckLevel level, string result)
        {
            Name = name; Level = level; Result = result;
        }

        internal string Status
        {
            get
            {
                switch (Level)
                {
                    case CheckLevel.Pass: return "정상";
                    case CheckLevel.Review: return "확인";
                    case CheckLevel.Fail: return "실패";
                    default: return "미측정";
                }
            }
        }
    }

    internal sealed class QuickReport
    {
        internal readonly NetworkCheck[] Checks;
        internal readonly DateTimeOffset Time = DateTimeOffset.Now;
        internal readonly string Headline, Advice;

        internal QuickReport(NetworkCheck adapter, NetworkCheck gateway, NetworkCheck tcp, NetworkCheck dns,
            NetworkCheck https, NetworkCheck portal, NetworkCheck link)
        {
            Checks = new[] { adapter, gateway, tcp, dns, https, portal, link };
            if (https.Level == CheckLevel.Pass && tcp.Level == CheckLevel.Fail)
            {
                Headline = "웹 응답은 확인됐습니다";
                Advice = "직접 IPv4 연결은 응답하지 않았습니다. IPv6, 프록시, VPN 또는 외부 IP 접근 제한 여부를 확인하세요.";
            }
            else if (tcp.Level != CheckLevel.Fail && dns.Level == CheckLevel.Fail)
            {
                Headline = "DNS 이름 해석을 먼저 확인하세요";
                Advice = "외부 IP에는 연결되지만 두 도메인 이름을 해석하지 못했습니다. DNS 캐시를 비운 뒤 재검사하고, 계속 실패하면 어댑터의 DNS 설정을 확인하세요.";
            }
            else if (https.Level != CheckLevel.Pass && portal.Level == CheckLevel.Review)
            {
                Headline = "네트워크 로그인이나 HTTP 정책을 확인하세요";
                Advice = "연결 확인용 HTTP 요청에 예상과 다른 응답이 왔습니다. 공용 Wi‑Fi라면 Windows에서 로그인 알림을 확인하고, 관리되는 네트워크라면 프록시 정책을 확인하세요.";
            }
            else if (adapter.Level == CheckLevel.Fail && tcp.Level == CheckLevel.Fail && https.Level != CheckLevel.Pass)
            {
                Headline = "PC의 네트워크 연결을 먼저 확인하세요";
                Advice = "Windows에서 연결 중인 어댑터나 IPv4 주소를 확인하세요. 유선은 LAN 포트와 케이블을, Wi‑Fi는 접속 상태를 먼저 확인하세요.";
            }
            else if (tcp.Level == CheckLevel.Fail && https.Level != CheckLevel.Pass)
            {
                Headline = gateway.Level == CheckLevel.Pass ? "공유기 이후 경로를 확인하세요" : "게이트웨이와 외부 경로를 확인하세요";
                Advice = "같은 네트워크의 다른 기기도 인터넷이 안 되는지 확인하세요. PC에서만 발생하면 어댑터·VPN·방화벽 상태를, 여러 기기에서 발생하면 공유기 WAN 상태를 확인하세요.";
            }
            else if (https.Level == CheckLevel.Fail || https.Level == CheckLevel.Review)
            {
                Headline = "웹 연결을 추가로 확인하세요";
                Advice = "브라우저에서 문제가 난 사이트를 열어 보세요. 다른 사이트도 실패하면 프록시·VPN·Windows 시간 및 인증서 상태를 확인하세요.";
            }
            else if (gateway.Level == CheckLevel.Review || portal.Level == CheckLevel.Review)
            {
                Headline = "인터넷 연결은 확인됐습니다";
                Advice = "게이트웨이 Ping 또는 HTTP 확인에서 예외가 관측됐습니다. 장비 정책에 따라 일부 검사 요청에 응답하지 않을 수 있습니다.";
            }
            else
            {
                Headline = "기본 인터넷 연결이 확인됐습니다";
                Advice = "간헐적 끊김은 실시간 측정을 켜둔 뒤 증상 시각과 Windows 링크 기록을 대조하세요.";
            }
        }

        internal string CopyText()
        {
            return "NetPulse 빠른 진단 · " + Time.ToString("yyyy-MM-dd HH:mm:ss zzz") + "\n" +
                Headline + "\n" + Advice + "\n\n" + string.Join("\n", Checks.Select(check =>
                    check.Name + " [" + check.Status + "] " + check.Result));
        }
    }

    internal static class QuickDiagnostics
    {
        private static readonly Target External = new Target("외부 IPv4", "1.1.1.1");
        private static readonly Uri PortalUri = new Uri("http://www.msftconnecttest.com/connecttest.txt");
        private static readonly string[] Domains = { "www.microsoft.com", "www.cloudflare.com" };
        private const int ShortTimeout = 3000;
        private const int WebTimeout = 4500;

        private static HttpClient CreateClient()
        {
            // 이 진단 요청의 프로토콜만 설정하며 Windows 정책과 인증서 검증은 유지한다.
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            };
            return new HttpClient(handler);
        }

        internal static async Task<QuickReport> RunAsync(CancellationToken token)
        {
            using (var client = CreateClient())
            {
                var adapter = Task.Run(() => ReadAdapter(), token);
                var tcp = TcpCheck(token);
                var dns = DnsCheck(token);
                var https = HttpsCheck(client, token);
                var portal = PortalCheck(client, token);
                var link = Task.Run(() => LinkCheck(), token);
                var local = await adapter.ConfigureAwait(false);
                var gateway = GatewayCheck(local.Gateway, token);
                await Task.WhenAll(tcp, dns, https, portal, link, gateway).ConfigureAwait(false);
                return new QuickReport(local.Check, gateway.Result, tcp.Result, dns.Result, https.Result, portal.Result, link.Result);
            }
        }

        internal static async Task<string> CheckSiteAsync(string domain, CancellationToken token)
        {
            using (var client = CreateClient())
            {
                var dns = Resolve(domain, token);
                var https = WebHead(client, domain, token);
                await Task.WhenAll(dns, https).ConfigureAwait(false);
                return domain + "\nWindows DNS · " + dns.Result.Item2 + "\nHTTPS · " + https.Result.Item2;
            }
        }

        private sealed class AdapterResult
        {
            internal readonly NetworkCheck Check;
            internal readonly IPAddress Gateway;
            internal AdapterResult(NetworkCheck check, IPAddress gateway = null) { Check = check; Gateway = gateway; }
        }

        private static AdapterResult ReadAdapter()
        {
            const string name = "Windows 어댑터";
            try
            {
                var route = BestRoute.Read(DateTimeOffset.Now, 0, External, null);
                var candidates = new List<Tuple<NetworkInterface, IPInterfaceProperties, int>>();
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        !nic.Supports(NetworkInterfaceComponent.IPv4)) continue;
                    var props = nic.GetIPProperties();
                    var v4 = props.GetIPv4Properties();
                    if (v4 != null) candidates.Add(Tuple.Create(nic, props, v4.Index));
                }

                var selected = route.InterfaceIndex.HasValue
                    ? candidates.FirstOrDefault(item => (uint)item.Item3 == route.InterfaceIndex.Value)
                    : null;
                if (selected == null)
                {
                    int up = candidates.Count(item => item.Item1.OperationalStatus == OperationalStatus.Up);
                    return new AdapterResult(new NetworkCheck(name, up == 0 ? CheckLevel.Fail : CheckLevel.Review,
                        up == 0 ? "활성 IPv4 어댑터를 찾지 못했습니다." : "활성 어댑터 " + up + "개 · 외부 IPv4 경로를 확인하지 못했습니다."));
                }
                var adapter = selected.Item1;
                var properties = selected.Item2;
                var addresses = properties.UnicastAddresses.Select(item => item.Address)
                    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).ToArray();
                var gateway = properties.GatewayAddresses.Select(item => item.Address)
                    .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(ip));
                var dns = properties.DnsAddresses.Take(4).ToArray();
                bool hasUsableAddress = addresses.Any(ip => !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal));
                var level = !hasUsableAddress || adapter.OperationalStatus != OperationalStatus.Up ? CheckLevel.Fail
                    : gateway == null ? CheckLevel.Review : CheckLevel.Pass;
                string detail = DiagnosticReport.RouteLabel(route) + " · " + adapter.Name + " · " + adapter.OperationalStatus +
                    "\nIPv4 " + ShowAddresses(addresses) + " · 게이트웨이 " + (gateway == null ? "없음" : gateway.ToString()) +
                    "\nDNS 서버 " + ShowAddresses(dns);
                if (!hasUsableAddress) detail += "\n유효한 IPv4 주소가 없습니다. 169.254.x.x는 자동 할당 주소입니다.";
                return new AdapterResult(new NetworkCheck(name, level, detail), gateway);
            }
            catch (Exception ex)
            {
                if (!(ex is NetworkInformationException || ex is UnauthorizedAccessException || ex is SecurityException)) throw;
                return new AdapterResult(new NetworkCheck(name, CheckLevel.Skipped, "Windows 정보 조회 실패 · " + ex.GetType().Name));
            }
        }

        private static string ShowAddresses(IPAddress[] addresses)
        {
            return addresses.Length == 0 ? "없음" : string.Join(", ", addresses.Select(ip => ip.ToString()));
        }

        private static async Task<NetworkCheck> GatewayCheck(IPAddress gateway, CancellationToken token)
        {
            const string name = "기본 게이트웨이";
            if (gateway == null) return new NetworkCheck(name, CheckLevel.Skipped, "활성 경로에서 IPv4 게이트웨이를 확인하지 못했습니다.");
            token.ThrowIfCancellationRequested();
            Sample sample = await Session.PingAsync(new Target(name, gateway.ToString())).ConfigureAwait(false);
            return new NetworkCheck(name, sample.Rtt.HasValue ? CheckLevel.Pass : CheckLevel.Review,
                gateway + " · " + (sample.Rtt.HasValue ? sample.Rtt + " ms" : sample.Status + " · 장비가 Ping을 제한할 수 있습니다."));
        }

        private static async Task<NetworkCheck> TcpCheck(CancellationToken token)
        {
            const string name = "외부 IPv4 · TCP 443";
            string[] ips = { "1.1.1.1", "8.8.8.8" };
            var checks = await Task.WhenAll(ips.Select(ip => Connect(ip, token))).ConfigureAwait(false);
            return Combine(name, ips, checks);
        }

        private static async Task<Tuple<bool, string>> Connect(string ip, CancellationToken token)
        {
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    await Within(client.ConnectAsync(IPAddress.Parse(ip), 443), token).ConfigureAwait(false);
                    return Outcome(true, clock.ElapsedMilliseconds + " ms");
                }
                catch (TimeoutException) { return Outcome(false, "시간 초과"); }
                catch (SocketException ex) { return Outcome(false, ex.SocketErrorCode.ToString()); }
            }
        }

        private static async Task<NetworkCheck> DnsCheck(CancellationToken token)
        {
            var checks = await Task.WhenAll(Domains.Select(domain => Resolve(domain, token))).ConfigureAwait(false);
            return Combine("Windows DNS 이름 해석", Domains, checks);
        }

        private static async Task<Tuple<bool, string>> Resolve(string domain, CancellationToken token)
        {
            try
            {
                Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(domain);
                await Within(lookup, token).ConfigureAwait(false);
                var addresses = lookup.Result;
                return Outcome(addresses.Length > 0, addresses.Length == 0 ? "주소 없음" : addresses[0] + " 응답");
            }
            catch (TimeoutException) { return Outcome(false, "시간 초과"); }
            catch (SocketException ex) { return Outcome(false, ex.SocketErrorCode.ToString()); }
        }

        private static async Task<NetworkCheck> HttpsCheck(HttpClient client, CancellationToken token)
        {
            var checks = await Task.WhenAll(Domains.Select(domain => WebHead(client, domain, token))).ConfigureAwait(false);
            return Combine("HTTPS 웹 연결", Domains, checks);
        }

        private static async Task<Tuple<bool, string>> WebHead(HttpClient client, string domain, CancellationToken token)
        {
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var request = new HttpRequestMessage(HttpMethod.Head, "https://" + domain + "/"))
            {
                limit.CancelAfter(WebTimeout);
                try
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        limit.Token).ConfigureAwait(false))
                    {
                        int code = (int)response.StatusCode;
                        return Outcome(code >= 200 && code < 500 && code != 407, "HTTP " + code);
                    }
                }
                catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); return Outcome(false, "시간 초과"); }
                catch (HttpRequestException ex)
                {
                    var web = ex.InnerException as WebException;
                    return Outcome(false, web == null ? "연결 또는 TLS 실패" : web.Status.ToString());
                }
            }
        }

        private static async Task<NetworkCheck> PortalCheck(HttpClient client, CancellationToken token)
        {
            const string name = "네트워크 로그인 확인";
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(WebTimeout);
                try
                {
                    using (var response = await client.GetAsync(PortalUri, HttpCompletionOption.ResponseHeadersRead,
                        limit.Token).ConfigureAwait(false))
                    {
                        int code = (int)response.StatusCode;
                        if (code == 200)
                        {
                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            {
                                byte[] buffer = new byte[64];
                                int length = 0;
                                while (length < buffer.Length)
                                {
                                    Task<int> pending = stream.ReadAsync(buffer, length, buffer.Length - length, limit.Token);
                                    await Within(pending, limit.Token).ConfigureAwait(false);
                                    int read = pending.Result;
                                    if (read == 0) break;
                                    length += read;
                                }
                                bool expected = Encoding.UTF8.GetString(buffer, 0, length).Trim() == "Microsoft Connect Test";
                                return new NetworkCheck(name, expected ? CheckLevel.Pass : CheckLevel.Review,
                                    expected ? "Windows 연결 확인 주소의 정상 응답" : "HTTP 200 · 예상과 다른 본문");
                            }
                        }
                        return new NetworkCheck(name, code >= 300 && code < 500 ? CheckLevel.Review : CheckLevel.Skipped,
                            "연결 확인 주소 · HTTP " + code);
                    }
                }
                catch (OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    return new NetworkCheck(name, CheckLevel.Skipped, "연결 확인 주소 응답 시간 초과");
                }
                catch (TimeoutException)
                {
                    return new NetworkCheck(name, CheckLevel.Skipped, "연결 확인 주소 응답 시간 초과");
                }
                catch (HttpRequestException)
                {
                    return new NetworkCheck(name, CheckLevel.Skipped, "연결 확인 주소에 접속할 수 없습니다.");
                }
                catch (IOException)
                {
                    return new NetworkCheck(name, CheckLevel.Skipped, "연결 확인 응답을 읽지 못했습니다.");
                }
            }
        }

        private static NetworkCheck LinkCheck()
        {
            const string name = "최근 유선 링크 기록";
            var end = DateTimeOffset.Now;
            var items = EvidenceRecorder.ReadLinkEvents(end.AddMinutes(-30), end);
            if (items.Last().Status != "Read")
                return new NetworkCheck(name, CheckLevel.Skipped, "시스템 이벤트 조회 불가 · " + items.Last().Status);
            var downs = items.Where(item => item.EventId == 27 && item.Time >= end.AddMinutes(-30)).OrderBy(item => item.Time).ToArray();
            return downs.Length == 0
                ? new NetworkCheck(name, CheckLevel.Pass, "최근 30분 e2fexpress 링크 다운 기록 없음")
                : new NetworkCheck(name, CheckLevel.Review, "최근 30분 e2fexpress 링크 다운 " + downs.Length +
                    "건 · 마지막 " + downs.Last().Time.ToString("HH:mm:ss"));
        }

        private static async Task Within(Task operation, CancellationToken token)
        {
            if (await Task.WhenAny(operation, Task.Delay(ShortTimeout, token)).ConfigureAwait(false) == operation)
            {
                await operation.ConfigureAwait(false);
                return;
            }
            ObserveLateFault(operation);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException();
        }

        private static void ObserveLateFault(Task operation)
        {
            operation.ContinueWith(task => GC.KeepAlive(task.Exception), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static Tuple<bool, string> Outcome(bool pass, string detail) { return Tuple.Create(pass, detail); }

        private static NetworkCheck Combine(string name, string[] labels, Tuple<bool, string>[] checks)
        {
            int passed = checks.Count(check => check.Item1);
            return new NetworkCheck(name, passed == checks.Length ? CheckLevel.Pass : passed > 0 ? CheckLevel.Review : CheckLevel.Fail,
                string.Join("  ·  ", labels.Select((label, i) => label + " " + checks[i].Item2)));
        }
    }
}
