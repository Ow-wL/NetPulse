using System.IO;

namespace NetPulse.UiTests;

/// <summary>
/// M10 설계 B2: Release 화면에 따로 올리는 exe(배포 이름)를 다른 빈 폴더·빈 추출 위치에서 띄워 시작 → 홈 → 설정 → 종료.
/// 배포 exe는 게이트의 app/NetPulse.exe와 같은 바이트다 (Test-Release가 확인).
/// </summary>
public sealed class M10ReleaseExeTests
{
    [Fact]
    public void TheReleaseNamedExeRunsAloneFromAnEmptyFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "netpulse-release-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "NetPulse-1.0.0-win-x64.exe");
        File.Copy(AppDriver.Executable, exe);
        var extract = Path.Combine(folder, "extract");
        string? oldExe = Environment.GetEnvironmentVariable("NETPULSE_EXE");
        string? oldExtract = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("NETPULSE_EXE", exe);
            Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", extract);   // 앱이 물려받는다: 빈 추출 위치
            using var app = AppDriver.LaunchUnconsented(null, "--demo", "steady", "--screen", "home");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "terms", "사용 조건 화면");
            app.Invoke("terms.accept");
            app.FollowNextWindow();
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "home", "홈");
            app.Invoke("nav.settings");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "settings", "설정");
            Assert.Equal("1.0.0", app.Text("settings.about.version.value"));
            var startup = app.ReadLog().First(e => e.GetProperty("kind").GetString() == "startup").GetProperty("data").GetProperty("resources");
            Assert.True(startup.GetProperty("fonts").GetBoolean());                         // 글꼴·시나리오가 exe 안에서 풀렸다
            Assert.True(startup.GetProperty("scenarios").GetBoolean());
            Assert.Equal(0, app.Close());
            Assert.Equal(["NetPulse-1.0.0-win-x64.exe", "extract"], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order().ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETPULSE_EXE", oldExe);
            Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", oldExtract);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
