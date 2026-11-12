// M0 스파이크 공용 코드. 제품 코드가 아니다. A안과 C안이 같은 측정·진단 루프를 쓰게 해서
// 화면 기술의 차이만 비교한다. 기록에는 이 PC의 주소, 어댑터 이름 같은 식별 정보를 남기지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetPulseSpike
{
    public sealed class SpikeOptions
    {
        public int CdpPort;
        public bool ReduceMotion, ReduceTransparency, Bench, HideTest, GlassTest;
        public string Theme = "dark";
        public int RunSeconds;

        public static SpikeOptions Parse(string[] args)
        {
            var o = new SpikeOptions();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--cdp-port": o.CdpPort = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--reduce-motion": o.ReduceMotion = true; break;
                    case "--reduce-transparency": o.ReduceTransparency = true; break;
                    case "--bench": o.Bench = true; break;
                    case "--hide-test": o.HideTest = true; break;
                    case "--glass-test": o.GlassTest = true; break;
                    case "--theme": o.Theme = args[++i]; break;
                    case "--run-seconds": o.RunSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                }
            }
            return o;
        }
    }

    /// <summary>결과를 JSON Lines로 %LOCALAPPDATA%\NetPulseSpike\{name}\ 아래에 쓴다.</summary>
    public sealed class ResultLog : IDisposable
    {
        private readonly StreamWriter writer;
        private readonly object gate = new object();
        public readonly string Folder;

        public readonly string RunId = Guid.NewGuid().ToString("N").Substring(0, 12);

        public ResultLog(string name)
        {
            Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulseSpike", name);
            Directory.CreateDirectory(Folder);
            writer = new StreamWriter(Path.Combine(Folder, "results.jsonl"), false, new UTF8Encoding(false));
            Write("run", "runId", RunId, "pid", Process.GetCurrentProcess().Id);
        }

        public void Write(string kind, params object[] pairs)
        {
            var sb = new StringBuilder();
            sb.Append("{\"t\":\"").Append(DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture))
              .Append("\",\"kind\":\"").Append(kind).Append('"');
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                sb.Append(",\"").Append(pairs[i]).Append("\":").Append(Json(pairs[i + 1]));
            sb.Append('}');
            lock (gate) { writer.WriteLine(sb.ToString()); writer.Flush(); }
        }

        public static string Json(object value)
        {
            if (value == null) return "null";
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is int || value is long || value is double || value is float)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            var s = value.ToString();
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        public void Dispose() { lock (gate) writer.Dispose(); }
    }

    public sealed class Round
    {
        public long Index;
        public double ScheduledMs, ActualMs;
        public long? RttMs;
        public double[] Series = new double[3];
    }

    /// <summary>1초 주기 루프백 Ping 루프. 화면과 무관한 스레드에서 돈다 (P4, P5).</summary>
    public sealed class MeasureLoop
    {
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Random random = new Random(7);
        public event Action<Round> RoundCompleted;
        public long Missed { get; private set; }
        public double MaxLateMs { get; private set; }
        public long Rounds { get; private set; }
        public Round Latest { get; private set; }

        public Task RunAsync(CancellationToken stop)
        {
            return Task.Run(async () =>
            {
                clock.Start();
                long next = 0;
                using (var ping = new Ping())
                {
                    while (!stop.IsCancellationRequested)
                    {
                        double wait = next * 1000.0 - clock.Elapsed.TotalMilliseconds;
                        if (wait > 0)
                        {
                            try { await Task.Delay(TimeSpan.FromMilliseconds(wait), stop).ConfigureAwait(false); }
                            catch (OperationCanceledException) { break; }
                        }
                        double actual = clock.Elapsed.TotalMilliseconds;
                        // 타이머가 예정보다 일찍 깨면 다시 기다린다. 같은 회차를 두 번 기록하지 않기 위해서다.
                        if (actual < next * 1000.0) continue;
                        long slot = (long)(actual / 1000.0);
                        if (slot > next) Missed += slot - next;
                        double late = actual - slot * 1000.0;
                        MaxLateMs = Math.Max(MaxLateMs, late);
                        long? rtt = null;
                        try
                        {
                            var reply = await ping.SendPingAsync(IPAddress.Loopback, 800).ConfigureAwait(false);
                            if (reply.Status == IPStatus.Success) rtt = reply.RoundtripTime;
                        }
                        catch (PingException) { }
                        var round = new Round { Index = slot, ScheduledMs = slot * 1000.0, ActualMs = actual, RttMs = rtt };
                        // 화면 시험용 합성 계열. 실제 측정값이 아니다.
                        double t = slot / 6.0;
                        round.Series[0] = 12 + 4 * Math.Sin(t) + random.NextDouble() * 3;
                        round.Series[1] = 24 + 6 * Math.Sin(t * 0.7 + 1) + random.NextDouble() * 5;
                        round.Series[2] = 38 + 9 * Math.Sin(t * 0.4 + 2) + (slot % 23 == 0 ? 60 : random.NextDouble() * 8);
                        Latest = round;
                        Rounds++;
                        next = slot + 1;
                        var handler = RoundCompleted;
                        if (handler != null) handler(round);
                    }
                }
            });
        }
    }

    /// <summary>관리자 권한 없이 진단 API가 동작하는지 확인한다 (P6). 결과는 성공 여부와 개수만 남긴다.</summary>
    public static class Probes
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern uint GetBestInterface(uint destination, out uint index);

        public static Dictionary<string, string> RunAll()
        {
            var result = new Dictionary<string, string>();
            result["elevated"] = IsElevated() ? "yes" : "no";
            try
            {
                using (var ping = new Ping())
                {
                    var reply = ping.Send(IPAddress.Loopback, 800);
                    result["icmp"] = reply.Status.ToString();
                }
            }
            catch (Exception ex) { result["icmp"] = "fail:" + ex.GetType().Name; }

            try
            {
                var query = new EventLogQuery("System", PathType.LogName, "*[System[TimeCreated[timediff(@SystemTime) <= 86400000]]]")
                { ReverseDirection = true };
                int count = 0;
                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while (count < 20 && (record = reader.ReadEvent()) != null) { record.Dispose(); count++; }
                }
                result["eventlog"] = "read:" + count;
            }
            catch (Exception ex) { result["eventlog"] = "fail:" + ex.GetType().Name; }

            try
            {
                // 문서용 주소(192.0.2.1). 라우팅 표만 조회하고 패킷은 보내지 않는다.
                uint index;
                uint status = GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("192.0.2.1").GetAddressBytes(), 0), out index);
                result["bestinterface"] = status == 0 ? "ok" : "status:" + status;
            }
            catch (Exception ex) { result["bestinterface"] = "fail:" + ex.GetType().Name; }

            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToArray();
                result["interfaces"] = "count:" + nics.Length + ",up:" + nics.Count(n => n.OperationalStatus == OperationalStatus.Up);
            }
            catch (Exception ex) { result["interfaces"] = "fail:" + ex.GetType().Name; }
            return result;
        }

        private static bool IsElevated()
        {
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Windows 표시 설정을 읽기만 한다 (P9). 바꾸지 않는다.</summary>
    public static class WindowsSettings
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint ini);
        private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

        public static Dictionary<string, string> Read()
        {
            var r = new Dictionary<string, string>();
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                r["appsUseLightTheme"] = key == null ? "missing" : Convert.ToString(key.GetValue("AppsUseLightTheme", "missing"), CultureInfo.InvariantCulture);
                r["enableTransparency"] = key == null ? "missing" : Convert.ToString(key.GetValue("EnableTransparency", "missing"), CultureInfo.InvariantCulture);
            }
            bool animation = true;
            r["clientAreaAnimation"] = SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref animation, 0) ? (animation ? "on" : "off") : "unknown";
            return r;
        }
    }

    public static class ProbeSummary
    {
        /// <summary>화면에 보여줄 요약. 인터페이스 수 같은 환경 정보는 화면에 내지 않는다 (공개 캡처용).</summary>
        public static string ForDisplay(Dictionary<string, string> values)
        {
            int ok = 0;
            if (values["icmp"] == "Success") ok++;
            if (values["eventlog"].StartsWith("read:", StringComparison.Ordinal)) ok++;
            if (values["bestinterface"] == "ok") ok++;
            if (values["interfaces"].StartsWith("count:", StringComparison.Ordinal)) ok++;
            return "진단 API " + ok + "/4 응답 · 관리자 권한 " + (values["elevated"] == "yes" ? "있음" : "없음");
        }
    }

    public static class StartupClock
    {
        public static double MillisecondsSinceProcessStart()
        {
            using (var p = Process.GetCurrentProcess())
                return (DateTime.Now - p.StartTime).TotalMilliseconds;
        }
    }
}
