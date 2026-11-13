namespace NetPulse.Core.Records;

/// <summary>기록을 둘 위치 하나 (기본 또는 대체). 측정 한 회분마다 폴더를 만든다.</summary>
public interface IRecordRoot
{
    /// <summary>"primary" 또는 "fallback". 기록과 이벤트에는 경로 대신 이 종류만 적는다 (경로에 사용자 이름이 들어간다).</summary>
    string Kind { get; }

    /// <summary>같은 볼륨인지 비교할 열쇠. 모르면 null (다른 볼륨으로 본다).</summary>
    string? VolumeKey { get; }

    /// <summary>폴더를 만든다. 이미 있으면 null (이름을 바꿔 다시 부른다).</summary>
    IRecordFolder? CreateFolder(string name);

    /// <summary>읽을 때 조각을 찾을 실제 경로 (디스크가 아니면 null).</summary>
    string? Path { get; }
}

/// <summary>디스크 위치.</summary>
public sealed class DiskRecordRoot(string path, string kind) : IRecordRoot
{
    public string Kind { get; } = kind;
    public string? Path { get; } = path;

    public string? VolumeKey
    {
        get
        {
            try { return System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(Path!))?.ToUpperInvariant(); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException or PathTooLongException) { return null; }
        }
    }

    public IRecordFolder? CreateFolder(string name)
    {
        var full = System.IO.Path.Combine(Path!, name);
        if (Directory.Exists(full)) return null;
        Directory.CreateDirectory(full);
        return new DiskRecordFolder(full, Kind);
    }

    /// <summary>기본 위치: %LOCALAPPDATA%\NetPulse\records, 대체: %TEMP%\NetPulse\records (M3 설계 6.6).</summary>
    public static (DiskRecordRoot Primary, DiskRecordRoot Fallback) Defaults() =>
        (new DiskRecordRoot(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulse", "records"), "primary"),
         new DiskRecordRoot(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetPulse", "records"), "fallback"));
}

/// <summary>저장 위치 정책 (M3 설계 6.6).</summary>
public static class RecordLocation
{
    /// <summary>디스크 가득 참 (ERROR_DISK_FULL 112, ERROR_HANDLE_DISK_FULL 39).</summary>
    public static bool IsDiskFull(Exception ex) => ex is IOException io && (io.HResult & 0xFFFF) is 112 or 39;

    /// <summary>
    /// 측정 중 쓰기에 실패했을 때 대체 위치로 옮길지. 디스크 가득 참이고 같은 볼륨이면 옮겨도 효과가 없으니 옮기지 않는다.
    /// 이미 대체 위치에 있으면 옮기지 않는다.
    /// </summary>
    public static bool ShouldRelocate(Exception failure, IRecordRoot current, IRecordRoot fallback)
    {
        if (ReferenceEquals(current, fallback)) return false;
        bool sameVolume = current.VolumeKey is not null && current.VolumeKey == fallback.VolumeKey;
        return !(IsDiskFull(failure) && sameVolume);
    }

    /// <summary>측정 Id (= 폴더 이름): 시작 UTC 시각 + 임의 6자.</summary>
    public static string NewId(DateTimeOffset startedAt)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        Span<char> tail = stackalloc char[6];
        for (int i = 0; i < tail.Length; i++) tail[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        return startedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "_" + new string(tail);
    }
}
