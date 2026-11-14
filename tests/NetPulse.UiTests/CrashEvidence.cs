using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NetPulse.UiTests;

/// <summary>앱이 시험 중 비정상 종료함 (증거 폴더 경로를 메시지에 담는다).</summary>
public sealed class AppCrashedException(string message) : Exception(message);

/// <summary>
/// 비정상 종료 증거 수집 (M6 설계 6.1, M5 이월 1). 모두 읽기만 한다. 시스템 설정(덤프 수집 레지스트리)은 바꾸지 않는다.
/// <list type="bullet">
/// <item>앱 기록 (<c>app-log.jsonl</c>, 처리되지 않은 예외의 HRESULT·스택이 있으면 여기에)</item>
/// <item>Windows 응용 프로그램 로그에서 이 실행 이후의 Application Error·.NET Runtime·Windows Error Reporting 항목 중 NetPulse.exe 것 (<c>wevtutil</c>로 읽기)</item>
/// <item>Windows 오류 보고가 만든 보고서 폴더(AppCrash_NetPulse.exe_*)의 파일 복사 (덤프는 64 MB 이하만)</item>
/// </list>
/// 출력은 시험 출력 폴더의 <c>crash-시각-경로-pid/</c>. 이 PC의 경로가 섞일 수 있어 게이트 산출물(<c>artifacts/</c>, 공개 안 함)에만 둔다.
/// 무엇을 확보했고 무엇을 못 했는지(<c>summary.json</c>)를 함께 적는다: 원인이 확정되지 않으면 "재현 없음"을 해결로 기록하지 않는다.
/// </summary>
public static class CrashEvidence
{
    private const long MaxCopyBytes = 64L * 1024 * 1024;
    private static readonly string[] Providers = ["Application Error", ".NET Runtime", "Windows Error Reporting"];

    /// <summary>증거를 모아 폴더 경로를 돌려준다. 수집 자체의 실패는 삼키고 요약에 적는다 (원래 실패를 덮지 않게).</summary>
    public static string Collect(Process process, string userData, string where, DateTime startedUtc)
    {
        int pid = SafePid(process);
        int? exitCode = null;
        try { if (process.HasExited) exitCode = process.ExitCode; } catch (InvalidOperationException) { }
        string dir = Path.Combine(AppDriver.OutputDirectory, $"crash-{DateTime.Now:HHmmss}-{where}-{pid}");
        var summary = new Dictionary<string, object?> { ["where"] = where, ["pid"] = pid, ["exitCode"] = exitCode is { } c ? $"0x{c:X8}" : null };
        var problems = new List<string>();
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "(증거 폴더를 만들지 못함: " + ex.Message + ")"; }

        // 1. 앱 기록
        try
        {
            var log = Path.Combine(userData, "app-log.jsonl");
            summary["appLog"] = File.Exists(log);
            if (File.Exists(log)) File.Copy(log, Path.Combine(dir, "app-log.jsonl"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add("앱 기록: " + ex.Message); summary["appLog"] = false; }

        // 2. 응용 프로그램 로그 (항목이 종료 직후 늦게 쓰일 수 있어 잠깐 기다리며 다시 읽는다)
        try
        {
            string events = "";
            for (int attempt = 0; attempt < 5; attempt++)
            {
                events = ReadEvents(startedUtc);
                if (events.Length > 0) break;
                Thread.Sleep(1000);
            }
            summary["events"] = events.Length > 0;
            if (events.Length > 0) File.WriteAllText(Path.Combine(dir, "application-events.txt"), events, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            problems.Add("응용 프로그램 로그: " + ex.Message); summary["events"] = false;
        }

        // 3. 오류 보고 폴더
        var reports = new List<string>();
        bool dump = false;
        foreach (var root in WerRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var report in Directory.EnumerateDirectories(root, "AppCrash_NetPulse.exe_*"))
                {
                    if (Directory.GetLastWriteTimeUtc(report) < startedUtc) continue;
                    string target = Path.Combine(dir, "wer-" + Path.GetFileName(report));
                    Directory.CreateDirectory(target);
                    foreach (var file in Directory.EnumerateFiles(report))
                    {
                        var info = new FileInfo(file);
                        bool isDump = info.Extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase) || info.Extension.Equals(".mdmp", StringComparison.OrdinalIgnoreCase);
                        if (info.Length > MaxCopyBytes) { problems.Add($"오류 보고 파일이 커서 복사 안 함: {info.Name} ({info.Length / 1024 / 1024} MB)"); continue; }
                        File.Copy(file, Path.Combine(target, info.Name), overwrite: true);
                        dump |= isDump;
                    }
                    reports.Add(Path.GetFileName(report) + WerSignature(Path.Combine(report, "Report.wer")));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add("오류 보고 폴더: " + ex.Message); }
        }
        summary["werReports"] = reports;
        summary["dump"] = dump;
        if (!dump) problems.Add("덤프 없음 (덤프 수집 설정은 시스템 설정이라 바꾸지 않음)");
        summary["notCollected"] = problems;
        try { File.WriteAllText(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return dir;
    }

    /// <summary>
    /// 보고서의 오류 모듈·예외 코드·오프셋 (Sig[3]·[6]·[7]). 0xC000027B(저장된 예외)는 여기서 그 안의 원래 HRESULT가 보인다
    /// (예: combase.dll 80131509 = 관리 코드의 InvalidOperationException이 WinRT 경계를 넘음).
    /// </summary>
    private static string WerSignature(string reportWer)
    {
        try
        {
            if (!File.Exists(reportWer)) return "";
            var sig = new Dictionary<string, string>();
            foreach (var line in File.ReadLines(reportWer, Encoding.Unicode))
            {
                if (!line.StartsWith("Sig[", StringComparison.Ordinal)) continue;
                int close = line.IndexOf("].Value=", StringComparison.Ordinal);
                if (close > 4) sig[line[4..close]] = line[(close + 8)..];
            }
            return $" (모듈 {sig.GetValueOrDefault("3")}, 코드 {sig.GetValueOrDefault("6")}, 오프셋 {sig.GetValueOrDefault("7")})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static int SafePid(Process p)
    {
        try { return p.Id; } catch (InvalidOperationException) { return 0; }
    }

    private static IEnumerable<string> WerRoots()
    {
        foreach (var baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) })
        {
            if (baseDir.Length == 0) continue;
            yield return Path.Combine(baseDir, "Microsoft", "Windows", "WER", "ReportArchive");
            yield return Path.Combine(baseDir, "Microsoft", "Windows", "WER", "ReportQueue");
        }
    }

    /// <summary>
    /// <c>wevtutil qe Application</c>로 이 실행 이후의 해당 공급자 항목을 읽고 NetPulse.exe 항목만 남긴다.
    /// 글은 콘솔 코드 페이지일 수 있어 한글이 깨질 수 있지만 예외 코드·모듈·오프셋은 ASCII라 남는다.
    /// </summary>
    private static string ReadEvents(DateTime sinceUtc)
    {
        string since = sinceUtc.AddSeconds(-5).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        string providers = string.Join(" or ", Providers.Select(p => $"@Name='{p}'"));
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wevtutil.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "qe", "Application", $"/q:*[System[Provider[{providers}] and TimeCreated[@SystemTime>='{since}']]]", "/f:text", "/rd:true", "/c:50" })
            info.ArgumentList.Add(a);
        using var p = Process.Start(info) ?? throw new InvalidOperationException("wevtutil을 시작하지 못했습니다.");
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(15000)) { p.Kill(); throw new InvalidOperationException("wevtutil 시간 초과"); }
        string text = output.GetAwaiter().GetResult();
        // 항목은 "Event[n]:"로 시작한다
        var entries = text.Split("Event[", StringSplitOptions.RemoveEmptyEntries)
            // .NET Runtime 항목에는 프로세스 Id가 없어, 이 실행이 시작된 뒤의 NetPulse.exe 항목을 모두 둔다 (시험은 한 번에 앱 하나)
            .Where(e => e.Contains("NetPulse.exe", StringComparison.OrdinalIgnoreCase))
            .Select(e => "Event[" + e.TrimEnd());
        return string.Join(Environment.NewLine + Environment.NewLine, entries);
    }
}
