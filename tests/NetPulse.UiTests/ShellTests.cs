using System.Globalization;

namespace NetPulse.UiTests;

/// <summary>M1의 빈 앱 (M1 설계 6절). 한 클래스에 두어 순서대로 돈다 (같은 데스크톱을 쓰므로 병렬로 띄우지 않는다).</summary>
public sealed class ShellTests
{
    private static long Round(AppDriver app) =>
        long.TryParse(app.Text("shell.round"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : -1;

    [Fact]
    public void DemoModeAdvancesRoundsOnADarkWindowAndClosesCleanly()
    {
        using var app = AppDriver.Launch("--demo", "steady");
        Assert.Equal("NetPulse", app.Window.Current.Name);
        Assert.Equal("데모 · steady", app.Text("shell.status"));
        AppDriver.WaitFor(() => Round(app), r => r >= 2, "회차 2 이상");

        double brightness = app.CaptureClient("m1-shell-demo");
        Assert.True(brightness < 0.12, $"흑요석 바탕이어야 한다. 평균 밝기 {brightness:0.000}");

        Assert.Equal(0, app.Close());
        var log = app.ReadLog();
        var startup = Assert.Single(log, e => e.GetProperty("kind").GetString() == "startup").GetProperty("data");
        Assert.Equal("demo:steady", startup.GetProperty("mode").GetString());
        Assert.True(startup.GetProperty("resources").GetProperty("fonts").GetBoolean(), "배포된 앱이 글꼴을 찾아야 한다");
        Assert.True(startup.GetProperty("resources").GetProperty("scenarios").GetBoolean(), "배포된 앱이 데모 시나리오를 찾아야 한다");
        var shutdown = Assert.Single(log, e => e.GetProperty("kind").GetString() == "shutdown");
        Assert.True(shutdown.GetProperty("data").GetProperty("stopCompleted").GetBoolean(), "측정 작업이 제한시간 안에 끝나야 한다");
        Assert.DoesNotContain(log, e => e.GetProperty("kind").GetString() == "unhandled");
    }

    [Fact]
    public void LiveModeRunsDetectedProfileOrSaysWhyNot()
    {
        // M3: 탐지 제안 프로필로 세션을 돌린다. 제안이 없는 환경이면 이유를 보여 주고 대상 없이 회차만 돈다.
        using var app = AppDriver.Launch();
        var status = app.Text("shell.status");
        Assert.True(status.StartsWith("실시간 · 대상 ", StringComparison.Ordinal) || status.StartsWith("측정 대상 없음", StringComparison.Ordinal), "상태: " + status);
        AppDriver.WaitFor(() => Round(app), r => r >= 1, "회차 1 이상");
        Assert.Equal(0, app.Close());
    }

    [Fact]
    public void DemoSpeedRunsFasterThanRealTime()
    {
        using var app = AppDriver.Launch("--demo", "steady", "--demo-speed", "10");
        var started = DateTime.UtcNow;
        AppDriver.WaitFor(() => Round(app), r => r >= 30, "10배속에서 회차 30 이상");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "10배속이면 회차 30에 15초도 걸리지 않아야 한다");
        Assert.Equal(0, app.Close());
    }
}
