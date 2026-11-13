using System.Text.Json;

namespace NetPulse.UiTests;

/// <summary>앱 틀 (M1에서 시작, M5에서 메인 창으로 바뀜): 데모 시작·종료 정리·배속, 실측 시작.</summary>
public sealed class ShellTests
{
    [Fact]
    public void DemoOpensFirstRunWithoutMeasuringAndClosesCleanly()
    {
        using var app = AppDriver.Launch("--demo", "steady");
        Assert.Equal("NetPulse", app.Window.Current.Name);
        var d = app.WaitDiag(x => x, x => M5Fixtures.Str(x, "screen") == "firstrun", "첫 실행 안내");
        Assert.Equal("NoProfile", M5Fixtures.Str(d, "host"));     // 측정을 시작하지 않고 뜬다 (M5 설계 4.1)
        Assert.Equal(0, d.GetProperty("rounds").GetInt32());
        double brightness = app.CaptureClient("shell-demo-dark");
        Assert.True(brightness < 0.12, $"다크 창이 너무 밝음: {brightness:0.000}");
        Assert.Equal(0, app.Close());
        var log = app.ReadLog();
        var startup = log.Single(e => e.GetProperty("kind").GetString() == "startup").GetProperty("data");
        Assert.Equal("demo:steady", startup.GetProperty("mode").GetString());
        Assert.True(startup.GetProperty("resources").GetProperty("fonts").GetBoolean());
        Assert.True(startup.GetProperty("resources").GetProperty("scenarios").GetBoolean());
        Assert.Contains(log, e => e.GetProperty("kind").GetString() == "shutdown");
        Assert.DoesNotContain(log, e => e.GetProperty("kind").GetString() is "unhandled" or "renderFailed" or "diagFailed");
    }

    [Fact]
    public void LiveModeOpensFirstRunAndDetects()
    {
        // 실측: 이 PC의 실제 탐지 (결과는 네트워크에 달렸으므로 통과 조건이 아니다. 탐지가 끝나 이유를 보이는 것까지)
        using var app = AppDriver.Launch();
        app.WaitDiag(x => M5Fixtures.Str(x, "screen"), s => s == "firstrun", "첫 실행 안내");
        app.WaitDiag(x => x.GetProperty("screenDiag").ValueKind == JsonValueKind.Object ? M5Fixtures.Str(x, "screenDiag", "status") : "", s => s.Length > 0, "탐지 끝");
        Assert.Equal("live", M5Fixtures.Str(app.Diag(), "world"));
        Assert.Equal(0, app.Close());
    }

    [Fact]
    public void DemoSpeedRunsFasterThanRealTime()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "10", "--start");
            var started = DateTime.UtcNow;
            app.WaitDiag(x => x.GetProperty("rounds").GetInt32(), r => r >= 30, "30회차");
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "10배속이면 30회차가 15초 안에 와야 한다");
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }
}
