using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using NetPulse.Design.Settings;

namespace NetPulse.UiTests;

/// <summary>
/// M10 사용 조건 동의 (설계 A2 [1-1][2-1][2-2][2-6]). 동의 전에는 세계·기록·설정·트레이·셸이 없고, 동의 뒤 명령줄 요청이 한 번 실행된다.
/// 데모도 동의를 거친다 (시험은 <see cref="AppDriver.LaunchUnconsented"/>로 동의 기록 없이 띄운다).
/// </summary>
public sealed class M10TermsTests
{
    private static string Screen(JsonElement d) => d.TryGetProperty("screen", out var s) ? s.GetString() ?? "" : "";
    private static string[] Kinds(AppDriver app) => [.. app.ReadLog().Select(e => e.GetProperty("kind").GetString()!)];

    /// <summary>동의 전에 만들어지면 안 되는 것: 설정·측정 구성·기록·진단 폴더와 시작 기록 (앱 기록 파일 하나만 있다).</summary>
    private static void AssertNothingStarted(AppDriver app)
    {
        var kinds = Kinds(app);
        foreach (var k in new[] { "startup", "settings", "records", "detection" }) Assert.DoesNotContain(k, kinds);
        foreach (var name in new[] { SettingsFormat.FileName, "profiles.json", "records", "diagnoses", "demo-fallback", TermsConsent.FileName })
            Assert.False(File.Exists(Path.Combine(app.UserData, name)) || Directory.Exists(Path.Combine(app.UserData, name)), name + "이(가) 동의 전에 생겼다");
    }

    [Fact]
    public void TermsComeFirstAndCommandLineRunsOnceAfterConsent()
    {
        using var app = AppDriver.LaunchUnconsented(null, "--demo", "steady", "--start");
        app.WaitDiag(Screen, s => s == "terms", "사용 조건 화면");
        Assert.True(app.Diag().GetProperty("canAccept").GetBoolean());
        Thread.Sleep(1500);   // 동의 전에 측정이 시작되지 않는지 잠깐 지켜본다
        AssertNothingStarted(app);
        app.Invoke("terms.accept");
        app.FollowNextWindow();
        app.WaitDiag(d => M5Fixtures.Str(d, "host"), h => h == "Measuring", "동의 뒤 --start");
        Assert.Equal("measure", Screen(app.Diag()));
        Assert.True(TermsConsent.IsAccepted(app.UserData));
        var kinds = Kinds(app);
        Assert.Single(kinds, k => k == "startup");                                  // 보류한 요청은 한 번만
        Assert.Contains(app.ReadLog(), e => e.GetProperty("kind").GetString() == "terms" && e.GetProperty("data").GetProperty("step").GetString() == "accepted");
        Assert.Equal(0, app.Close());
    }

    [Fact]
    public void DecliningExitsCleanlyWithoutRecordsOrSettings()
    {
        using var app = AppDriver.LaunchUnconsented(null, "--demo", "steady");
        app.WaitDiag(Screen, s => s == "terms", "사용 조건 화면");
        app.Invoke("terms.decline");
        Assert.Equal(0, app.WaitExit(TimeSpan.FromSeconds(20)));
        AssertNothingStarted(app);
    }

    [Fact]
    public void ClosingTheTermsWindowIsDeclining()
    {
        using var app = AppDriver.LaunchUnconsented(null, "--demo", "steady");
        app.WaitDiag(Screen, s => s == "terms", "사용 조건 화면");
        Assert.Equal(0, app.Close());
        AssertNothingStarted(app);
    }

    [Fact]
    public void ConsentIsRememberedForTheNextRun()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            using (var first = AppDriver.LaunchUnconsented(userData, "--demo", "steady", "--screen", "home"))
            {
                first.WaitDiag(Screen, s => s == "terms", "사용 조건 화면");
                first.Invoke("terms.accept");
                first.FollowNextWindow();
                first.WaitDiag(Screen, s => s == "home", "홈");
                Assert.Equal(0, first.Close());
            }
            using var second = AppDriver.LaunchUnconsented(userData, "--demo", "steady", "--screen", "home");
            second.WaitDiag(Screen, s => s == "home", "다시 묻지 않고 홈");
            Assert.False(second.Exists("terms.accept"));
            Assert.Equal(0, second.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void ConsentThatCannotBeSavedIsUsedOnceAndAskedAgain()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.Prepare(userData, "termsblocked");
        try
        {
            using (var app = AppDriver.LaunchUnconsented(userData, "--demo", "steady", "--screen", "home"))
            {
                app.WaitDiag(Screen, s => s == "terms", "사용 조건 화면");
                app.Invoke("terms.accept");
                app.FollowNextWindow();
                app.WaitDiag(Screen, s => s == "home", "홈");
                Assert.True(app.Exists("home.termsnotsaved"));                           // 셸로 넘어간 뒤에도 남는 안내
                Assert.Contains(app.ReadLog(), e => e.GetProperty("kind").GetString() == "terms" && e.GetProperty("data").TryGetProperty("saved", out var s) && !s.GetBoolean());
                Assert.Equal(0, app.Close());
            }
            using var again = AppDriver.LaunchUnconsented(userData, "--demo", "steady");
            again.WaitDiag(Screen, s => s == "terms", "다음 실행에 다시 묻는다");
            Assert.Equal(0, again.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    [Fact]
    public void SecondRunDuringTermsBringsItForwardAndExitEndsIt()
    {
        using var app = AppDriver.LaunchUnconsented(null, "--demo", "steady");
        app.WaitDiag(Screen, s => s == "terms", "사용 조건 화면");
        Assert.Equal(0, app.RunSecond("--demo", "steady"));                          // 열기 요청은 동의 화면이 받는다
        Assert.Equal("terms", Screen(app.Diag()));
        AssertNothingStarted(app);
        Assert.Equal(0, app.RunSecond("--exit"));
        Assert.Equal(0, app.WaitExit(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void UnreadableTermsCannotBeAccepted()
    {
        using var app = AppDriver.LaunchUnconsented(null, "--demo", "steady", "--notices-fail");
        app.WaitDiag(Screen, s => s == "terms", "사용 조건 화면");
        Assert.False(app.Diag().GetProperty("canAccept").GetBoolean());
        Assert.True(app.Exists("terms.failed"));
        Assert.False(app.Find("terms.accept").Current.IsEnabled);
        Assert.Equal(0, app.Close());
    }

    [Fact]
    public void FullTermsAndLicensesCanBeReadBeforeConsent()
    {
        using var app = AppDriver.LaunchUnconsented(null, "--demo", "steady");
        app.WaitDiag(Screen, s => s == "terms", "사용 조건 화면");
        app.Invoke("terms.full");
        Assert.Contains("Microsoft", app.Text("terms.fulltext"), StringComparison.Ordinal);
        app.Invoke("terms.legal");
        var items = app.WaitDiag(d => d.GetProperty("legalItems").ValueKind == JsonValueKind.Number ? d.GetProperty("legalItems").GetInt32() : 0, n => n > 2, "고지 목록");
        Assert.True(items >= 12, $"고지 항목 {items}개");                            // 사용 조건, NetPulse, 구성요소 10개
        app.Invoke("terms.legal.item.3");
        app.WaitDiag(d => d.GetProperty("legalExpanded").GetInt32(), i => i == 3, "항목 펼침");
        var text = (ValuePattern)app.Find("terms.legal.text.3").GetCurrentPattern(ValuePattern.Pattern);
        Assert.True(text.Current.IsReadOnly);
        Assert.True(text.Current.Value.Length > 100);
        Assert.Equal(0, app.Close());
    }

    [Fact]
    public void TermsWorkWithTheKeyboardAlone()
    {
        using var app = AppDriver.LaunchUnconsented(null, "--demo", "steady", "--screen", "home");
        app.WaitDiag(Screen, s => s == "terms", "사용 조건 화면");
        // 처음 초점은 [동의하고 시작]: Enter 한 번으로 시작한다
        AppDriver.WaitFor(() => { try { return AutomationElement.FocusedElement?.Current.AutomationId; } catch (ElementNotAvailableException) { return null; } }, id => id == "terms.accept", "처음 초점");
        app.PressEnter("terms.accept");
        app.FollowNextWindow();
        app.WaitDiag(Screen, s => s == "home", "홈");
        Assert.Equal(0, app.Close());
    }

    [Fact]
    public void InjectionNeedsDemoAndExplicitUserData()
    {
        // 시험 주입 인수는 --demo와 --user-data가 둘 다 있어야 시작한다 (M10 설계 D3): 하나라도 없으면 시작하지 않는다
        foreach (var args in new[] { new[] { "--notices-fail" }, new[] { "--demo", "steady", "--fake-version", "1.0.0" } })
        {
            var info = new System.Diagnostics.ProcessStartInfo(AppDriver.Executable) { UseShellExecute = false };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(info)!;
            Assert.True(p.WaitForExit(30000), "인수를 거절하고 끝나야 한다");
            Assert.NotEqual(0, p.ExitCode);
        }
    }
}
