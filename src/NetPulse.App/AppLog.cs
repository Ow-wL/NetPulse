using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPulse.Core.Records;

namespace NetPulse.App;

/// <summary>
/// 앱 수명 기록 (사용자 데이터 폴더의 app-log.jsonl). 자동화가 시작·종료를 확인하는 데 쓴다.
/// 주소·어댑터 이름 같은 식별 정보는 쓰지 않는다. 값 안의 모든 글에서 경로를 줄이거나 지운다 (예외 메시지·스택 줄, M9 설계 C1 [1-13]).
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
            ["data"] = Clean(data is null ? null : JsonSerializer.SerializeToNode(data)),
        });
        try { lock (gate) File.AppendAllText(path, line + "\n", Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 기록 실패가 앱을 멈추지 않게 한다 (권한을 잃은 경우 포함, G4 M10 3라운드 2) */ }
    }

    /// <summary>글 값마다 <see cref="RecordFolderRules.Sanitize"/> (알려진 앞부분은 이름으로, 그 밖 절대 경로·UNC는 &lt;path&gt;).</summary>
    internal static JsonNode? Clean(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToArray()) o[key] = Clean(o[key]?.DeepClone());
                return o;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++) a[i] = Clean(a[i]?.DeepClone());
                return a;
            case JsonValue v when v.TryGetValue<string>(out var s):
                var clean = RecordFolderRules.Sanitize(s);
                return clean == s ? v : JsonValue.Create(clean);
            default:
                return node;
        }
    }
}
