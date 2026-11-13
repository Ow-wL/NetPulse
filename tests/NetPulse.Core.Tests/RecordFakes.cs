using System.Text;
using NetPulse.Core.Records;

namespace NetPulse.Core.Tests;

/// <summary>쓰기 고장 종류.</summary>
internal enum Fault { None, PartialWrite, FlushFails, FlushFailsButWritten, RollbackFails, DiskFull, Unexpected }

/// <summary>디스크 파일을 감싸 고장을 주입한다. 다음 쓰기에 쓸 고장을 <see cref="Next"/>가 정한다.</summary>
internal sealed class FaultyFile(IAppendFile inner, Func<Fault> next, Func<TimeSpan>? delay = null) : IAppendFile
{
    private Fault current;

    public long Length => inner.Length;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (delay?.Invoke() is { } d && d > TimeSpan.Zero) Thread.Sleep(d);
        current = next();
        switch (current)
        {
            case Fault.PartialWrite:
                inner.Write(bytes[..(bytes.Length / 2)]);
                inner.FlushToDisk();
                throw new IOException("부분 쓰기");
            case Fault.RollbackFails:
                inner.Write(bytes[..(bytes.Length / 2)]);
                inner.FlushToDisk();
                throw new IOException("부분 쓰기 (되돌리기도 실패)");
            case Fault.Unexpected:
                throw new InvalidOperationException("예상하지 못한 오류");
            case Fault.DiskFull:
                throw new IOException("디스크 가득 참", unchecked((int)0x80070070));
            default:
                inner.Write(bytes);
                break;
        }
    }

    public void FlushToDisk()
    {
        if (current == Fault.FlushFails) throw new IOException("flush 실패");
        inner.FlushToDisk();
        if (current == Fault.FlushFailsButWritten) throw new IOException("flush 실패로 보고 (실제로는 기록됨)");
    }

    public void SetLength(long length)
    {
        if (current == Fault.RollbackFails) throw new IOException("되돌리기 실패");
        if (current == Fault.FlushFailsButWritten) return;   // 보고만 실패: 되돌리기도 실제로는 반영되지 않은 상황 (중복 재현)
        inner.SetLength(length);
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>디스크 폴더를 감싸 파일 이름별로 고장을 주입한다.</summary>
internal sealed class FaultyFolder(IRecordFolder inner, Func<string, Fault> next, Func<string, TimeSpan>? delay = null) : IRecordFolder
{
    public bool FailWholeFiles { get; set; }
    public string Kind => inner.Kind;
    public IAppendFile OpenAppend(string name) => new FaultyFile(inner.OpenAppend(name), () => next(name), delay is null ? null : () => delay(name));
    public void WriteAllText(string name, string text)
    {
        if (FailWholeFiles) throw new IOException("통째 쓰기 실패");
        inner.WriteAllText(name, text);
    }
}

/// <summary>디스크 위치를 감싸 고장을 주입한다.</summary>
internal sealed class FaultyRoot(DiskRecordRoot inner, string? volume = null) : IRecordRoot
{
    public Func<string, Fault> Next { get; set; } = _ => Fault.None;
    public Func<TimeSpan>? Delay { get; set; }
    /// <summary>파일 이름별 지연 (예: 이벤트 쓰기만 막기).</summary>
    public Func<string, TimeSpan>? DelayFor { get; set; }
    public bool FailCreate { get; set; }
    public FaultyFolder? LastFolder { get; private set; }
    public string Kind => inner.Kind;
    public string? VolumeKey { get; } = volume;
    public string? Path => inner.Path;

    public IRecordFolder? CreateFolder(string name)
    {
        if (FailCreate) throw new UnauthorizedAccessException("쓸 수 없음");
        var folder = inner.CreateFolder(name);
        if (folder is null) return null;
        LastFolder = new FaultyFolder(folder, n => Next(n), n => (Delay?.Invoke() ?? TimeSpan.Zero) + (DelayFor?.Invoke(n) ?? TimeSpan.Zero));
        return LastFolder;
    }
}

/// <summary>시험마다 지우는 임시 폴더.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => Path = Directory.CreateTempSubdirectory("np-m3-").FullName;
    public string Path { get; }
    public string Sub(string name) { var p = System.IO.Path.Combine(Path, name); Directory.CreateDirectory(p); return p; }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    public static void Write(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
}
