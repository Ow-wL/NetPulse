using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NetPulse.App;

/// <summary>
/// 앱 수명 기록 (사용자 데이터 폴더의 app-log.jsonl). 자동화가 시작·종료를 확인하는 데 쓴다.
/// 주소·어댑터 이름 같은 식별 정보는 쓰지 않는다.
/// </summary>
public sealed class AppLog
{
    private readonly string path;
    private readonly object gate = new();

    public AppLog(string folder)
    {
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "app-log.jsonl");
    }

    public void Write(string kind, object? data = null)
    {
        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["t"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ["kind"] = kind,
            ["data"] = data,
        });
        try { lock (gate) File.AppendAllText(path, line + "\n", Encoding.UTF8); }
        catch (IOException) { /* 기록 실패가 앱을 멈추지 않게 한다 */ }
    }
}
