using System.Globalization;
using System.Text;

namespace ChoPilot.Core;

/// <summary>
/// 정규화된 엔티티 식별자. <see cref="Key"/>는 정규화 결과이고 원문이 아니다 —
/// "A사 "와 "a사"는 같은 키로 접힌다.
/// </summary>
public sealed record EntityKey(string Type, string Key);

/// <summary>관측된 엔티티 1건 — 정규화 전 원문을 함께 들고 있다(갈림 진단용).</summary>
public sealed record EntityMention(EntityKey Entity, string Raw, string Concept);

/// <summary>
/// Entity Resolver 1단 — <b>Deterministic</b> (ARCHITECTURE §6).
///
/// <para>
/// 3단 캐스케이드 중 공유 키 매칭만 구현한다. 2단(Temporal: 직전 N분 내 읽은 메일/문서)과
/// 3단(Semantic: 임베딩 + LLM 판정)은 <b>메일·문서 관측이 전제</b>라 현재 범위 밖이다.
/// 1단이 대부분을 처리하고 LLM을 호출하지 않는다는 설계 의도는 여기서 그대로 지켜진다.
/// </para>
/// <para>
/// 엔티티가 되는 개념은 <see cref="Concept.EntityRef"/>가 지정된 것뿐이다 — 지식 문서로
/// 정해지므로 어떤 개념을 엔티티로 볼지는 재배포 없이 바뀐다. <b>민감 개념은 제외</b>한다:
/// 값이 마스킹되어 오지 않으므로 구조적으로도 불가능하지만, 명시적으로 막아 둔다.
/// </para>
/// </summary>
public static class EntityResolver
{
    /// <summary>Business Object에서 엔티티 언급을 추출. 값이 없거나 엔티티 개념이 아니면 건너뛴다.</summary>
    public static IReadOnlyList<EntityMention> Extract(BusinessObject bo, CompiledKnowledge knowledge)
    {
        var mentions = new List<EntityMention>();

        foreach (var (conceptName, value) in bo.Fields)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            var concept = knowledge.ByName(conceptName);
            if (concept?.EntityRef is not { Length: > 0 } type) continue;
            if (concept.Sensitive) continue;      // 민감 개념은 엔티티가 되지 않는다

            var key = Normalize(value);
            if (key.Length == 0) continue;

            mentions.Add(new EntityMention(new EntityKey(type, key), value, conceptName));
        }

        return mentions;
    }

    /// <summary>
    /// 공유 키 매칭을 위한 정규화. 화면마다 다른 공백·대소문자·전각 표기가 같은 실체를
    /// 다른 엔티티로 가르는 것을 막는다 — 서명에서 행 인덱스를 접은 것과 같은 이유다.
    ///
    /// <para>
    /// 대문자화는 <b>불변 문화권</b>으로 한다. 터키어 로케일에서 "i".ToUpper()는 "İ"가 되어
    /// 서버 로케일에 따라 같은 값이 다른 키로 갈린다.
    /// </para>
    /// </summary>
    public static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = true;   // 선행 공백 제거

        foreach (var raw in value.Normalize(NormalizationForm.FormKC))   // 전각 → 반각
        {
            var c = char.IsWhiteSpace(raw) ? ' ' : raw;
            if (c == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
                sb.Append(' ');
                continue;
            }
            lastWasSpace = false;
            sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString().TrimEnd();
    }
}
