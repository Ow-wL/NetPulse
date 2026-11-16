using System.IO;
using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.UiTests;

/// <summary>
/// M7 시험 준비 (설계 4.3·7.2): 장면마다 필요한 기록만 만들어 목록 0번 줄이 그 장면의 기록이 되게 한다.
/// 데모 기록은 제품 코드(데모 세계 + 세션)로, 손상·끊김 모양이 정해진 기록은 기록 형식(RecordSchema) 그대로 합성한다.
/// 주소는 문서용(192.0.2.x, 198.51.100.x, 203.0.113.x)만.
/// </summary>
internal static class M7Fixtures
{
    private static readonly DateTimeOffset Kst = M5Fixtures.DemoNow;
    private static string Records(string userData) => Path.Combine(userData, "records");

    public static bool Prepare(string userData, string prep)
    {
        switch (prep)
        {
            case "rec-ok": Ok(userData); return true;
            case "rec-many":
                M5Fixtures.SaveProfile(userData, "steady");
                for (int i = 0; i < 35; i++) M5Fixtures.MakeRecord(userData, i % 5 == 0 ? "outage-long" : "steady", Kst.AddDays(-2).AddMinutes(i * 30), 1);
                return true;
            case "rec-mix":
            {
                Ok(userData);
                NoSummary(userData);
                var damaged = M5Fixtures.MakeRecord(userData, "steady", Kst.AddMinutes(-30), 3);
                CorruptRounds(userData, damaged);
                M5Fixtures.MakeBrokenRecord(userData);
                return true;
            }
            case "rec-mixed": Synthetic(userData, Kst.AddMinutes(-40), mixed: true); return true;
            case "rec-sparse": Synthetic(userData, Kst.AddMinutes(-40), mixed: false, deadTarget: true); return true;
            case "rec-damaged":
            {
                var id = Ok(userData);
                CorruptRounds(userData, id);
                AddDetachedSegment(userData, id);
                return true;
            }
            case "rec-evidence":
            {
                var id = Ok(userData);
                AddMark(userData, id, 30_000, "회의 중 끊김");
                File.AppendAllText(Path.Combine(Folder(userData, id), "events.jsonl"), "{ 망가진 근거\n");
                File.AppendAllText(Path.Combine(Folder(userData, id), RecordNotes.FileName), "{ 망가진 메모\n");
                return true;
            }
            case "rec-nosummary": NoSummary(userData); return true;
            case "rec-unreadable": M5Fixtures.SaveProfile(userData, "steady"); M5Fixtures.MakeBrokenRecord(userData); return true;
            case "rec-memo": AddMark(userData, Ok(userData), 30_000, "회의 중 끊김"); return true;
            case "rec-memo-blocked":
            {
                var id = Ok(userData);
                AddMark(userData, id, 30_000, "회의 중 끊김");
                Directory.CreateDirectory(Path.Combine(Folder(userData, id), RecordNotes.FileName));   // 노트 파일 자리에 폴더 → 쓰기 실패
                return true;
            }
            case "rec-diag":
            {
                var id = Ok(userData);
                M5Fixtures.MakeDiagnosisRecord(userData);
                DiagnosisDuring(userData, id);
                File.WriteAllText(Path.Combine(userData, "diagnoses", "diagnosis-20261101T000000Z.json"), "{ 망가짐");
                return true;
            }
            case "rec-diag-empty": Ok(userData); return true;
            case "rec-diag-mismatch":
            {
                // 저장된 결론 번호를 바꿔, 다시 해석한 결론과 다르게 (설계 3.6·4.3 "불일치")
                Ok(userData);
                M5Fixtures.MakeDiagnosisRecord(userData);
                var file = Directory.GetFiles(Path.Combine(userData, "diagnoses"), "*.json").Single();
                var o = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                o["conclusion"] = (int)o["conclusion"]! == 1 ? 2 : 1;
                File.WriteAllText(file, o.ToJsonString());
                return true;
            }
            case "rec-two-memos-blocked":
            {
                // 서로 다른 증상 표시 둘, 노트 쓰기는 막힘 (한 메모가 재시도하는 동안 다른 메모를 고친다, G4 M7 6라운드 1)
                var id = Ok(userData);
                AddMark(userData, id, 30_000, "첫 메모", "fixture-mark-a");
                AddMark(userData, id, 90_000, "둘째 메모", "fixture-mark-b");
                Directory.CreateDirectory(Path.Combine(Folder(userData, id), RecordNotes.FileName));
                return true;
            }
            case "rec-mixed-memo-blocked":
            {
                // 끊김과 증상 표시가 있는 기록, 노트 쓰기는 막힘 (저장하는 중이 몇 초 보임)
                var id = Synthetic(userData, Kst.AddMinutes(-40), mixed: true);
                AddMark(userData, id, 30_000, "회의 중 끊김");
                Directory.CreateDirectory(Path.Combine(Folder(userData, id), RecordNotes.FileName));
                return true;
            }
            case "rec-compare":
                M5Fixtures.SaveProfile(userData, "steady");
                M5Fixtures.MakeRecord(userData, "outage-long", Kst.AddHours(-3), 3);
                M5Fixtures.MakeRecord(userData, "steady", Kst.AddHours(-2), 3);
                return true;
            case "rec-compare-mismatch":
            {
                M5Fixtures.SaveProfile(userData, "steady");
                M5Fixtures.MakeRecord(userData, "outage-long", Kst.AddHours(-3), 3);
                var id = M5Fixtures.MakeRecord(userData, "steady", Kst.AddHours(-2), 3);
                ChangeHeader(Folder(userData, id), o => o["timeoutMs"] = 2000);   // 제한시간 다름
                return true;
            }
            case "rec-external":
            {
                M5Fixtures.SaveProfile(userData, "steady");
                var other = Path.Combine(userData, "elsewhere");
                var id = M5Fixtures.MakeRecord(other, "outage-long", Kst.AddHours(-5), 3);
                var source = Path.Combine(other, "records", id);
                var usb = Path.Combine(userData, "usb");
                Directory.CreateDirectory(usb);
                Directory.Move(source, Path.Combine(usb, "picked"));
                // 이웃에 머리가 다른 조각 하나 → "머리가 다른 조각 1개를 뺐습니다"
                CopyDirectory(Path.Combine(usb, "picked"), Path.Combine(usb, id + "~2"));
                ChangeHeader(Path.Combine(usb, id + "~2"), o => { o["segment"] = 1; o["timeoutMs"] = 3000; });
                Directory.Delete(other, true);
                return true;
            }
            case "profile-short": M5Fixtures.SaveProfile(userData, "steady", TimeSpan.FromMinutes(1)); return true;
            case "profile-outage": M5Fixtures.SaveProfile(userData, "outage-long", TimeSpan.FromMinutes(3)); return true;
            case "rec-notrecord": Ok(userData); Directory.CreateDirectory(Path.Combine(userData, "photos")); return true;
            default: return false;
        }
    }

    private static string Ok(string userData)
    {
        M5Fixtures.SaveProfile(userData, "steady");
        return M5Fixtures.MakeRecord(userData, "steady", Kst.AddHours(-1), 3);
    }

    /// <summary>요약을 지운 기록 (끝 기록 없음). 다른 시각으로 하나 더 만든다.</summary>
    private static string NoSummary(string userData)
    {
        M5Fixtures.SaveProfile(userData, "steady");
        var id = M5Fixtures.MakeRecord(userData, "outage-long", Kst.AddMinutes(-20), 3);
        File.Delete(Path.Combine(Folder(userData, id), "summary.json"));
        return id;
    }

    public static string Folder(string userData, string id) => Path.Combine(Records(userData), id);

    private static void CorruptRounds(string userData, string id) =>
        File.AppendAllText(Path.Combine(Folder(userData, id), "rounds.jsonl"), "{ 망가진 회차\n{ 또 망가짐\n");

    /// <summary>조각 2만 따로 두어 조각 1이 없는 상태 (누락 조각).</summary>
    private static void AddDetachedSegment(string userData, string id)
    {
        var target = Path.Combine(Records(userData), id + "~3");
        CopyDirectory(Folder(userData, id), target);
        ChangeHeader(target, o => o["segment"] = 2);
        File.Delete(Path.Combine(target, "summary.json"));
    }

    public static void AddMark(string userData, string id, long atMs, string? memo, string mark = "fixture-mark")
    {
        var folder = Folder(userData, id);
        var header = RecordSchema.ParseHeader(File.ReadAllText(Path.Combine(folder, "measurement.json"))).Header!;
        var fields = new JsonObject { ["mark"] = mark };
        if (memo is not null) fields["memo"] = memo;
        File.AppendAllText(Path.Combine(folder, "events.jsonl"),
            RecordSchema.EventLine(id, SymptomMarks.MarkType, atMs / 1000, atMs, header.StartedAt.AddMilliseconds(atMs), fields) + "\n");
    }

    private static void DiagnosisDuring(string userData, string measurementId)
    {
        var scenario = M5Fixtures.Load("diag-dns");
        var tb = new NetPulse.Core.Demo.DemoTimeBase(Kst.AddMinutes(-58), 1);
        tb.Freeze(TimeSpan.FromSeconds(10));
        var world = new NetPulse.Core.Demo.DemoWorld(scenario, tb);
        var result = NetPulse.Core.Diagnostics.QuickDiagnosis.RunAsync(new NetPulse.Core.Demo.DemoDiagnosticProbes(world),
            NetPulse.Core.Demo.DemoDiagnosticProbes.Settings(scenario), () => Kst.AddMinutes(-58)).GetAwaiter().GetResult();
        new DiagnosisStore(Path.Combine(userData, "diagnoses")).Save(result, "test", measurementId);
    }

    private static void ChangeHeader(string folder, Action<JsonObject> change)
    {
        var path = Path.Combine(folder, "measurement.json");
        var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(o);
        File.WriteAllText(path, o.ToJsonString());
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    }

    /// <summary>
    /// 합성 기록 (20분, 1초). mixed: 외부 둘 함께 손실 3회차 → 이어서 하나만 손실 2회차 (판정이 바뀌는 끊김) + 그 안의 증상 표시.
    /// deadTarget: 외부 B가 한 번도 응답하지 않음 (지연 추세 "값이 적어 추세 없음").
    /// </summary>
    public static string Synthetic(string userData, DateTimeOffset start, bool mixed, bool deadTarget = false, int rounds = 1200)
    {
        M5Fixtures.SaveProfile(userData, "steady");
        var id = RecordLocation.NewId(start);
        var folder = Folder(userData, id);
        Directory.CreateDirectory(folder);
        Target[] targets =
        [
            new("gateway", "공유기", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp),
            new("ext-a", "외부 A", IPAddress.Parse("198.51.100.10"), TargetRole.External, "예시 통신사 A", ProbeMethod.Icmp),
            new("ext-b", "외부 B", IPAddress.Parse("203.0.113.20"), TargetRole.External, "예시 통신사 B", ProbeMethod.Icmp),
        ];
        int n = rounds;
        var header = new RecordHeader(id, 0, "test", start, 540, targets, 1000, 800, (long)n * 1000, "primary", ExpectedConnection.Wired);
        File.WriteAllText(Path.Combine(folder, "measurement.json"), RecordSchema.HeaderJson(header));
        var rng = new Random(7);
        var lines = new List<string>();
        for (int r = 0; r < n; r++)
        {
            // 긴 기록은 2시간마다 같은 모양의 끊김 (12시간 = 6번)
            int at400 = rounds > 7200 ? r % 7200 : r;
            bool bothDown = mixed && at400 is >= 400 and < 403;
            bool aDown = mixed && at400 is >= 403 and < 405;
            double drift = 18 + (r % 7200) / 120.0;   // 서서히 오르는 지연 (추세가 보이게)
            TargetResult Ext(string tid, bool down, bool dead) => down || dead ? new(tid, ProbeOutcome.Lost, null, null) : new(tid, ProbeOutcome.Success, drift + rng.NextDouble() * 4, null);
            var results = new[]
            {
                new TargetResult("gateway", ProbeOutcome.Success, 2 + rng.NextDouble(), null),
                Ext("ext-a", bothDown || aDown, false),
                Ext("ext-b", bothDown, deadTarget),
            };
            long at = r * 1000L;
            lines.Add(RecordSchema.RoundLine(id, new RoundData(r, at, at + 3, start.AddMilliseconds(at), 0, false, results, at + 40)));
        }
        File.WriteAllLines(Path.Combine(folder, "rounds.jsonl"), lines);
        var events = targets.Select(t => RecordSchema.EventLine(id, "route", 0, 0, start, new JsonObject
        {
            ["target"] = t.Id, ["status"] = "ok", ["interfaceIndex"] = 7, ["kind"] = "wired",
        })).ToList();
        if (mixed) events.Add(RecordSchema.EventLine(id, SymptomMarks.MarkType, 401, 401_000, start.AddSeconds(401), new JsonObject { ["mark"] = "fixture-mark", ["memo"] = "회의 중 끊김" }));
        File.WriteAllLines(Path.Combine(folder, "events.jsonl"), events);
        File.WriteAllText(Path.Combine(folder, "summary.json"), RecordSchema.SummaryJson(
            new RecordSummary(id, EndReason.Completed, start.AddSeconds(n), n, n, 0, 0, 0, n - 1, false, null, 0, (long)n * 1000)));
        return id;
    }

    /// <summary>기록 화면 bench 준비 (설계 5절): 짧은 기록 38개(가장 최근 것에 증상 표시) + 합성 12시간 기록 2개.</summary>
    public static void BenchRecords(string userData)
    {
        M5Fixtures.SaveProfile(userData, "steady");
        Synthetic(userData, Kst.AddDays(-3), mixed: true, rounds: 12 * 3600);
        Synthetic(userData, Kst.AddDays(-2), mixed: true, rounds: 12 * 3600);
        string? newest = null;
        for (int i = 0; i < 38; i++) newest = M5Fixtures.MakeRecord(userData, i % 4 == 0 ? "outage-long" : "steady", Kst.AddDays(-1).AddMinutes(i * 20), 1);
        AddMark(userData, newest!, 20_000, null);
    }
}
