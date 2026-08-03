namespace ChoPilot.Core;

/// <summary>
/// 화면·레코드 식별 (PHASE1-DESIGN §2.1 ScreenIdentifier, H2).
/// URL 쿼리 → 경로 세그먼트 → 타이틀 순으로 레코드ID 신호를 추출한다.
///  - "지금 어느 화면·어느 레코드"를 판정 (통과선 ≥95%)
///  - 순수 함수(크로스플랫폼) — 단위 테스트 가능
/// </summary>
public static class ScreenIdentifier
{
    /// <summary>레코드ID로 흔히 쓰이는 쿼리 키(우선순위 순). 소문자 비교.</summary>
    private static readonly string[] RecordKeys =
    {
        "id", "no", "docno", "docid", "prno", "pono", "grno",
        "recordid", "reqno", "orderno", "key",
    };

    /// <summary>URL·타이틀에서 레코드 식별 힌트를 도출. 신호가 없으면 null.</summary>
    public static RecordHint? Identify(string? url, string? title = null)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            // 1) URL 쿼리스트링: ?id=PR123 / ?docNo=PO-001 ...
            var fromQuery = FromQuery(url);
            if (fromQuery is not null) return fromQuery;

            // 2) 경로 세그먼트: /po/view/PO123 → 마지막 세그먼트가 레코드처럼 보이면 채택
            var fromPath = FromPath(url);
            if (fromPath is not null) return fromPath;
        }

        // 3) 타이틀: "발주 조회 - PO123" 처럼 코드형 토큰이 있으면 채택
        return FromTitle(title);
    }

    private static RecordHint? FromQuery(string url)
    {
        var query = SplitQuery(url);
        if (query.Count == 0) return null;

        foreach (var key in RecordKeys)
        {
            var match = query.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (match.Key is not null && !string.IsNullOrWhiteSpace(match.Value))
                return new RecordHint("url_query", match.Key, match.Value);
        }
        return null;
    }

    private static RecordHint? FromPath(string url)
    {
        var path = SignatureService.NormalizeRoute(url);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        var last = segments[^1];
        // 마지막 세그먼트가 레코드 코드처럼 보이면(숫자 포함) 채택. 순수 라우트 단어는 제외.
        return LooksLikeRecordCode(last)
            ? new RecordHint("url_path", "segment", last)
            : null;
    }

    private static RecordHint? FromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        foreach (var token in title.Split(new[] { ' ', '-', '–', ':', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (LooksLikeRecordCode(token))
                return new RecordHint("title", "token", token);
        }
        return null;
    }

    /// <summary>코드형 토큰 판별: 숫자를 포함하고 3자 이상. 순수 단어/라우트는 제외.</summary>
    private static bool LooksLikeRecordCode(string token)
    {
        if (token.Length < 3) return false;
        if (!token.Any(char.IsDigit)) return false;
        // 영문/숫자/'-'만 허용 (한글 라벨·문장 배제)
        return token.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
    }

    private static List<KeyValuePair<string, string>> SplitQuery(string url)
    {
        var result = new List<KeyValuePair<string, string>>();

        // 절대/상대 URL 모두에서 '?' 이후만 취한다.
        var qIndex = url.IndexOf('?');
        if (qIndex < 0 || qIndex == url.Length - 1) return result;

        var query = url[(qIndex + 1)..];
        var fragment = query.IndexOf('#');
        if (fragment >= 0) query = query[..fragment];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            result.Add(new KeyValuePair<string, string>(key, value));
        }
        return result;
    }
}
