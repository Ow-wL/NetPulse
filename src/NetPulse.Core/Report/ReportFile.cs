using System.Text;

namespace NetPulse.Core.Report;

/// <summary>시험 주입 (M8 설계 3.7·4.2): 쓰는 중 실패, 옮기기 직전 실패, 안전망 걸림.</summary>
public enum ReportFault { None, Write, Move, Leak }

public enum ReportWriteError { None, Exists, Failed }

/// <summary>내보내기 결과. 앱 기록에는 성공 여부·크기·가리기 여부만 남긴다 (경로 없음).</summary>
public sealed record ReportWriteResult(ReportWriteError Error, string? Path, long Bytes, string? Problem)
{
    public bool Ok => Error == ReportWriteError.None;
}

/// <summary>
/// 리포트 파일 쓰기 (M8 설계 3.7 [1-12]): 같은 폴더의 고유 임시 파일에 쓰고 flush → 대상이 없으면 옮기기(덮어쓰지 않음),
/// 덮어쓰기를 확인한 경우만 바꾸기. 실패·취소 때 임시 파일을 지우고 기존 파일은 그대로 둔다. 정리 실패는 원래 오류를 덮지 않는다.
/// </summary>
public static class ReportFile
{
    public const string TempPrefix = ".netpulse-report-";
    public const string TempSuffix = ".tmp";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static ReportWriteResult Write(string path, string content, bool overwrite, ReportFault fault = ReportFault.None)
    {
        string full = System.IO.Path.GetFullPath(path);
        string folder = System.IO.Path.GetDirectoryName(full) ?? throw new ArgumentException("폴더가 없는 경로입니다.", nameof(path));
        if (File.Exists(full) && !overwrite) return new ReportWriteResult(ReportWriteError.Exists, null, 0, "같은 이름의 파일이 있습니다.");
        string temp = System.IO.Path.Combine(folder, TempPrefix + Guid.NewGuid().ToString("N") + TempSuffix);
        try
        {
            var bytes = Utf8.GetBytes(content);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (fault == ReportFault.Write)
                {
                    stream.Write(bytes, 0, bytes.Length / 2);
                    throw new IOException("시험 주입: 쓰는 중 실패");
                }
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (fault == ReportFault.Move) throw new IOException("시험 주입: 옮기기 직전 실패");
            if (File.Exists(full))
            {
                if (!overwrite) throw new IOException("같은 이름의 파일이 생겼습니다.");
                File.Replace(temp, full, destinationBackupFileName: null);   // 실패하면 원래 파일 유지
            }
            else File.Move(temp, full, overwrite: false);
            return new ReportWriteResult(ReportWriteError.None, full, bytes.LongLength, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            Cleanup(temp);
            return new ReportWriteResult(ReportWriteError.Failed, null, 0, ex is UnauthorizedAccessException ? "그 폴더에 쓸 권한이 없습니다." : "파일을 쓰지 못했습니다.");
        }
    }

    private static void Cleanup(string temp)
    {
        try { if (File.Exists(temp)) File.Delete(temp); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // 정리 실패는 원래 오류를 덮지 않는다
    }
}

/// <summary>
/// 내보낼 HTML 만들기와 내보내기 전 확인 (M8 설계 3.3 안전망, 3.4 외부 참조). 걸리면 HTML을 돌려주지 않는다.
/// </summary>
public static class ReportExport
{
    public sealed record Rendered(string? Html, IReadOnlyList<string> LeakKinds, IReadOnlyList<string> Problems)
    {
        public bool Ok => Html is not null;
    }

    public static Rendered Render(ReportDocument d, ReportFault fault = ReportFault.None)
    {
        var doc = fault == ReportFault.Leak ? WithLeak(d) : d;
        var leaks = new SortedSet<string>(StringComparer.Ordinal);
        if (doc.Guard is { } guard) foreach (var k in guard.Check(doc)) leaks.Add(k);
        else if (doc.Options.Masked) leaks.Add("안전망 없음");
        var html = ReportHtml.Write(doc);
        if (doc.Options.Masked && doc.Guard is { } g) foreach (var k in g.CheckAttributes(html)) leaks.Add(k);
        var problems = ReportHtml.Verify(html);
        return leaks.Count > 0 || problems.Count > 0 ? new Rendered(null, leaks.ToArray(), problems) : new Rendered(html, [], []);
    }

    /// <summary>시험 주입: 기록의 첫 대상 주소를 기록 출처 조각으로 첫 절에 끼운다 (안전망이 거는지 보는 장면).</summary>
    private static ReportDocument WithLeak(ReportDocument d)
    {
        var value = d.Guard?.Identities.FirstOrDefault(i => i.Kind == "대상 주소")?.Value ?? "192.0.2.1";
        if (d.Sections.Count == 0) return d;
        var first = d.Sections[0];
        var blocks = first.Blocks.Append(new ReportParagraph([ReportRun.Rec(value)])).ToArray();
        return d with { Sections = [first with { Blocks = blocks }, .. d.Sections.Skip(1)] };
    }
}
