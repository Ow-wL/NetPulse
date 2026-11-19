using System.IO;
using System.Text.Json;
using NetPulse.Core.Demo;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.UiTests;

/// <summary>
/// M5 시험 준비: 사용자 폴더에 측정 구성·데모 기록을 미리 만든다. 기록은 제품 코드(데모 세계 + 세션)로 시험 프로세스 안에서 만든다
/// (처리 속도에 맞춘 진행이라 몇 초면 끝난다). 앱이 데모로 쓰는 대체 위치도 알려 준다.
/// </summary>
internal static class M5Fixtures
{
    public static readonly DateTimeOffset DemoNow = new(2026, 11, 13, 13, 20, 0, TimeSpan.FromHours(9));

    public static string ScenarioPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "scenarios", name + ".json") is var p && File.Exists(p) ? p : throw new FileNotFoundException(name);

    public static Scenario Load(string name, DateTimeOffset? startAt = null)
    {
        var text = File.ReadAllText(ScenarioPath(name));
        if (startAt is { } s)
        {
            var o = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
            o["startAt"] = s.ToString("O");
            text = o.ToJsonString();
        }
        return Scenario.Parse(text);
    }

    /// <summary>앱이 데모에서 쓰는 기록 위치 (기본: 사용자 폴더의 records, 대체: 임시 폴더).</summary>
    public static IReadOnlyList<string> RecordRoots(string userData) =>
        [Path.Combine(userData, "records"), Path.Combine(userData, "demo-fallback")];

    /// <summary>그 시나리오의 데모 탐지 제안을 측정 구성으로 저장한다.</summary>
    public static Profile SaveProfile(string userData, string scenario, TimeSpan? duration = null)
    {
        Directory.CreateDirectory(userData);
        var p = new DemoWorld(Load(scenario), new DemoTimeBase(DemoNow, 1)).Detect().Proposal!;
        if (duration is { } d) p = p with { Duration = d };
        ProfileStore.Save(userData, new ProfileSet([p], p.Id));
        return p;
    }

    /// <summary>데모 기록 하나를 끝까지 만든다 (계획 시간 = <paramref name="minutes"/>분).</summary>
    public static string MakeRecord(string userData, string scenario, DateTimeOffset startAt, int minutes)
    {
        var world = new DemoWorld(Load(scenario, startAt), new DemoTimeBase(startAt, 600));
        var app = new DemoAppWorld(world, userData, new DemoRunRules(), Path.Combine(userData, "demo-fallback"));
        var profile = world.Detect().Proposal! with { Duration = TimeSpan.FromMinutes(minutes) };
        var session = app.StartSession(profile, "test");
        var result = session.Completion.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        if (result.EndReason != EndReason.Completed) throw new InvalidOperationException("기록 준비 실패: " + result.EndReason);
        return session.Id;
    }

    /// <summary>홈 "최근 측정 3개" 장면: 오늘·어제·11월 10일 (기준 이미지와 같은 시각).</summary>
    public static void MakeRecentThree(string userData)
    {
        SaveProfile(userData, "steady");
        MakeRecord(userData, "link-drop", new DateTimeOffset(2026, 11, 10, 20, 45, 0, TimeSpan.FromHours(9)), 3);
        MakeRecord(userData, "outage-long", new DateTimeOffset(2026, 11, 12, 21, 2, 0, TimeSpan.FromHours(9)), 3);
        MakeRecord(userData, "steady", new DateTimeOffset(2026, 11, 13, 13, 0, 0, TimeSpan.FromHours(9)), 3);
    }

    /// <summary>읽지 못하는 측정 폴더를 하나 둔다 (머리 손상).</summary>
    public static void MakeBrokenRecord(string userData)
    {
        var folder = Path.Combine(userData, "records", "20261113T030000Z_broken");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "measurement.json"), "{\"format\":\"netpulse.measurement\",");
    }

    /// <summary>지난 진단 기록 하나 (결론 6: 외부 TCP·DNS·HTTPS 실패). 제품의 기록 형식 그대로 Core로 만든다.</summary>
    public static void MakeDiagnosisRecord(string userData)
    {
        var scenario = Load("diag-outside");
        var tb = new DemoTimeBase(DemoNow.AddMinutes(-30), 1);
        tb.Freeze(TimeSpan.FromSeconds(10));
        var world = new DemoWorld(scenario, tb);
        var settings = NetPulse.Core.Demo.DemoDiagnosticProbes.Settings(scenario);
        var result = NetPulse.Core.Diagnostics.QuickDiagnosis.RunAsync(new NetPulse.Core.Demo.DemoDiagnosticProbes(world), settings, () => DemoNow.AddMinutes(-30))
            .GetAwaiter().GetResult();
        new DiagnosisStore(Path.Combine(userData, "diagnoses")).Save(result, "test", null);
    }

    /// <summary>손상된 측정 구성 파일 (첫 실행이 "따로 보관했습니다"를 알린다).</summary>
    public static void BreakProfile(string userData)
    {
        Directory.CreateDirectory(userData);
        File.WriteAllText(Path.Combine(userData, ProfileStore.FileName), "{\"version\": 1, \"profiles\": [");
    }

    public static void Prepare(string userData, string prep)
    {
        var (kind, arg) = prep.Split(':') is [var k, var a] ? (k, a) : (prep, "steady");
        switch (kind)
        {
            case "none": break;
            case "profile": SaveProfile(userData, arg, arg == "steady" ? TimeSpan.FromMinutes(60) : null); break;
            case "records3": MakeRecentThree(userData); break;
            case "recorderror": MakeRecentThree(userData); MakeBrokenRecord(userData); break;
            case "unreadable": BreakProfile(userData); break;
            case "diagrecord": SaveProfile(userData, "steady", TimeSpan.FromMinutes(60)); MakeDiagnosisRecord(userData); break;
            case "diagblock":
                SaveProfile(userData, "steady", TimeSpan.FromMinutes(60));
                File.WriteAllText(Path.Combine(userData, "diagnoses"), "막음");   // 진단 기록 폴더 자리에 파일 → 저장 실패
                break;
            // ---- M9 설정 장면 (설계 7.4) ----
            case "settings-newer":   // 더 새 판 설정 파일: 이번 실행은 저장하지 않는다
                SaveProfile(userData, "steady", TimeSpan.FromMinutes(60));
                File.WriteAllText(Path.Combine(userData, "settings.json"), "{\"version\": 3}");
                break;
            case "rec-move":         // 기록 둘 + 옮길 목적지 폴더 ({userData}-dest)
                SaveProfile(userData, "steady", TimeSpan.FromMinutes(60));
                MakeRecord(userData, "steady", new DateTimeOffset(2026, 11, 10, 20, 0, 0, TimeSpan.FromHours(9)), 2);
                MakeRecord(userData, "link-drop", new DateTimeOffset(2026, 11, 11, 20, 0, 0, TimeSpan.FromHours(9)), 3);
                Directory.CreateDirectory(userData + "-dest");
                break;
            case "rec-move-conflict":      // 목적지에 같은 기록이 다른 내용으로 있음 (옮기기 충돌 장면)
            {
                Prepare(userData, "rec-move");
                var from = Path.Combine(userData, "records");
                var first = Directory.EnumerateDirectories(from).Select(Path.GetFileName).Order().First()!;
                var copy = Path.Combine(userData + "-dest", first);
                Directory.CreateDirectory(copy);
                foreach (var f in Directory.EnumerateFiles(Path.Combine(from, first))) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
                File.AppendAllText(Path.Combine(copy, "rounds.jsonl"), "");
                File.WriteAllText(Path.Combine(copy, "extra.txt"), "다른 내용");
                break;
            }
            case "settings-readonly":      // 설정 파일을 쓸 수 없음 (저장 실패 장면). 지울 때 AppDriver가 속성을 푼다
                SaveProfile(userData, "steady", TimeSpan.FromMinutes(60));
                File.WriteAllText(Path.Combine(userData, "settings.json"), "{\"version\": 2}");
                File.SetAttributes(Path.Combine(userData, "settings.json"), FileAttributes.ReadOnly);
                break;
            case "rec-move-interrupted":   // 옮기기가 끊긴 상태: 위치는 목적지, 옛 위치가 남고 일지가 있다
            case "rec-move-bad-journal":   // 일지 모양이 틀림: 자동 동작 없이 경고
                Prepare(userData, "rec-move");
                var old = Path.Combine(userData, "records");
                var dest = userData + "-dest";
                var ids = Directory.EnumerateDirectories(old).Select(Path.GetFileName).Order().ToArray();
                Directory.Move(Path.Combine(old, ids[0]!), Path.Combine(dest, ids[0]!));
                var journal = kind == "rec-move-bad-journal" ? new System.Text.Json.Nodes.JsonObject { ["kind"] = "what" }
                    : new System.Text.Json.Nodes.JsonObject
                    {
                        ["kind"] = "new", ["from"] = old, ["to"] = dest, ["step"] = "moving",
                        ["ids"] = new System.Text.Json.Nodes.JsonArray(ids.Select(i => (System.Text.Json.Nodes.JsonNode?)i).ToArray()),
                        ["hashes"] = new System.Text.Json.Nodes.JsonObject(), ["done"] = new System.Text.Json.Nodes.JsonArray(ids[0]),
                    };
                var settings = new System.Text.Json.Nodes.JsonObject
                {
                    ["version"] = 2,
                    ["records"] = new System.Text.Json.Nodes.JsonObject { ["folder"] = dest, ["extraFolders"] = new System.Text.Json.Nodes.JsonArray(old), ["move"] = journal },
                };
                File.WriteAllText(Path.Combine(userData, "settings.json"), settings.ToJsonString());
                break;
            case "settings-missing-folder":   // 설정의 위치가 없어짐 (지운 드라이브 등): 측정은 대체 위치로
                SaveProfile(userData, "steady", TimeSpan.FromMinutes(60));
                var gone = new System.Text.Json.Nodes.JsonObject
                {
                    ["version"] = 2,
                    ["records"] = new System.Text.Json.Nodes.JsonObject { ["folder"] = Path.Combine(userData, "gone", "records"), ["extraFolders"] = new System.Text.Json.Nodes.JsonArray() },
                };
                File.WriteAllText(Path.Combine(userData, "gone"), "폴더 자리에 파일");   // 만들 수 없는 위치
                File.WriteAllText(Path.Combine(userData, "settings.json"), gone.ToJsonString());
                break;
            case "hint-shown":
                SaveProfile(userData, "steady", TimeSpan.FromMinutes(60));
                File.WriteAllText(Path.Combine(userData, "settings.json"), "{\"version\": 2, \"resident\": {\"trayHintShown\": true}}");
                break;
            default:
                if (!M7Fixtures.Prepare(userData, prep)) throw new ArgumentException("알 수 없는 준비: " + prep);
                break;
        }
    }

    /// <summary>시나리오 인수: "tests:이름"이면 시험 폴더의 시나리오 경로로.</summary>
    public static string[] Args(string args) => args.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(a => a.StartsWith("tests:", StringComparison.Ordinal) ? ScenarioPath(a[6..]) : a).ToArray();

    public static string Str(JsonElement e, params string[] path)
    {
        foreach (var p in path) e = e.GetProperty(p);
        return e.ValueKind == JsonValueKind.Null ? "" : e.ToString();
    }
}
