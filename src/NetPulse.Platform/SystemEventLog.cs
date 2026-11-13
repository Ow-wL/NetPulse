using System.Diagnostics.Eventing.Reader;
using System.Security;

namespace NetPulse.Platform;

/// <summary>이벤트 로그 조회 결과. 읽지 못한 경우를 "이벤트 없음"과 구별한다 (기획서 6.2 R2).</summary>
public enum LogReadStatus
{
    Read,
    AccessDenied,
    Unavailable,
}

public sealed record LogReadResult(LogReadStatus Status, int Count, string Detail);

/// <summary>
/// Windows 시스템 로그 읽기. M1은 "읽을 수 있는가"만 다룬다. 어떤 이벤트를 근거로 쓸지는 M4에서 정한다.
/// </summary>
public static class SystemEventLog
{
    /// <summary>최근 <paramref name="window"/> 동안의 시스템 로그를 최대 <paramref name="max"/>개 센다.</summary>
    public static LogReadResult CountRecent(TimeSpan window, int max)
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]]]") { ReverseDirection = true };
            int count = 0;
            using var reader = new EventLogReader(query);
            EventRecord? record;
            while (count < max && (record = reader.ReadEvent()) != null) { record.Dispose(); count++; }
            return new LogReadResult(LogReadStatus.Read, count, "");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return new LogReadResult(LogReadStatus.AccessDenied, 0, ex.GetType().Name);
        }
        catch (Exception ex) when (ex is EventLogException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new LogReadResult(LogReadStatus.Unavailable, 0, ex.GetType().Name);
        }
    }
}
