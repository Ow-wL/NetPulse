using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NetPulse
{
    internal enum Pattern
    {
        None, LocalMesh, MeshAfterExtender, LocalWifi, External, Mixed, Incomplete, Spike, RouteMismatch
    }

    internal sealed class PingFrame
    {
        internal readonly long Round;
        internal readonly DateTimeOffset Time;
        internal readonly Dictionary<string, Sample> Samples = new Dictionary<string, Sample>();

        internal PingFrame(long round, DateTimeOffset time) { Round = round; Time = time; }

        internal Pattern Classify(string mode, IEnumerable<string> expected)
        {
            if (expected.Any(ip => !Samples.ContainsKey(ip) || Samples[ip].Error)) return Pattern.Incomplete;

            bool router = Samples[DiagnosticReport.Router].Lost;
            bool internet = Samples[DiagnosticReport.Internet].Lost;
            if (mode == "Mesh")
            {
                bool extender = Samples[DiagnosticReport.Extender].Lost;
                if (extender && router && internet) return Pattern.LocalMesh;
                if (!extender && router && internet) return Pattern.MeshAfterExtender;
                if (!extender && !router && internet) return Pattern.External;
                if (extender || router || internet) return Pattern.Mixed;
            }
            else
            {
                if (router && internet) return Pattern.LocalWifi;
                if (internet) return Pattern.External;
                if (router) return Pattern.Mixed;
            }
            return Samples.Values.Any(sample => sample.Rtt >= DiagnosticReport.SpikeMs) ? Pattern.Spike : Pattern.None;
        }
    }

    internal sealed class Incident
    {
        internal DateTimeOffset Start, End;
        internal long FirstRound, LastRound;
        internal readonly HashSet<Pattern> Patterns = new HashSet<Pattern>();
        internal readonly string Kind;
        internal readonly Evidence Item;

        internal Incident(string kind, DateTimeOffset time, Evidence item = null)
        {
            Kind = kind; Start = End = time; Item = item;
        }

        internal void Add(PingFrame frame, Pattern pattern)
        {
            if (FirstRound == 0) FirstRound = frame.Round;
            LastRound = frame.Round;
            End = frame.Time;
            Patterns.Add(pattern);
        }

        public string Caption
        {
            get
            {
                if (Kind == "Mark") return "사용자가 표시한 끊김";
                if (Kind == "Link") return Item.EventId == 27 ? "유선 링크 다운 기록" : "유선 링크 업 기록";
                if (Kind == "RouteChange") return DiagnosticReport.TargetName(Item.Ip) + " 경로 변경";
                if (Patterns.Count > 1) return "회차별 손실 패턴 변화";
                switch (Patterns.Single())
                {
                    case Pattern.LocalMesh: return "PC–확장기 구간 우선";
                    case Pattern.MeshAfterExtender: return "확장기 이후 구간 우선";
                    case Pattern.LocalWifi: return "PC–메인 공유기 구간 우선";
                    case Pattern.External: return "메인 공유기 이후 구간 우선";
                    case Pattern.Incomplete: return "검사 오류 또는 미측정";
                    case Pattern.Spike: return "50 ms 이상 지연";
                    case Pattern.RouteMismatch: return "선택 모드와 Windows 경로 불일치";
                    default: return "대상별 응답 차이";
                }
            }
        }

        public string TimeLabel
        {
            get
            {
                string value = Start.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                if (LastRound > FirstRound)
                    value += "  ·  " + (LastRound - FirstRound + 1) + "회차";
                return value;
            }
        }
    }

    internal sealed class DiagnosticReport
    {
        internal const int SpikeMs = 50;
        internal const string Extender = "192.0.2.155", Router = "192.0.2.1", Internet = "1.1.1.1";
        internal readonly List<PingFrame> Frames = new List<PingFrame>();
        internal readonly List<Incident> Incidents = new List<Incident>();
        internal readonly List<Evidence> Context = new List<Evidence>();
        internal string Mode, SessionId, EvidenceProblem;
        internal bool HasEvidenceFile;
        internal int LossRounds, SpikeRounds, IncompleteRounds;
        internal long Skipped { get { return Frames.Last().Round - Frames.Count; } }
        private static readonly string[] MeshTargets = { Extender, Router, Internet };
        private static readonly string[] WifiTargets = { Router, Internet };
        internal string[] Targets { get { return Mode == "Mesh" ? MeshTargets : WifiTargets; } }

        internal static DiagnosticReport Read(string path)
        {
            var report = new DiagnosticReport();
            var frames = new SortedDictionary<long, PingFrame>();
            int lineNumber = 1;
            using (var reader = new StreamReader(path))
            {
                if (reader.ReadLine() != "time,session,mode,round,scheduled_ms,ip,status,rtt_ms,loss")
                    throw new FormatException("NetPulse에서 저장한 samples.csv를 선택하세요.");
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    string[] row = line.Split(',');
                    long round, rtt = 0;
                    DateTimeOffset time;
                    bool valid = row.Length == 9 && (row[2] == "Mesh" || row[2] == "WiFi") &&
                        TryTime(row[0], out time) && long.TryParse(row[3], out round) && round > 0 &&
                        (row[8] == "0" || row[8] == "1" || row[8] == "") &&
                        (row[8] == "0" ? row[6] == "Success" && long.TryParse(row[7], out rtt) && rtt >= 0
                            : row[6] != "Success" && row[7] == "");
                    if (!valid) throw new FormatException("samples.csv의 " + lineNumber + "행을 읽을 수 없습니다.");
                    time = DateTimeOffset.Parse(row[0], CultureInfo.InvariantCulture);
                    round = long.Parse(row[3], CultureInfo.InvariantCulture);

                    if (report.Mode == null) { report.Mode = row[2]; report.SessionId = row[1]; }
                    if (report.Mode != row[2] || report.SessionId != row[1] || !report.Targets.Contains(row[5]))
                        throw new FormatException("서로 다른 측정 또는 알 수 없는 대상이 섞여 있습니다.");
                    PingFrame frame;
                    if (!frames.TryGetValue(round, out frame))
                    {
                        frame = new PingFrame(round, time);
                        frames.Add(round, frame);
                    }
                    if (frame.Time != time || frame.Samples.ContainsKey(row[5]))
                        throw new FormatException("같은 회차의 시각 또는 대상이 중복되었습니다.");
                    frame.Samples.Add(row[5], new Sample(row[6], row[8] == "0" ? (long?)rtt : null, row[8] == ""));
                }
            }
            if (frames.Count == 0) throw new FormatException("기록된 Ping 회차가 없습니다.");
            report.Frames.AddRange(frames.Values);
            report.ReadEvidence(Path.Combine(Path.GetDirectoryName(path), "evidence.csv"));
            report.Analyze();
            return report;
        }

        private void ReadEvidence(string path)
        {
            if (!File.Exists(path)) return;
            HasEvidenceFile = true;
            try
            {
                using (var reader = new StreamReader(path))
                {
                    if (reader.ReadLine() != Evidence.Header) throw new FormatException();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var row = line.Split(',');
                        DateTimeOffset time;
                        if (row.Length != 10 || !TryTime(row[0], out time)) throw new FormatException();
                        Context.Add(new Evidence(time, row[1], row[9], NullableLong(row[2]), row[3],
                            row[4] == "" ? (uint?)null : uint.Parse(row[4], CultureInfo.InvariantCulture), row[5], row[6],
                            row[7] == "" ? (int?)null : int.Parse(row[7], CultureInfo.InvariantCulture), NullableLong(row[8])));
                    }
                }
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is OverflowException)) throw;
                EvidenceProblem = "보조 기록 일부를 읽지 못했습니다.";
            }
        }

        private void Analyze()
        {
            string[] expected = Targets;
            var routes = Context.Where(item => (item.Kind == "RouteInitial" || item.Kind == "RouteChange") &&
                item.Round.HasValue).OrderBy(item => item.Round.Value).ToArray();
            int routeIndex = 0;
            var activeRoutes = new Dictionary<string, Evidence>();
            Incident current = null;
            foreach (PingFrame frame in Frames)
            {
                while (routeIndex < routes.Length && routes[routeIndex].Round.Value <= frame.Round)
                {
                    Evidence route = routes[routeIndex++];
                    activeRoutes[route.Ip] = route;
                }

                Pattern pattern = frame.Classify(Mode, expected);
                bool hasLoss = frame.Samples.Values.Any(sample => sample.Lost);
                string kind;
                if (hasLoss) { LossRounds++; kind = "Loss"; }
                else if (pattern == Pattern.Incomplete) { IncompleteRounds++; kind = "Incomplete"; }
                else if (pattern == Pattern.Spike) { SpikeRounds++; kind = "Spike"; }
                else { current = null; continue; }

                if (hasLoss && pattern == Pattern.Incomplete) IncompleteRounds++;
                if (hasLoss && pattern != Pattern.Incomplete &&
                    activeRoutes.Values.Any(route => RouteMismatch(Mode, route))) pattern = Pattern.RouteMismatch;
                if (current == null || current.Kind != kind || frame.Round != current.LastRound + 1)
                {
                    current = new Incident(kind, frame.Time);
                    Incidents.Add(current);
                }
                current.Add(frame, pattern);
            }
            foreach (Evidence item in Context.Where(item => item.Kind == "Mark" || item.Kind == "Link" || item.Kind == "RouteChange"))
                Incidents.Add(new Incident(item.Kind, item.Time, item));
            Incidents.Sort((left, right) => left.Start.CompareTo(right.Start));
        }

        internal string Headline
        {
            get
            {
                return ModeName(Mode) + "  ·  " + Frames.Count + "회차  ·  손실 " + LossRounds +
                    "회  ·  지연 상승 " + SpikeRounds + "회  ·  검사 오류·미측정 " + IncompleteRounds +
                    "회  ·  번호 누락 " + Skipped + "회";
            }
        }

        internal string LogStatus
        {
            get
            {
                if (EvidenceProblem != null) return EvidenceProblem;
                if (!HasEvidenceFile) return "이 측정에는 경로·Windows 링크 로그가 저장되어 있지 않습니다.";
                var query = Context.LastOrDefault(item => item.Kind == "LogQuery");
                if (query == null) return "Windows 링크 로그 조회 상태가 저장되지 않았습니다.";
                if (query.Status != "Read") return "Windows 링크 로그를 읽지 못했습니다 (" + query.Status + ").";
                return "Windows 링크 로그 조회 완료 · e2fexpress 27/32 " + Context.Count(item => item.Kind == "Link") + "건";
            }
        }

        internal Evidence RouteAt(string ip, DateTimeOffset time)
        {
            return Context.LastOrDefault(item => (item.Kind == "RouteInitial" || item.Kind == "RouteChange") &&
                item.Ip == ip && item.Time <= time);
        }

        internal static string RouteLabel(Evidence route)
        {
            if (route == null) return "기록 없음";
            if (!route.InterfaceIndex.HasValue) return "조회 실패 (" + route.Status + ")";
            string type = route.InterfaceType == "WiFi" ? "Wi‑Fi" : route.InterfaceType == "Ethernet" ? "이더넷" :
                route.InterfaceType == "Other" ? "기타" : "유형 미확인";
            return type + " #" + route.InterfaceIndex.Value;
        }

        internal static string RouteNote(string mode, Evidence route)
        {
            return RouteMismatch(mode, route) ?
                "선택 모드와 Windows 경로가 다릅니다. 구간 해석 전에 연결을 확인하세요." : "";
        }

        private static bool RouteMismatch(string mode, Evidence route)
        {
            return route != null && route.InterfaceIndex.HasValue && route.InterfaceType != "Unknown" &&
                route.InterfaceType != (mode == "Mesh" ? "Ethernet" : "WiFi");
        }

        internal static string TargetName(string ip)
        {
            if (ip == Extender) return "Mesh 확장기";
            if (ip == Router) return "메인 공유기";
            if (ip == Internet) return "외부 IP";
            return ip;
        }

        internal static string ModeName(string mode) { return mode == "Mesh" ? "Mesh 경유" : "직접 Wi‑Fi"; }

        private static bool TryTime(string input, out DateTimeOffset time)
        {
            return DateTimeOffset.TryParseExact(input, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
        }

        private static long? NullableLong(string value)
        {
            return value == "" ? (long?)null : long.Parse(value, CultureInfo.InvariantCulture);
        }
    }
}
