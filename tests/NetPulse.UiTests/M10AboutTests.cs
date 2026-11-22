using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using NetPulse.Design.Tokens;

namespace NetPulse.UiTests;

/// <summary>M10 정보 카드와 라이선스와 고지 (설계 A1·A3 [1-11][2-7]).</summary>
public sealed class M10AboutTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static AppDriver OpenSettings(params string[] extra)
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady", TimeSpan.FromMinutes(60));
        var app = AppDriver.LaunchWith(userData, ["--demo", "steady", "--screen", "settings", .. extra]);
        app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "settings", "설정");
        return app;
    }

    private static string Chip(AppDriver app, string id) => app.Text($"settings.about.{id}.value");

    [Fact]
    public void AboutCardShowsVersionBuildAndLicense()
    {
        using (var app = OpenSettings())
        {
            Assert.Equal("1.0.0", Chip(app, "version"));
            Assert.Equal("데모 실행", Chip(app, "build"));                                     // 데모는 캡처가 커밋마다 바뀌지 않게
            Assert.Equal("MIT", Chip(app, "license"));
            Assert.Equal(0, app.Close());
        }
        using (var app = OpenSettings("--fake-version", "1.0.0+762addc822e7e6865a456fe4b68330e3e0926119"))
        {
            Assert.Equal("762addc", Chip(app, "build"));
            Assert.Equal(0, app.Close());
        }
        using (var app = OpenSettings("--fake-version", "1.0.0"))
        {
            Assert.Equal("알 수 없음", Chip(app, "build"));
            Assert.Equal(0, app.Close());
        }
    }

    [Fact]
    public void LegalDialogListsComponentsExpandsOneAndReturnsFocus()
    {
        using var app = OpenSettings();
        app.Invoke("settings.about.legal");
        app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "대화상자");
        int count = 0;
        while (app.Exists($"legal.item.{count}")) count++;
        Assert.True(count >= 12, $"항목 {count}개");                                          // 사용 조건, NetPulse, 구성요소 10개
        Assert.Contains("사용 조건", app.Text("legal.item.0"), StringComparison.Ordinal);
        Assert.Contains("MIT", app.Text("legal.item.1"), StringComparison.Ordinal);
        app.Invoke("legal.item.1");
        var text = (ValuePattern)app.Find("legal.text.1").GetCurrentPattern(ValuePattern.Pattern);
        Assert.True(text.Current.IsReadOnly);
        Assert.Contains("Permission is hereby granted", text.Current.Value, StringComparison.Ordinal);
        app.Invoke("legal.item.1");                                                         // 다시 누르면 접힘
        AppDriver.WaitFor(() => app.Exists("legal.text.1"), e => !e, "접힘");
        app.Invoke("dialog.cancel");
        app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => !o, "닫힘");
        AppDriver.WaitFor(() => { try { return AutomationElement.FocusedElement?.Current.AutomationId; } catch (ElementNotAvailableException) { return null; } },
            id => id == "settings.about.legal", "초점이 연 단추로");
        Assert.Equal(0, app.Close());
    }

    /// <summary>bench (설계 5절): 가장 긴 원문(Windows App SDK 구성요소 전체 고지)을 펼쳐 처음 배치까지의 시간과 끝까지 스크롤.</summary>
    [Fact]
    public void TheLongestNoticeOpensAndScrollsToTheEnd()
    {
        using var app = OpenSettings();
        app.Invoke("settings.about.legal");
        app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "대화상자");
        int index = -1;
        for (int i = 0; app.Exists($"legal.item.{i}"); i++)
            if (app.Text($"legal.item.{i}").Contains("구성요소 전체", StringComparison.Ordinal)) index = i;
        Assert.True(index >= 0, "Windows App SDK 구성요소 전체 고지 항목");
        app.Invoke($"legal.item.{index}");
        var log = AppDriver.WaitFor(() => app.ReadLog().LastOrDefault(e => e.GetProperty("kind").GetString() == "legal" && e.GetProperty("data").GetProperty("step").GetString() == "expanded"),
            e => e.ValueKind == JsonValueKind.Object, "펼침 기록").GetProperty("data");
        double ms = log.GetProperty("ms").GetDouble();
        int chars = log.GetProperty("chars").GetInt32();
        var frames = AppDriver.WaitFor(() => app.ReadLog().LastOrDefault(e => e.GetProperty("kind").GetString() == "legal" && e.GetProperty("data").GetProperty("step").GetString() == "frames"),
            e => e.ValueKind == JsonValueKind.Object, "펼친 뒤 프레임").GetProperty("data");
        double maxGap = frames.GetProperty("maxMs").GetDouble();
        int over33 = frames.GetProperty("over33").GetInt32();
        // M12 설계 G2: 창 모니터의 주사율로 "긴 간격"을 세고 지금 프로필의 고지 상한을 쓴다 (standard 판정도 남긴다)
        double? hz = frames.TryGetProperty("hz", out var hzProp) && hzProp.ValueKind == JsonValueKind.Number ? hzProp.GetDouble() : null;
        // 측정 앞뒤 주사율이 다르거나 앞에서 못 읽었으면 앱이 hz를 비우고 refreshChanged를 남긴다 (G4 M12 1라운드 6)
        bool refreshChanged = frames.TryGetProperty("refreshChanged", out var rc) && rc.GetBoolean();
        Assert.False(refreshChanged, "고지 측정 중 모니터·주사율이 바뀌었거나 측정 앞에서 읽지 못함 (앞 " + (frames.TryGetProperty("hzBefore", out var hb) ? hb.ToString() : "없음") + ")");
        Assert.True(hz is > 0, "주사율을 확정하지 못함 (출처 " + (frames.TryGetProperty("refreshSource", out var rs) ? rs.GetString() : "없음") + ")");
        var gaps = frames.GetProperty("gaps").EnumerateArray().Select(g => g.GetDouble()).ToArray();
        var limits = FrameContract.For(Perf.Profile, hz!.Value);
        var standard = FrameContract.For(PerfProfile.Standard, hz.Value);
        int longGaps = gaps.Count(g => g > limits.LongGapMs);
        // 긴 원문은 문단 목록 (설계 5절): 목록의 ScrollPattern으로 끝까지 내리고 마지막 문단이 만들어졌는지 본다
        var list = app.Find($"legal.text.{index}");
        Assert.Equal(ControlType.List, list.Current.ControlType);
        // [전문 복사] 단추는 목록 바로 다음 형제로 찾는다: 창 전체 하위 검색은 가상화 목록의 줄을 모두 만들게 해 UIA가 시간 초과했다 (UI 시험 1차). 누르지는 않는다 (사용자 클립보드)
        var sibling = TreeWalker.ControlViewWalker.GetNextSibling(list);
        Assert.Equal($"legal.copy.{index}", sibling?.Current.AutomationId);
        var scroll = (ScrollPattern)list.GetCurrentPattern(ScrollPattern.Pattern);
        scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
        bool scrolled = AppDriver.WaitFor(() => scroll.Current.VerticalScrollPercent, v => v >= 99.9, "끝까지") >= 99.9;
        bool standardPass = ms <= standard.NoticeFirstLayout && maxGap <= standard.NoticeMax && gaps.Count(g => g > standard.LongGapMs) <= standard.NoticeLongGaps;
        File.WriteAllText(Path.Combine(G5Dir, "bench-legal.json"), JsonSerializer.Serialize(new
        {
            index, chars, firstLayoutMs = ms, maxFrameMs = maxGap, over33, samples = frames.GetProperty("samples").GetInt32(), scrolledToEnd = scrolled,
            contract = new { limits.Version, profile = limits.Profile.ToString(), hz, limits.LongGapMs, longGaps, applied = new { limits.NoticeFirstLayout, limits.NoticeMax, limits.NoticeLongGaps }, standardPass },
        }));
        Assert.True(chars > 300_000, $"원문 {chars}자");
        Assert.True(scrolled, "끝까지 스크롤");
        Assert.True(frames.GetProperty("samples").GetInt32() >= 10, $"펼친 뒤 1초 프레임 표본 {frames.GetProperty("samples").GetInt32()}개 (10개 미만, 측정 무효)");
        Assert.True(ms <= limits.NoticeFirstLayout, $"펼친 원문 첫 배치 {ms} ms > {limits.NoticeFirstLayout} ({limits.Profile})");   // M10 설계 5절 기준, M12 G2 프로필
        Assert.True(maxGap <= limits.NoticeMax, $"펼친 뒤 1초 최대 간격 {maxGap} ms > {limits.NoticeMax} ({limits.Profile})");
        Assert.True(longGaps <= limits.NoticeLongGaps, $"펼친 뒤 1초 긴 간격(> {limits.LongGapMs} ms) {longGaps}회 > {limits.NoticeLongGaps} (최대 {maxGap} ms, {limits.Profile})");
        Assert.Equal(0, app.Close());
    }

    /// <summary>고지를 펼친 직후(1초 프레임 측정 중) 트레이로 숨기면 측정 구독이 풀리고 숨긴 뒤 구독 수가 0 (G4 M10 1라운드 b2).</summary>
    [Fact]
    public void HidingRightAfterExpandingANoticeLeavesNoFrameHook()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        M9ResidentTests.HintAlreadyShown(userData);
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "20", "--start", "--screen", "settings", "--close-to-tray", "on");
            app.WaitDiag(d => M5Fixtures.Str(d, "host"), h => h == "Measuring", "측정 중");
            app.Invoke("settings.about.legal");
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "대화상자");
            app.Invoke("legal.item.1");
            app.RequestClose();                                                     // 펼친 지 1초 안에 숨김
            M9ResidentTests.WaitHidden(app);                                        // 숨긴 뒤 프레임 구독 0 (숨김 기록)
            var hidden = app.ReadLog().Last(e => e.GetProperty("kind").GetString() == "resident" && e.GetProperty("data").GetProperty("what").GetString() == "hidden");
            Thread.Sleep(1500);
            // 숨기며 그만둔 측정은 결과를 남기지 않는다 (숨긴 뒤 렌더링 콜백이 오지 않았다는 뜻이기도 하다)
            Assert.DoesNotContain(app.ReadLog(), e => e.GetProperty("kind").GetString() == "legal" && e.GetProperty("data").GetProperty("step").GetString() == "frames"
                && e.GetProperty("t").GetDateTimeOffset() > hidden.GetProperty("t").GetDateTimeOffset());
            Assert.Equal((0, 0), app.Exit());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void UnreadableNoticesShowTheFailedState()
    {
        using var app = OpenSettings("--notices-fail");
        app.Invoke("settings.about.legal");
        app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "대화상자");
        Assert.True(app.Exists("legal.failed"));
        Assert.False(app.Exists("legal.item.0"));
        Assert.Equal(0, app.Close());
    }
}
