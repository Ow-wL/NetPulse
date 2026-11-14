using System.Globalization;
using System.Text;
using NetPulse.Core.Diagnostics;

namespace NetPulse.Core.Live;

/// <summary>저장 결과: 성공이면 파일 경로, 실패면 정해진 사유 코드.</summary>
public sealed record DiagnosisSave(string? File, string? Error);

/// <summary>
/// 진단 기록 저장과 최근 기록 읽기 (M6 설계 3.5, 구현 중 결정 "저장의 CreateNew·이동").
/// 고유 임시 파일을 새로 만들어 쓰고 닫은 뒤 최종 이름으로 이동한다 (덮어쓰지 않음, 이름이 있으면 -2, -3 …).
/// 남은 임시 파일은 읽을 때 무시한다.
/// </summary>
public sealed class DiagnosisStore(string folder)
{
    public string Folder => folder;

    /// <summary>시험용: 임시 파일에 쓴 뒤 flush 전에 실행 (쓰기 실패를 만든다).</summary>
    internal Action? WriteFailForTest { get; set; }

    /// <param name="stillWanted">이동(확정) 직전마다 확인: 거짓이면 확정하지 않는다 (종료 억제)</param>
    /// <param name="commitLock">주면 확인과 이동을 그 잠금 안에서 한다 (종료 진입과 직렬화, G4 2라운드 2)</param>
    public DiagnosisSave Save(DiagnosisResult result, string appVersion, string? measurementId, Func<bool>? stillWanted = null, object? commitLock = null)
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, ".diagnosis-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = new UTF8Encoding(false).GetBytes(DiagnosisRecord.ToJson(result, appVersion, measurementId));
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                tmp = path;   // 만들었으면 실패해도 지운다
                fs.Write(bytes);
                WriteFailForTest?.Invoke();
                fs.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            if (tmp is not null) TryDelete(tmp);   // 쓰기·flush·닫기 실패에서도 임시 파일을 남기지 않는다 (G4 1라운드 a2)
            return new(null, Code(ex));
        }
        try
        {
            var stem = Path.GetFileNameWithoutExtension(DiagnosisRecord.FileName(result.StartedAt));
            for (int n = 1; n <= 999; n++)
            {
                var target = Path.Combine(folder, n == 1 ? stem + ".json" : $"{stem}-{n}.json");
                if (File.Exists(target)) continue;
                // 확인과 이동을 한 덩어리로: 확인한 뒤 종료가 들어와 확정되는 일이 없게 (재시도마다)
                bool? moved = commitLock is null ? TryCommit(tmp, target, stillWanted) : CommitLocked(commitLock, tmp, target, stillWanted);
                if (moved is null) { TryDelete(tmp); return new(null, "cancelled"); }
                if (moved == true) return new(target, null);
                // false: 그 사이 같은 이름이 생김 → 다음 접미사
            }
            TryDelete(tmp);
            return new(null, "name-exhausted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);   // 정리 실패는 원래 오류를 덮지 않는다
            return new(null, Code(ex));
        }
    }

    /// <summary>
    /// 가장 최근의 읽을 수 있는 기록과 건너뛴(읽지 못한) 기록 수. 최근순: 시작 시각, 같으면 접미사 숫자가 큰 쪽.
    /// </summary>
    public (StoredDiagnosis? Latest, int Skipped) Latest()
    {
        if (!Directory.Exists(folder)) return (null, 0);
        var found = new List<(StoredDiagnosis Stored, int Suffix)>();
        int skipped = 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "diagnosis-*.json").ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, 0); }
        foreach (var f in files)
        {
            StoredDiagnosis d;
            try { d = DiagnosisRecord.Parse(File.ReadAllText(f, Encoding.UTF8)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }
            if (d.Status != DiagnosisReadStatus.Ok) { skipped++; continue; }
            found.Add((d, Suffix(f)));
        }
        var best = found.OrderByDescending(x => x.Stored.StartedAt).ThenByDescending(x => x.Suffix).Select(x => x.Stored).FirstOrDefault();
        return (best, skipped);
    }

    private static bool? CommitLocked(object commitLock, string tmp, string target, Func<bool>? stillWanted)
    {
        lock (commitLock) return TryCommit(tmp, target, stillWanted);
    }

    /// <summary>null = 원하지 않음(종료), true = 이동함, false = 대상 이름이 그 사이 생김.</summary>
    private static bool? TryCommit(string tmp, string target, Func<bool>? stillWanted)
    {
        if (stillWanted?.Invoke() == false) return null;
        try
        {
            File.Move(tmp, target, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(target)) { return false; }
    }

    private static int Suffix(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        int dash = name.LastIndexOf('-');
        return dash > "diagnosis-".Length && int.TryParse(name[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 1;
    }

    private static string Code(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access-denied",
        IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => "disk-full",
        IOException => "io-error",
        _ => "invalid-path",
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
