using System.Diagnostics;

namespace NetPulse.UiTests;

/// <summary>
/// 시험 도구의 종료 판정 (M6 G4 4라운드 이월 3). 앱 없이 도는 회귀 시험:
/// 스스로 -1로 끝난 프로세스를 뒤늦은 강제 종료 요청 때문에 "시험이 끝냄"으로 보지 않는다.
/// </summary>
public sealed class AppDriverExitTests
{
    [Fact]
    public void ExitingOnItsOwnWithMinusOneIsNotTakenAsTheTestsKill()
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit -1") { UseShellExecute = false, CreateNoWindow = true })!;
        Assert.True(p.WaitForExit(10000));
        AppDriver.TerminateForTest(p);   // 이미 끝난 뒤의 종료 요청 (관찰 → 스스로 끝남 → 종료 요청 순서)
        Assert.Equal(-1, p.ExitCode);
        Assert.NotNull(AppDriver.ExitProblem(p.ExitCode));
    }

    [Fact]
    public void OnlyTheTestsOwnTerminationCodeAndZeroAreNormal()
    {
        // 표준 입력을 기다리는 cmd: 스스로는 끝나지 않는다
        using var p = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!;
        Assert.Null(AppDriver.TerminateForTest(p));
        Assert.True(p.WaitForExit(10000));
        Assert.Equal(AppDriver.TestKillCode, p.ExitCode);
        Assert.Null(AppDriver.ExitProblem(p.ExitCode));
        Assert.Null(AppDriver.ExitProblem(0));
        Assert.NotNull(AppDriver.ExitProblem(-1));
        Assert.NotNull(AppDriver.ExitProblem(unchecked((int)0xC000027B)));
    }
}
