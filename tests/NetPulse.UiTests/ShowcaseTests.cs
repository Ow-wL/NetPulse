using System.Text.Json;

[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]   // 모든 UI 테스트를 한 묶음으로 → 직렬

namespace NetPulse.UiTests;

/// <summary>
/// 부품 전시 화면 (M2 설계 7.3). 조작은 UI Automation 패턴만 쓰고 키를 보내지 않는다.
/// 같은 데스크톱을 쓰므로 어셈블리 전체를 직렬로 돈다.
/// </summary>
public sealed class ShowcaseTests
{
    private static readonly string[] Sections =
        ["overview", "colors", "materials", "contrast", "buttons", "cards", "navigation", "toasts", "dialog", "chart", "motion", "settings"];

    private static AppDriver Showcase(params string[] extra) => AppDriver.Launch(["--showcase", .. extra]);

    private static string S(JsonElement d, string name) => d.GetProperty(name).ValueKind == JsonValueKind.Null ? "" : d.GetProperty(name).ToString();

    [Fact]
    public void EverySectionOpensFromTheNavigation()
    {
        using var app = Showcase("--freeze-motion");
        app.WaitDiag(d => S(d, "section"), s => s == "overview", "개요");
        foreach (var id in Sections)
        {
            app.Select("nav." + id);
            app.WaitDiag(d => S(d, "section"), s => s == id, "절 " + id);
        }
        Assert.Equal(0, app.Close());
        Assert.DoesNotContain(app.ReadLog(), e => e.GetProperty("kind").GetString() == "unhandled");
    }

    [Fact]
    public void ThemeChoiceIsSavedAndComesBack()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using (var app = AppDriver.LaunchWith(userData, "--showcase", "--freeze-motion"))
            {
                app.WaitDiag(d => S(d, "theme"), t => t == "dark", "첫 실행은 다크");
                app.Select("toolbar.theme.light");
                app.WaitDiag(d => S(d, "theme"), t => t == "light", "라이트로");
                Assert.Equal(0, app.Close());
            }
            using (var again = AppDriver.LaunchWith(userData, "--showcase", "--freeze-motion"))
            {
                again.WaitDiag(d => S(d, "theme"), t => t == "light", "다시 실행해도 라이트");
                Assert.Equal(0, again.Close());
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void ReducingMotionAtRuntimeStopsEverythingThatMoves()
    {
        // 반복 모션(배경 빛 흐름, 불러오는 중 윤곽)이 돌고 있는 상태에서 설정을 켠다 (설계 5.3 검사)
        using var app = Showcase("--section", "motion");
        app.WaitDiag(d => d.GetProperty("active").GetProperty("repeat").GetInt32(), n => n > 0, "반복 모션이 돎");
        app.Select("nav.settings");
        app.WaitDiag(d => S(d, "section"), s => s == "settings", "설정 절");
        app.Toggle("settings.reduceMotion");
        var d = app.WaitDiag(x => x, x => x.GetProperty("reduceMotion").GetBoolean(), "움직임 줄이기 켜짐");
        app.WaitDiag(x => x.GetProperty("active"), a => a.GetProperty("move").GetInt32() + a.GetProperty("scale").GetInt32() + a.GetProperty("repeat").GetInt32() == 0, "활성 이동·크기·반복 0");
        Thread.Sleep(300);
        using var first = app.CaptureBitmap();
        Thread.Sleep(500);
        using var second = app.CaptureBitmap();
        var diff = ImageCompare.Diff(first, second);
        Assert.True(diff.Mean <= 1.0 && diff.P99 <= 4, $"움직임 줄이기 뒤에도 화면이 바뀐다: {diff}");
        // 버튼을 눌러도 이동·크기 모션이 생기지 않는다
        var created = app.Diag().GetProperty("created");
        int before = created.GetProperty("move").GetInt32() + created.GetProperty("scale").GetInt32();
        app.Select("nav.buttons");
        app.WaitDiag(x => S(x, "section"), s => s == "buttons", "버튼 절");
        app.Invoke("btn.primary.normal");
        Thread.Sleep(300);
        created = app.Diag().GetProperty("created");
        Assert.Equal(before, created.GetProperty("move").GetInt32() + created.GetProperty("scale").GetInt32());
        Assert.Equal(0, app.Close());
    }

    [Theory]
    [InlineData("--simulate-high-contrast", "aquatic", "highcontrast", false)]
    [InlineData("--simulate-high-contrast", "desert", "highcontrast", false)]
    [InlineData("--simulate-transparency-off", null, "opaque", true)]
    [InlineData("--simulate-energy-saver", null, "opaque", true)]
    [InlineData("--simulate-low-performance", null, "opaque", true)]
    public void SimulatedSystemSettingsTakeTheSamePathAsRealOnes(string flag, string? value, string material, bool lightsVisible)
    {
        string[] args = value is null ? ["--freeze-motion", flag] : ["--freeze-motion", flag, value];
        using var app = Showcase(args);
        var d = app.WaitDiag(x => x, x => S(x, "material") == material, "재질 " + material);
        Assert.False(d.GetProperty("glassAcrylic").GetBoolean());
        Assert.Equal(lightsVisible, d.GetProperty("lightsVisible").GetBoolean());
        Assert.False(d.GetProperty("aurora").GetBoolean());
        Assert.True(d.GetProperty("simulated").GetBoolean());
        Assert.Equal(0, app.Close());
    }

    [Fact]
    public void SimulatedAnimationsOffMeansReducedMotionFromTheStart()
    {
        using var app = Showcase("--simulate-animations-off", "--section", "motion");
        var d = app.WaitDiag(x => x, x => x.GetProperty("reduceMotion").GetBoolean(), "움직임 줄이기");
        Thread.Sleep(500);
        d = app.Diag();
        var active = d.GetProperty("active");
        Assert.Equal(0, active.GetProperty("move").GetInt32() + active.GetProperty("scale").GetInt32() + active.GetProperty("repeat").GetInt32());
        Assert.Equal(0, d.GetProperty("created").GetProperty("move").GetInt32());
        Assert.Equal(0, app.Close());
    }

    [Fact]
    public void DialogFocusesItsFirstButtonAndEscapeReturnsFocus()
    {
        // 탭 순환 가둠은 KeyboardTests가 실제 키로 확인한다 (WinUI 데스크톱에서는 앱 안에서 다음 초점 요소를 계산할 수 없다)
        using var app = Showcase("--freeze-motion", "--selftest", "dialog");
        var r = app.WaitSelfTest("dialog");
        Assert.Equal("dialog.cancel", r.GetProperty("initialFocus").GetString());
        Assert.True(r.GetProperty("closed").GetBoolean());
        Assert.Equal("dialog.open", r.GetProperty("focusAfterClose").GetString());
        Assert.Equal(0, app.Close());
    }

    public static TheoryData<string, string> LayoutCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var s in Sections) { data.Add(s, "1"); data.Add(s, "1.5"); }
        return data;
    }

    [Theory, MemberData(nameof(LayoutCases))]
    public void NothingIsClippedOrOverlapping(string section, string scale)
    {
        // 설계 5.8: 스크롤 밖은 잘림 0, 스크롤 안은 가로가 보기 안, 글자 잘림 0, 상호작용 요소 겹침 0
        using var app = Showcase("--freeze-motion", "--section", section, "--ui-scale", scale, "--selftest", "layout");
        var report = app.WaitSelfTest("layout").GetProperty("report");
        var trimmed = report.GetProperty("trimmed").EnumerateArray().Select(e => e.GetString()).ToArray();
        var overlaps = report.GetProperty("overlaps").EnumerateArray().Select(e => e.GetString()).ToArray();
        var problems = new List<string>();
        foreach (var item in report.GetProperty("items").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            double w = item.GetProperty("w").GetDouble(), h = item.GetProperty("h").GetDouble();
            double vw = item.GetProperty("vw").GetDouble(), vh = item.GetProperty("vh").GetDouble();
            bool inScroll = item.GetProperty("inScroll").GetBoolean();
            if (Math.Abs(vw - w) > 1) problems.Add($"{id} 가로 잘림 {vw:0}/{w:0}");
            if (!inScroll && Math.Abs(vh - h) > 1) problems.Add($"{id} 세로 잘림 {vh:0}/{h:0}");
        }
        Assert.True(trimmed.Length == 0, "글자 잘림: " + string.Join(", ", trimmed));
        Assert.True(overlaps.Length == 0, "겹침: " + string.Join(", ", overlaps));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Equal(0, app.Close());
    }
}
