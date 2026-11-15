using NetPulse.Core.Demo;
using NetPulse.Core.Diagnostics;

namespace NetPulse.Core.Live;

public enum SettingsPage { Network, Wifi, Proxy }

/// <summary>시스템 동작의 결과. <paramref name="Code"/>는 정해진 사유 코드만 (경로·예외 글 없음).</summary>
public sealed record SystemActionResult(bool Ok, string? Code = null);

/// <summary>
/// 보조 도구의 시스템 동작 (M6 설계 3.3). 실제 구현은 고정 명령만 실행하고 사용자 입력을 명령줄에 넣지 않는다.
/// DNS 캐시 비우기는 화면이 확인 대화상자를 거친 뒤에만 부른다 (기획서 7.5).
/// </summary>
public interface ISystemActions
{
    /// <summary>DNS 캐시 비우기. 제한 10초.</summary>
    Task<SystemActionResult> FlushDnsAsync(CancellationToken ct);
    SystemActionResult OpenSettings(SettingsPage page);
    /// <summary>허용 뿌리 안의 존재하는 폴더만 연다 (<see cref="FolderPolicy"/>).</summary>
    SystemActionResult OpenFolder(string folder, IReadOnlyList<string> allowedRoots);
}

/// <summary>세계가 주는 진단 묶음: 검사기, 설정(데모는 문서용 값), 시스템 동작.</summary>
public sealed record DiagnosisWorld(IDiagnosticProbes Probes, DiagnosisSettings Settings, ISystemActions Actions);

/// <summary>폴더 열기 허용 검사 (M6 설계 3.3): 정규화한 경로가 허용 뿌리 자체이거나 그 아래(경로 구성요소 경계)이고 존재해야 한다.</summary>
public static class FolderPolicy
{
    public static string? Check(string folder, IReadOnlyList<string> allowedRoots)
    {
        string full;
        try { full = Normalize(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "not-allowed"; }
        bool inside = false;
        foreach (var root in allowedRoots)
        {
            string r;
            try { r = Normalize(root); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (string.Equals(full, r, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { inside = true; break; }
        }
        if (!inside) return "not-allowed";
        return Directory.Exists(full) ? null : "missing";
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

/// <summary>데모 시스템 동작: 실행하지 않고 호출을 기록한다. DNS 비우기 결과는 시나리오의 <c>diagnosis.flushDns</c>.</summary>
public sealed class DemoSystemActions(Scenario scenario) : ISystemActions
{
    private readonly List<string> calls = [];

    /// <summary>호출 기록 ("flushDns", "settings:Network", "folder:경로").</summary>
    public IReadOnlyList<string> Calls { get { lock (calls) return calls.ToArray(); } }

    private void Add(string call) { lock (calls) calls.Add(call); }

    public async Task<SystemActionResult> FlushDnsAsync(CancellationToken ct)
    {
        Add("flushDns");
        if (scenario.Diagnosis?.DelayMs?.GetValueOrDefault("flushDns") is int ms && ms > 0) await Task.Delay(ms, ct).ConfigureAwait(false);
        switch (scenario.Diagnosis?.FlushDns ?? "ok")
        {
            case "ok": return new(true);
            case "fail": return new(false, "exit-1");
            default:
                // 실제 구현의 10초 제한과 같은 결과 (기다리지는 않는다: 캡처·시험이 10초 걸리지 않게)
                return new(false, "timeout");
        }
    }

    public SystemActionResult OpenSettings(SettingsPage page)
    {
        Add("settings:" + page);
        return new(true);
    }

    public SystemActionResult OpenFolder(string folder, IReadOnlyList<string> allowedRoots)
    {
        if (FolderPolicy.Check(folder, allowedRoots) is { } code) return new(false, code);
        Add("folder:" + folder);
        return new(true);
    }
}
