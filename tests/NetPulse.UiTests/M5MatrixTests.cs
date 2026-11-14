using System.IO;
using System.Text.Json;
using Size = NetPulse.Design.Tokens.Size;

namespace NetPulse.UiTests;

/// <summary>
/// M5 캡처 매트릭스와 배치 검사 (설계 7.4). 장면 30개 × 다크·라이트 × 배율 1·1.5. 장면 목록은 m5-scenes.txt (ui-test.ps1도 같은 파일로 빠진 장면을 찾는다).
/// 한 번 띄워 장면을 만든 뒤: 알림을 닫고 → 창 전용 캡처 → 그 장면 그대로 배치 검사(잘림·글자 잘림·겹침 0).
/// </summary>
public sealed class M5MatrixTests
{
    public sealed record Scene(string Id, string Prep, string Args, string[] Actions);

    public static IReadOnlyList<Scene> Scenes(string file = "m5-scenes.txt") => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, file))
        .Where(l => l.Trim().Length > 0 && !l.StartsWith('#'))
        .Select(l => l.Split('|'))
        .Select(p => new Scene(p[0], p[1], p[2], p[3].Split(';', StringSplitOptions.RemoveEmptyEntries)))
        .ToArray();

    public static TheoryData<string, string, string> Matrix()
    {
        var d = new TheoryData<string, string, string>();
        // 개발 중 일부 장면만 다시 돌릴 때: NETPULSE_SCENES=id1,id2 (게이트는 비워 두고 전부 돈다; ui-test.ps1이 결과 수를 검사)
        var only = Environment.GetEnvironmentVariable("NETPULSE_SCENES")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var s in Scenes().Where(s => only is not { Length: > 0 } || only.Contains(s.Id)))
            foreach (var t in new[] { "dark", "light" })
                foreach (var scale in new[] { "1", "1.5" })
                    d.Add(s.Id, t, scale);
        return d;
    }

    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>장면 하나를 띄우고 동작까지 마친 앱.</summary>
    internal static AppDriver Open(Scene scene, params string[] extra)
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.Prepare(userData, scene.Prep);
        var app = AppDriver.LaunchWith(userData, [.. M5Fixtures.Args(scene.Args), "--freeze-motion", .. extra]);
        try
        {
            foreach (var a in scene.Actions) Act(app, a);
            return app;
        }
        catch
        {
            app.Dispose();
            AppDriver.DeleteUserData(userData);
            throw;
        }
    }

    internal static void Act(AppDriver app, string action)
    {
        var (verb, arg) = action.IndexOf(':') is var i and > 0 ? (action[..i], action[(i + 1)..]) : (action, "");
        switch (verb)
        {
            case "invoke": app.Invoke(arg); break;
            case "select": app.Select(arg); break;
            case "text":
                var eq = arg.IndexOf('=');
                app.SetText(arg[..eq], arg[(eq + 1)..]);
                break;
            case "wait": Wait(app, arg); break;
            case "scroll":   // 스크롤 영역을 세로 n%로 (150%에서 아래로 밀린 카드). 스크롤할 것이 없으면 그대로
                var at = arg.IndexOf('=');
                app.ScrollTo(arg[..at], double.Parse(arg[(at + 1)..], System.Globalization.CultureInfo.InvariantCulture));
                break;
            default: throw new ArgumentException("알 수 없는 동작: " + action);
        }
    }

    private static void Wait(AppDriver app, string what)
    {
        var (key, value) = what.IndexOf('=') is var i and > 0 ? (what[..i], what[(i + 1)..]) : (what, "");
        switch (key)
        {
            case "screen": app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == value, "화면 " + value); break;
            case "host": app.WaitDiag(d => M5Fixtures.Str(d, "host"), s => s == value, "측정 상태 " + value); break;
            case "held": app.WaitDiag(d => d.GetProperty("held").GetBoolean(), h => h, "데모 시간 멈춤"); break;
            case "analysis":
                app.WaitDiag(d => d.GetProperty("analysis").ValueKind == JsonValueKind.Object
                    && d.GetProperty("analysis").GetProperty("Rounds").GetInt32() >= d.GetProperty("rounds").GetInt32() - 1, ok => ok, "멈춘 회차까지의 잠정 분석");
                break;
            case "recentListed": app.WaitDiag(d => d.GetProperty("recent").GetProperty("listed").GetBoolean(), l => l, "최근 측정 목록"); break;
            case "recentReady":
                app.WaitDiag(d => d.GetProperty("recent").GetProperty("listed").GetBoolean()
                    && d.GetProperty("recent").GetProperty("items").EnumerateArray().All(x => !x.GetProperty("Analyzing").GetBoolean()), r => r, "최근 측정 분석");
                break;
            case "firstrun":
                app.WaitDiag(d => d.GetProperty("screenDiag").ValueKind == JsonValueKind.Object ? M5Fixtures.Str(d, "screenDiag", "status") : "", s => s == value, "탐지 " + value);
                break;
            case "detecting":
                app.WaitDiag(d => d.GetProperty("screenDiag").ValueKind == JsonValueKind.Object && d.GetProperty("screenDiag").GetProperty("detecting").GetBoolean(), b => b, "탐지 중");
                break;
            case "startError": app.WaitDiag(d => M5Fixtures.Str(d, "startError"), s => s.Length > 0, "시작 실패"); break;
            case "finished": app.WaitDiag(d => d.GetProperty("finished").ValueKind == JsonValueKind.Object, f => f, "측정 끝"); break;
            case "dialog": app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "대화상자"); break;
            case "dirty": app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("dirty").GetBoolean(), o => o, "편집 변경"); break;
            case "memo": app.WaitDiag(d => d.GetProperty("screenDiag").GetProperty("memoOpen").GetBoolean(), o => o, "메모 패널"); break;
            case "marksaved":
                app.WaitDiag(d => d.GetProperty("marks").EnumerateArray().Any(m => M5Fixtures.Str(m, "Memo").Length > 0 && M5Fixtures.Str(m, "state") == "Saved"), s => s, "메모 저장");
                break;
            // M6 진단 화면 (screenDiag = DiagnoseScreen.Diag)
            case "diagdone":
                app.WaitDiag(d => Sd(d) is { } s && s.GetProperty("operation").GetString() == "None" && s.GetProperty("conclusion").ValueKind == JsonValueKind.Number, b => b, "진단 끝");
                break;
            case "diagpartial": app.WaitDiag(d => Sd(d) is { } s ? s.GetProperty("partial").GetInt32() : 0, n => n >= int.Parse(value), "끝난 검사 " + value); break;
            case "diag": app.WaitDiag(d => Sd(d) is { } s ? s.GetProperty("operation").GetString() : "", o => o == value, "진단 작업 " + value); break;
            case "flushstage": app.WaitDiag(d => Sd(d) is { } s ? M5Fixtures.Str(s, "flush") : "", f => f == value, "비우기 단계 " + value); break;
            case "flushdone":
                app.WaitDiag(d => Sd(d) is { } s && s.GetProperty("operation").GetString() == "None" && s.GetProperty("flushResult").ValueKind == JsonValueKind.String, b => b, "비우기 흐름 끝");
                break;
            case "diagrecent": app.WaitDiag(d => Sd(d) is { } s && s.GetProperty("recent").ValueKind == JsonValueKind.Number, b => b, "지난 진단 기록"); break;
            case "site": app.WaitDiag(d => Sd(d) is { } s && s.GetProperty("site").GetProperty("dns").ValueKind == JsonValueKind.String, b => b, "사이트 점검 결과"); break;
            case "siterunning": app.WaitDiag(d => Sd(d) is { } s && s.GetProperty("site").GetProperty("Running").GetBoolean(), b => b, "사이트 점검 중"); break;
            case "siteerror": app.WaitDiag(d => Sd(d) is { } s && s.GetProperty("site").GetProperty("InputError").ValueKind == JsonValueKind.String, b => b, "입력 오류"); break;
            case "exists": AppDriver.WaitFor(() => app.Exists(value), e => e, "요소 " + value); break;
            default: throw new ArgumentException("알 수 없는 기다림: " + what);
        }
    }

    /// <summary>현재 화면의 진단 값 (없으면 null).</summary>
    private static JsonElement? Sd(JsonElement d) => d.TryGetProperty("screenDiag", out var s) && s.ValueKind == JsonValueKind.Object ? s : null;

    /// <summary>배치 검사 판정 (M2 설계 5.8과 같음): 스크롤 밖 잘림 0, 스크롤 안은 보이는 요소의 가로가 보기 안, 글자 잘림 0, 같은 층 상호작용 요소 겹침 0.</summary>
    internal static List<string> LayoutProblems(JsonElement data)
    {
        var report = data.GetProperty("report");
        var problems = new List<string>();
        foreach (var t in report.GetProperty("trimmed").EnumerateArray()) problems.Add("글자 잘림 " + t.GetString());
        foreach (var o in report.GetProperty("overlaps").EnumerateArray()) problems.Add("겹침 " + o.GetString());
        foreach (var item in report.GetProperty("items").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            double w = item.GetProperty("w").GetDouble(), h = item.GetProperty("h").GetDouble();
            double vw = item.GetProperty("vw").GetDouble(), vh = item.GetProperty("vh").GetDouble();
            bool inScroll = item.GetProperty("inScroll").GetBoolean();
            if (!inScroll)
            {
                if (Math.Abs(vw - w) > 1) problems.Add($"{id} 가로 잘림 {vw:0}/{w:0}");
                if (Math.Abs(vh - h) > 1) problems.Add($"{id} 세로 잘림 {vh:0}/{h:0}");
            }
            else if (vh > 0 && Math.Abs(vw - w) > 1) problems.Add($"{id} 스크롤 안 가로 잘림 {vw:0}/{w:0}");
        }
        return problems;
    }

    [Theory, MemberData(nameof(Matrix))]
    public void SceneLooksRightAndFits(string id, string theme, string scale)
    {
        var scene = Scenes().Single(s => s.Id == id);
        using var app = Open(scene, "--theme", theme, "--ui-scale", scale);
        if (!id.StartsWith("dialog", StringComparison.Ordinal)) app.CloseToasts();
        Thread.Sleep(700);
        using var shot = app.CaptureBitmap();
        string name = $"m5-{id}-{theme}-{(scale == "1" ? "100" : "150")}";
        var path = app.Save(shot, "matrix-" + name);
        var layout = app.LayoutNow();
        var problems = LayoutProblems(layout);
        var diag = app.Diag();
        Record("matrix-" + name, new { name, scene = id, theme, scale, file = Path.GetFileName(path), w = shot.Width, h = shot.Height, layoutProblems = problems, diag });
        Assert.Equal((Size.WindowWidth, Size.WindowHeight), (shot.Width, shot.Height));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(app.ReadLog().All(e => e.GetProperty("kind").GetString() is not ("unhandled" or "renderFailed" or "diagFailed")), "앱 기록에 오류");
        app.Close();
    }

    // ---------------- 추가 장면 (설계 7.4) ----------------

    public static TheoryData<string, string, string> Extras() => new()
    {
        { "m5-home-outage-hc-aquatic", "home-outage", "--simulate-high-contrast aquatic" },
        { "m5-home-outage-hc-desert", "home-outage", "--simulate-high-contrast desert" },
        { "m5-measure-outage-hc-aquatic", "measure-outage", "--simulate-high-contrast aquatic" },
        { "m5-measure-outage-hc-desert", "measure-outage", "--simulate-high-contrast desert" },
        { "m5-home-outage-dark-reduced", "home-outage", "--theme dark --simulate-transparency-off" },
        { "m5-home-outage-light-reduced", "home-outage", "--theme light --simulate-transparency-off" },
        { "m5-measure-live-dark-reducedmotion", "measure-live", "--theme dark --simulate-animations-off" },
        { "m5-measure-live-light-reducedmotion", "measure-live", "--theme light --simulate-animations-off" },
    };

    [Theory, MemberData(nameof(Extras))]
    public void ExtraSceneRenders(string name, string sceneId, string extra)
    {
        var scene = Scenes().Single(s => s.Id == sceneId);
        using var app = Open(scene, extra.Split(' '));
        app.CloseToasts();
        Thread.Sleep(700);
        using var shot = app.CaptureBitmap();
        var path = app.Save(shot, "matrix-" + name);
        // 추가 장면도 같은 배치 검사 (G4 M5 1라운드 C4)
        var problems = LayoutProblems(app.LayoutNow());
        Record("matrix-" + name, new { name, scene = sceneId, extra, file = Path.GetFileName(path), layoutProblems = problems, diag = app.Diag() });
        Assert.Equal((Size.WindowWidth, Size.WindowHeight), (shot.Width, shot.Height));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(app.ReadLog().All(e => e.GetProperty("kind").GetString() is not ("unhandled" or "renderFailed" or "diagFailed")), "앱 기록에 오류");
        app.Close();
    }

    public static TheoryData<string, string> Transitions()
    {
        var d = new TheoryData<string, string>();
        foreach (var p in new[] { "0", "0.5", "1" })
        {
            d.Add("crossfade", p);
            d.Add("sharedmove", p);
        }
        return d;
    }

    /// <summary>화면 전환을 진행도에 멈춰 찍는다: 탐색 교차 페이드(홈 → 측정), 공통 요소 이동(홈의 최근 2분 → 측정 화면 타임라인, 측정 시작).</summary>
    [Theory, MemberData(nameof(Transitions))]
    public void TransitionFrame(string kind, string progress)
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "600", "--demo-hold", "30", "--screen", "home", "--freeze-motion", "--theme", "dark");
        app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "home", "홈");
        Thread.Sleep(700);   // 첫 화면 등장이 끝난 뒤에 멈춤을 건다 (멈춤은 이후 모션에만)
        app.CloseToasts();
        // 진행도 멈춤은 실행 인수로만 켤 수 있어, 같은 사용자 폴더로 다시 띄운다
        app.Close();
        using var held = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "600", "--demo-hold", "30", "--screen", "home", "--freeze-motion", "--theme", "dark", "--motion-hold", progress);
        held.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "home", "홈");
        if (kind == "crossfade") held.Select("nav.measure");
        else held.Invoke("home.start");
        held.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "measure", "측정 화면");
        Thread.Sleep(500);
        using var shot = held.CaptureBitmap();
        string name = $"m5-transition-{kind}-{progress}";
        var path = held.Save(shot, "matrix-" + name);
        Record("matrix-" + name, new { name, kind, progress, file = Path.GetFileName(path), diag = held.Diag() });
        held.Close();
        AppDriver.DeleteUserData(userData);
    }
}
