using System.IO;
using System.Text.Json;
using Size = NetPulse.Design.Tokens.Size;

namespace NetPulse.UiTests;

/// <summary>
/// M11 G5 (설계 C1, 장면 포함표 `docs/loop/M11-completion.md`): 모든 화면·대화상자가 각 대체 모드(고대비 aquatic·desert, 투명 효과 끔,
/// 움직임 줄이기, 고대비 + 150%)에서 대표 상태 하나 이상을 갖도록 빈칸을 채운다. 장면은 기존 목록(m5~m10)의 것을 그대로 쓰고,
/// 새 기본 상태(첫 실행 IPv4 경로 없음)만 m11-scenes.txt에 둔다. 전환 프레임: 설정 화면 진입, 동의 → 셸.
/// </summary>
public sealed class M11MatrixTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private const string HcA = "--simulate-high-contrast aquatic", HcD = "--simulate-high-contrast desert";
    private const string ToDark = "--theme dark --simulate-transparency-off", ToLight = "--theme light --simulate-transparency-off";
    private const string RmDark = "--theme dark --simulate-animations-off", RmLight = "--theme light --simulate-animations-off";
    private const string Hc150 = "--simulate-high-contrast aquatic --ui-scale 1.5";

    public static TheoryData<string, string, string> Matrix()
    {
        var d = new TheoryData<string, string, string>();
        foreach (var s in M5MatrixTests.Scenes("m11-scenes.txt"))
            foreach (var t in new[] { "dark", "light" })
                foreach (var scale in new[] { "1", "1.5" })
                    d.Add(s.Id, t, scale);
        return d;
    }

    /// <summary>장면 포함표의 "추가" 칸 (이름, 장면 목록 파일, 장면, 인수).</summary>
    public static TheoryData<string, string, string, string> Extras() => new()
    {
        // 첫 실행
        { "m11-firstrun-detected-hc-aquatic", "m5-scenes.txt", "firstrun-detected", HcA },
        { "m11-firstrun-detected-hc-desert", "m5-scenes.txt", "firstrun-detected", HcD },
        { "m11-firstrun-detected-dark-reduced", "m5-scenes.txt", "firstrun-detected", ToDark },
        { "m11-firstrun-detected-light-reduced", "m5-scenes.txt", "firstrun-detected", ToLight },
        { "m11-firstrun-detecting-dark-reducedmotion", "m5-scenes.txt", "firstrun-detecting", RmDark },
        { "m11-firstrun-detecting-light-reducedmotion", "m5-scenes.txt", "firstrun-detecting", RmLight },
        { "m11-firstrun-detected-hc-150", "m5-scenes.txt", "firstrun-detected", Hc150 },
        // 홈
        { "m11-home-measuring-dark-reducedmotion", "m5-scenes.txt", "home-measuring", RmDark },
        { "m11-home-measuring-light-reducedmotion", "m5-scenes.txt", "home-measuring", RmLight },
        { "m11-home-outage-hc-150", "m5-scenes.txt", "home-outage", Hc150 },
        // 측정
        { "m11-measure-outage-dark-reduced", "m5-scenes.txt", "measure-outage", ToDark },
        { "m11-measure-outage-light-reduced", "m5-scenes.txt", "measure-outage", ToLight },
        { "m11-measure-outage-hc-150", "m5-scenes.txt", "measure-outage", Hc150 },
        // 프로필 편집기
        { "m11-profile-ok-hc-aquatic", "m5-scenes.txt", "profile-ok", HcA },
        { "m11-profile-ok-hc-desert", "m5-scenes.txt", "profile-ok", HcD },
        { "m11-profile-ok-dark-reduced", "m5-scenes.txt", "profile-ok", ToDark },
        { "m11-profile-ok-light-reduced", "m5-scenes.txt", "profile-ok", ToLight },
        { "m11-profile-ok-hc-150", "m5-scenes.txt", "profile-ok", Hc150 },
        // 진단
        { "m11-diagnose-dns-hc-150", "m6-scenes.txt", "diagnose-dns", Hc150 },
        // 기록 목록·상세·비교·진단 기록
        { "m11-records-list-problems-dark-reduced", "m7-scenes.txt", "records-list-problems", ToDark },
        { "m11-records-list-problems-light-reduced", "m7-scenes.txt", "records-list-problems", ToLight },
        { "m11-records-detail-outages-hc-150", "m7-scenes.txt", "records-detail-outages", Hc150 },
        { "m11-records-compare-hc-aquatic", "m7-scenes.txt", "records-compare", HcA },
        { "m11-records-compare-hc-desert", "m7-scenes.txt", "records-compare", HcD },
        { "m11-records-compare-dark-reduced", "m7-scenes.txt", "records-compare", ToDark },
        { "m11-records-diagnoses-hc-aquatic", "m7-scenes.txt", "records-diagnoses", HcA },
        { "m11-records-diagnoses-light-reduced", "m7-scenes.txt", "records-diagnoses", ToLight },
        // 리포트·설정·사용 조건
        { "m11-report-preview-hc-150", "m8-scenes.txt", "report-preview", Hc150 },
        { "m11-settings-normal-hc-150", "m9-scenes.txt", "settings-normal", Hc150 },
        { "m11-terms-hc-150", "m10-scenes.txt", "terms", Hc150 },
        // 대화상자
        { "m11-dialog-unsaved-hc-aquatic", "m5-scenes.txt", "dialog-unsaved", HcA },
        { "m11-dialog-unsaved-hc-desert", "m5-scenes.txt", "dialog-unsaved", HcD },
        { "m11-dialog-unsaved-dark-reduced", "m5-scenes.txt", "dialog-unsaved", ToDark },
        { "m11-diagnose-flush-confirm-hc-aquatic", "m6-scenes.txt", "diagnose-flush-confirm", HcA },
        { "m11-diagnose-flush-confirm-hc-desert", "m6-scenes.txt", "diagnose-flush-confirm", HcD },
        { "m11-diagnose-flush-confirm-light-reduced", "m6-scenes.txt", "diagnose-flush-confirm", ToLight },
        { "m11-hint-dialog-hc-aquatic", "m9-scenes.txt", "hint-dialog", HcA },
        { "m11-hint-dialog-dark-reduced", "m9-scenes.txt", "hint-dialog", ToDark },
        { "m11-settings-move-confirm-hc-aquatic", "m9-scenes.txt", "settings-move-confirm", HcA },
        { "m11-settings-move-confirm-hc-desert", "m9-scenes.txt", "settings-move-confirm", HcD },
        { "m11-settings-move-confirm-dark-reduced", "m9-scenes.txt", "settings-move-confirm", ToDark },
        { "m11-about-legal-expanded-hc-150", "m10-scenes.txt", "about-legal-expanded", Hc150 },
    };

    private static void Shoot(string name, string file, string sceneId, string[] extra, object meta)
    {
        var scene = M5MatrixTests.Scenes(file).Single(s => s.Id == sceneId);
        using var app = M5MatrixTests.Open(scene, extra);
        // 대화상자 장면은 알림 닫기가 대화상자 단추와 겹치지 않게 그대로 둔다 (M9·M10 매트릭스와 같게)
        if (!sceneId.StartsWith("terms", StringComparison.Ordinal) && !sceneId.StartsWith("about-legal", StringComparison.Ordinal)
            && !sceneId.Contains("confirm", StringComparison.Ordinal) && sceneId != "hint-dialog") app.CloseToasts();
        Thread.Sleep(700);
        using var shot = app.CaptureBitmap();
        var path = app.Save(shot, "matrix-" + name);
        var problems = M5MatrixTests.LayoutProblems(app.LayoutNow());
        var privacy = M9MatrixTests.PrivateText(app.Window);
        Record("matrix-" + name, new { name, sceneFile = file, scene = sceneId, meta, file = Path.GetFileName(path), w = shot.Width, h = shot.Height, layoutProblems = problems, privacy, diag = app.Diag() });
        Assert.Equal((Size.WindowWidth, Size.WindowHeight), (shot.Width, shot.Height));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(privacy.Count == 0, string.Join("\n", privacy));
        Assert.True(app.ReadLog().All(e => e.GetProperty("kind").GetString() is not ("unhandled" or "renderFailed" or "diagFailed")), "앱 기록에 오류");
        if (sceneId == "hint-dialog")
        {
            // 처음 숨김 안내가 창 닫기를 막고 있다: [측정 멈추고 종료]로 끝낸다 (M9MatrixTests.Finish와 같게, 노트북 게이트 1차의 닫기 시간 초과)
            app.Invoke("dialog.alternate");
            Assert.Equal(0, app.WaitExit(TimeSpan.FromSeconds(20)));
        }
        else Assert.Equal(0, app.Close());
    }

    [Theory, MemberData(nameof(Matrix))]
    public void SceneLooksRightAndFits(string id, string theme, string scale) =>
        Shoot($"m11-{id}-{theme}-{(scale == "1" ? "100" : "150")}", "m11-scenes.txt", id, ["--theme", theme, "--ui-scale", scale], new { theme, scale });

    [Theory, MemberData(nameof(Extras))]
    public void ExtraSceneRenders(string name, string file, string sceneId, string extra) => Shoot(name, file, sceneId, extra.Split(' '), new { extra });

    public static TheoryData<string> Progress => new() { "0", "0.5", "1" };

    /// <summary>홈 → 설정 탐색 교차 페이드를 진행도에 멈춰 찍는다 (M6·M7 전환 시험과 같은 방식). 전환 프레임에는 배치 검사가 없다 (설계 C3 [2-3]).</summary>
    [Theory, MemberData(nameof(Progress))]
    public void HomeToSettingsCrossfade(string progress)
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "home", "--freeze-motion", "--theme", "dark", "--motion-hold", progress);
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "home", "홈");
            app.Select("nav.settings");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "settings", "설정 화면");
            Thread.Sleep(500);
            using var shot = app.CaptureBitmap();
            string name = $"m11-transition-settings-{progress}";
            var path = app.Save(shot, "matrix-" + name);
            Record("matrix-" + name, new { name, kind = "crossfade", progress, file = Path.GetFileName(path), diag = app.Diag() });
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>
    /// 동의 → 셸: 동의 창이 닫히고 셸 창이 뜬 직후를 진행도에 멈춰 찍는다. 셸 창 첫 화면의 들어오기 모션이 멈춘 모습이다
    /// (모션이 없으면 세 장이 같다 — 그것도 기록한다).
    /// </summary>
    [Theory, MemberData(nameof(Progress))]
    public void ConsentToShellEntry(string progress)
    {
        using var app = AppDriver.LaunchUnconsented(null, "--demo", "steady", "--screen", "home", "--freeze-motion", "--theme", "dark", "--motion-hold", progress);
        app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "terms", "사용 조건 화면");
        app.Invoke("terms.accept");
        app.FollowNextWindow();
        app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "home", "홈");
        Thread.Sleep(500);
        using var shot = app.CaptureBitmap();
        string name = $"m11-transition-consent-{progress}";
        var path = app.Save(shot, "matrix-" + name);
        Record("matrix-" + name, new { name, kind = "consent", progress, file = Path.GetFileName(path), diag = app.Diag() });
        Assert.Equal(0, app.Close());
    }
}
