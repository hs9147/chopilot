using System.Text;
using System.Text.Json;

namespace ChoPilot.Core;

// ─────────────────────────────────────────────────────────────────────────────
// 저장소 영속화 (ARCHITECTURE §11).
//
// 지금까지 모든 서버 저장소는 인메모리였다. 재시작하면 사람이 승인한 지식, AI 비용을 치르고
// 얻은 매핑 캐시, 그리고 "전 관측 Audit"(§8)이 함께 사라진다 — 사라지는 감사 로그는
// 감사 로그가 아니다.
//
// 저널은 <b>추가 전용</b>이다. 저장소들이 이미 그 모양이기 때문이다: 전부 부팅 시 전량을
// 메모리에 올리고 메모리에서 응답하며, 변경은 추가(또는 키 단위 덮어쓰기)뿐이다.
// 그래서 파일 전체를 읽는 부팅이 낭비가 아니라 정확히 맞는 모양이다.
//
// JSON Lines를 쓰고 SQLite를 쓰지 않는 이유: 여기 담기는 것은 설계와 함께 계속 바뀌는
// C# record다(이번 세션만 해도 LastInferredAt·OntologyVersion·Trigger·LastSeen이 늘었다).
// 스키마를 손으로 들고 있으면 필드 하나 늘 때마다 마이그레이션이 붙는다. JSON Lines는
// nullable 필드 추가가 공짜이고 예전 줄도 그대로 읽힌다. 질의·인덱스는 어차피 쓰지 않는다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>저장소 1개의 추가 전용 기록.</summary>
public interface IJournal<T>
{
    /// <summary>부팅 시 전량 복원. 손상된 줄은 건너뛰고 <see cref="Corrupt"/>로 센다.</summary>
    IReadOnlyList<T> Load();

    void Append(T record);

    /// <summary>복원 중 건너뛴 줄 수. 침묵하는 절단은 "다 복원했다"로 읽힌다.</summary>
    int Corrupt { get; }

    /// <summary>복원한 레코드 수.</summary>
    int Restored { get; }
}

/// <summary>저널 1개의 부팅 시 상태 — 무엇을 얼마나 복원했고 무엇을 버렸는가.</summary>
public sealed record JournalStatus(string Name, int Restored, int Corrupt);

/// <summary>저장소별 저널을 연다. 이름은 파일명이 되므로 경로 문자를 넣지 않는다.</summary>
public interface IJournalFactory
{
    IJournal<T> Open<T>(string name);
}

/// <summary>
/// 기본 — 아무것도 남기지 않는다. 인메모리 실행과 테스트가 이 경로를 탄다.
/// 영속화는 <b>선택</b>이다: 설정하지 않으면 지금까지와 똑같이 동작한다.
/// </summary>
public sealed class NullJournalFactory : IJournalFactory
{
    public static NullJournalFactory Instance { get; } = new();

    public IJournal<T> Open<T>(string name) => NullJournal<T>.Instance;

    private sealed class NullJournal<T> : IJournal<T>
    {
        public static NullJournal<T> Instance { get; } = new();
        public IReadOnlyList<T> Load() => Array.Empty<T>();
        public void Append(T record) { }
        public int Corrupt => 0;
        public int Restored => 0;
    }
}

/// <summary>디렉터리 하나에 저장소별 <c>{name}.jsonl</c> 파일을 만든다.</summary>
public sealed class FileJournalFactory : IJournalFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IJournal<object>> _opened = new(StringComparer.Ordinal);
    private readonly List<(string Name, Func<JournalStatus> Status)> _status = new();

    public string Directory { get; }

    public FileJournalFactory(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public IJournal<T> Open<T>(string name)
    {
        var journal = new JsonLinesJournal<T>(Path.Combine(Directory, name + ".jsonl"));
        lock (_gate) _status.Add((name, () => new JournalStatus(name, journal.Restored, journal.Corrupt)));
        return journal;
    }

    /// <summary>부팅 복원 결과. 손상 줄이 있었는지 운영자가 볼 수 있어야 한다.</summary>
    public IReadOnlyList<JournalStatus> Status()
    {
        lock (_gate)
            return _status.Select(s => s.Status())
                .OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// 한 줄에 레코드 하나인 추가 전용 파일.
///
/// <para>
/// 직렬화 결과에는 개행이 없다 — 문자열 안의 개행은 JSON이 <c>\n</c> 두 글자로 이스케이프하므로
/// 객체 하나가 항상 한 줄이다. 그래서 줄 단위 복원이 성립한다.
/// </para>
/// <para>
/// 쓰기 도중 프로세스가 죽으면 마지막 줄이 잘릴 수 있다. 그 줄은 건너뛰고 세어 둔다 —
/// 전체를 버리면 멀쩡한 앞부분까지 잃고, 조용히 넘어가면 유실을 아무도 모른다.
/// </para>
/// </summary>
public sealed class JsonLinesJournal<T> : IJournal<T>
{
    // 파일 생성 시 BOM이 붙으면 첫 줄 파싱이 깨진다.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        // 나중에 늘어난 필드를 예전 줄이 모르는 것은 정상이다 — 없으면 기본값으로 복원된다.
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly string _path;

    public JsonLinesJournal(string path) => _path = path;

    public int Corrupt { get; private set; }
    public int Restored { get; private set; }

    public IReadOnlyList<T> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return Array.Empty<T>();

            var records = new List<T>();
            Corrupt = 0;

            foreach (var line in File.ReadLines(_path, Utf8NoBom))
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<T>(line, Json) is { } record) records.Add(record);
                    else Corrupt++;
                }
                catch (JsonException)
                {
                    Corrupt++;
                }
            }

            Restored = records.Count;
            return records;
        }
    }

    /// <summary>
    /// 한 줄을 덧붙인다. 열고 닫기를 매번 하는 것은 의도다 — 관측 속도(사람이 화면을
    /// 넘기는 속도)에서 syscall 비용은 무의미하고, 여는 스트림을 오래 들고 있으면
    /// 저장소마다 생명주기가 붙는다. OS 버퍼까지는 내려가므로 프로세스가 죽어도 남는다.
    /// </summary>
    public void Append(T record)
    {
        var line = JsonSerializer.Serialize(record, Json);
        lock (_gate) File.AppendAllText(_path, line + "\n", Utf8NoBom);
    }
}
