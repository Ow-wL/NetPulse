using System.IO;
using System.Text.Json;
using System.Windows.Automation;

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
        var box = app.Find($"legal.text.{index}");
        var scroll = (ScrollPattern?)(box.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) ? p : null);
        bool scrolled = false;
        if (scroll is { Current.VerticallyScrollable: true }) { scroll.SetScrollPercent(ScrollPattern.NoScroll, 100); scrolled = AppDriver.WaitFor(() => scroll.Current.VerticalScrollPercent, v => v >= 99, "끝까지") >= 99; }
        File.WriteAllText(Path.Combine(G5Dir, "bench-legal.json"), JsonSerializer.Serialize(new { index, chars, firstLayoutMs = ms, scrolledToEnd = scrolled }));
        Assert.True(chars > 300_000, $"원문 {chars}자");
        Assert.True(scrolled, "끝까지 스크롤");
        Assert.True(ms <= 500, $"펼친 원문 첫 배치 {ms} ms");                                 // 기준: DECISIONS "M10 고지 bench"
        Assert.Equal(0, app.Close());
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
