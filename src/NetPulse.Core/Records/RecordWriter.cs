using System.Text;

namespace NetPulse.Core.Records;

/// <summary>줄을 덧붙이는 파일 하나. 시험에서 부분 쓰기·flush 실패를 주입하려고 추상화한다.</summary>
public interface IAppendFile : IDisposable
{
    long Length { get; }
    void Write(ReadOnlySpan<byte> bytes);
    /// <summary>디스크까지 내려 쓴다 (M3 설계 6.5).</summary>
    void FlushToDisk();
    void SetLength(long length);
}

/// <summary>조각 폴더 하나 (M3 설계 6.2, 6.6). 실제 구현은 파일 시스템, 시험은 메모리·고장 주입.</summary>
public interface IRecordFolder
{
    string Kind { get; }
    IAppendFile OpenAppend(string name);
    /// <summary>임시 파일에 쓴 뒤 바꾼다.</summary>
    void WriteAllText(string name, string text);
}

/// <summary>파일 시스템 조각 폴더.</summary>
public sealed class DiskRecordFolder(string path, string kind) : IRecordFolder
{
    public string Path { get; } = path;
    public string Kind { get; } = kind;

    public IAppendFile OpenAppend(string name)
    {
        Directory.CreateDirectory(Path);
        return new DiskAppendFile(new FileStream(System.IO.Path.Combine(Path, name), FileMode.Append, FileAccess.Write, FileShare.Read));
    }

    public void WriteAllText(string name, string text)
    {
        Directory.CreateDirectory(Path);
        var target = System.IO.Path.Combine(Path, name);
        var temp = target + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, target, overwrite: true);
    }

    private sealed class DiskAppendFile(FileStream stream) : IAppendFile
    {
        public long Length => stream.Length;
        public void Write(ReadOnlySpan<byte> bytes) => stream.Write(bytes);
        public void FlushToDisk() => stream.Flush(flushToDisk: true);
        public void SetLength(long length) { stream.SetLength(length); stream.Seek(0, SeekOrigin.End); }
        public void Dispose() => stream.Dispose();
    }
}

/// <summary>
/// 줄 단위 기록 쓰기 (M3 설계 6.5). 한 줄을 쓰고 디스크 flush까지 성공해야 확정이다.
/// 실패하면 마지막 확정 위치로 잘라 되돌린다(부분 쓰기 뒤에 재시도 줄이 붙지 않게). 되돌리기도 실패하면 그 파일을 버리고
/// 다음 조각 파일(rounds-2.jsonl …)로 이어 쓴다.
/// </summary>
public sealed class LineAppender(IRecordFolder folder, string baseName) : IDisposable
{
    private IAppendFile? file;
    private int part = 1;

    public int Part => part;

    private string FileName => part == 1 ? baseName + ".jsonl" : $"{baseName}-{part}.jsonl";

    /// <summary>한 줄을 확정한다. 실패하면 false (파일은 확정 위치로 돌아가 있거나 다음 조각으로 넘어간다).</summary>
    public bool TryAppend(string line, out Exception? failure)
    {
        failure = null;
        long committed = 0;
        try
        {
            file ??= folder.OpenAppend(FileName);
            committed = file.Length;
            file.Write(Encoding.UTF8.GetBytes(line + "\n"));
            file.FlushToDisk();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex;
            try { file?.SetLength(committed); }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                // 되돌리지 못한 파일에는 더 쓰지 않는다
                try { file?.Dispose(); } catch (IOException) { }
                file = null;
                part++;
            }
            return false;
        }
    }

    public void Dispose()
    {
        try { file?.Dispose(); } catch (IOException) { }
        file = null;
    }

    /// <summary>기록에 남길 실패 사유. 메시지에는 경로(사용자 이름)가 들어갈 수 있어 형식과 코드만 쓴다.</summary>
    public static string Reason(Exception ex) => $"{ex.GetType().Name}:0x{ex.HResult:X8}";
}
