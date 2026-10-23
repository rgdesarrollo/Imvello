using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Imvello.Models;

namespace Imvello.Data;

public sealed partial class MailDatabase
{
    private enum SearchField { Any, From, To, Subject, Body }

    private sealed record SearchTerm(string Text, SearchField Field, string? Digits);

    private const string NumberSeparators = "-() .+/";

    private static readonly Regex WholeNumberQuery = new(@"^[\d\s\-().+/]+$", RegexOptions.Compiled);

    private static SearchField? ParseField(string name) => name.ToLowerInvariant() switch
    {
        "from" or "sender" => SearchField.From,
        "to" or "cc" => SearchField.To,
        "subject" or "subj" => SearchField.Subject,
        "body" => SearchField.Body,
        _ => null
    };

    private static string? DigitsOf(string text)
    {
        if (!text.All(ch => char.IsDigit(ch) || NumberSeparators.Contains(ch))) return null;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return digits.Length >= 3 ? digits : null;
    }

    private static string StripDigitSeparators(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (!NumberSeparators.Contains(ch)) sb.Append(ch);
        return sb.ToString();
    }

    private static List<SearchTerm> ParseSearchQuery(string query)
    {
        var terms = new List<SearchTerm>();
        query = query.Trim();
        if (WholeNumberQuery.IsMatch(query) && DigitsOf(query) is { } wholeDigits)
        {
            terms.Add(new SearchTerm(query, SearchField.Any, wholeDigits));
            return terms;
        }
        var i = 0;
        while (i < query.Length)
        {
            while (i < query.Length && char.IsWhiteSpace(query[i])) i++;
            if (i >= query.Length) break;
            var field = SearchField.Any;
            var j = i;
            while (j < query.Length && char.IsLetter(query[j])) j++;
            if (j > i && j < query.Length && query[j] == ':' && ParseField(query[i..j]) is { } f)
            {
                field = f;
                i = j + 1;
            }
            string text;
            if (i < query.Length && query[i] == '"')
            {
                var end = query.IndexOf('"', i + 1);
                if (end < 0) end = query.Length;
                text = query[(i + 1)..end];
                i = Math.Min(end + 1, query.Length);
            }
            else
            {
                var s = i;
                while (i < query.Length && !char.IsWhiteSpace(query[i])) i++;
                text = query[s..i].Replace("\"", "");
            }
            text = text.Trim();
            if (text.Length == 0) continue;
            terms.Add(new SearchTerm(text, field, DigitsOf(text)));
        }
        return terms;
    }

    public static Regex? BuildHighlightRegex(string? query)
    {
        var patterns = ParseSearchQuery(query ?? "")
            .Where(t => t.Field != SearchField.Body)
            .Select(t => t.Digits != null
                ? string.Join(@"[-() .+/]*", t.Digits.Select(ch => ch.ToString()))
                : Regex.Escape(t.Text))
            .Distinct()
            .OrderByDescending(x => x.Length)
            .ToList();
        return patterns.Count == 0 ? null : new Regex(string.Join("|", patterns), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string SqlStrip(string column)
    {
        var expr = $"COALESCE({column},'')";
        foreach (var ch in NumberSeparators)
            expr = $"REPLACE({expr},'{ch}','')";
        return expr;
    }

    public async Task<List<MessageHeader>> SearchAsync(string query, long? accountId, long? folderId, int limit)
    {
        var terms = ParseSearchQuery(query ?? "");
        if (terms.Count == 0) return new();

        var p = new DynamicParameters();
        var where = new List<string>();
        var rank = new List<string>();
        for (var k = 0; k < terms.Count; k++)
        {
            var t = terms[k];
            p.Add($"l{k}", "%" + EscapeLike(t.Text) + "%");
            string L(string col) => $"{col} LIKE @l{k} ESCAPE '\\'";
            var fromHit = $"({L("m.from_name")} OR {L("m.from_address")})";
            var toHit = $"({L("m.to_list")} OR {L("m.cc_list")})";
            var subjHit = L("m.subject");
            string? bodyHit = null;

            if (t.Digits != null)
            {
                p.Add($"d{k}", "%" + t.Digits + "%");
                string D(string col) => $"{SqlStrip(col)} LIKE @d{k}";
                fromHit = $"({fromHit} OR {D("m.from_name")} OR {D("m.from_address")})";
                toHit = $"({toHit} OR {D("m.to_list")} OR {D("m.cc_list")})";
                subjHit = $"({subjHit} OR {D("m.subject")})";
                bodyHit = $"m.id IN (SELECT rowid FROM messages_fts WHERE {D("body_text")})";
            }

            string? ftsHit = null;
            var phrase = t.Text.Replace("\"", "");
            if (t.Field == SearchField.Body && phrase.Any(char.IsLetterOrDigit))
            {
                p.Add($"f{k}", $"body_text : \"{phrase}\"*");
                ftsHit = $"m.id IN (SELECT rowid FROM messages_fts WHERE messages_fts MATCH @f{k})";
            }

            var parts = t.Field switch
            {
                SearchField.From => new[] { fromHit },
                SearchField.To => new[] { toHit },
                SearchField.Subject => new[] { subjHit },
                SearchField.Body => new[] { ftsHit, bodyHit },
                _ => new[] { fromHit, subjHit }
            };
            var cond = string.Join(" OR ", parts.Where(x => x != null));
            if (cond.Length == 0) return new();
            where.Add($"({cond})");
            rank.Add($"(CASE WHEN {fromHit} THEN 8 WHEN {subjHit} THEN 3 ELSE 0 END)");
        }

        if (accountId.HasValue) { where.Add("m.account_id=@accountId"); p.Add("accountId", accountId); }
        if (folderId.HasValue) { where.Add("m.folder_id=@folderId"); p.Add("folderId", folderId); }
        p.Add("limit", limit);

        var sql = $"SELECT {HeaderCols} FROM messages m WHERE {string.Join(" AND ", where)} " +
                  $"ORDER BY ({string.Join(" + ", rank)}) DESC, m.date DESC LIMIT @limit";
        using var c = Open();
        var rows = (await c.QueryAsync<MessageHeader>(sql, p)).ToList();
        return RankSearchResults(rows, terms);
    }

    private static int MatchQuality(string? hay, string needle)
    {
        if (string.IsNullOrEmpty(hay) || needle.Length == 0) return 0;
        if (string.Equals(hay.Trim(), needle, StringComparison.OrdinalIgnoreCase)) return 4;
        var best = 0;
        var idx = hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var startOk = idx == 0 || !char.IsLetterOrDigit(hay[idx - 1]);
            var end = idx + needle.Length;
            var endOk = end >= hay.Length || !char.IsLetterOrDigit(hay[end]);
            var q = startOk ? (endOk ? 3 : 2) : 1;
            if (q > best) best = q;
            if (best == 3) break;
            idx = hay.IndexOf(needle, idx + 1, StringComparison.OrdinalIgnoreCase);
        }
        return best;
    }

    private static bool DigitsIn(string? hay, string? digits) =>
        digits != null && !string.IsNullOrEmpty(hay) && StripDigitSeparators(hay).Contains(digits, StringComparison.Ordinal);

    private static readonly int[] FromWeights = { 0, 45, 70, 85, 100 };
    private static readonly int[] SubjectWeights = { 0, 20, 30, 40, 45 };
    private static readonly int[] RecipientWeights = { 0, 25, 35, 40, 50 };

    private static int ScoreTerm(MessageHeader h, SearchTerm t)
    {
        var any = t.Field == SearchField.Any;
        var best = 0;
        if (any || t.Field == SearchField.From)
        {
            best = Math.Max(best, FromWeights[MatchQuality(h.FromName, t.Text)]);
            var addrQ = MatchQuality(h.FromAddress, t.Text);
            var local = h.FromAddress.Split('@')[0];
            if (string.Equals(local, t.Text, StringComparison.OrdinalIgnoreCase)) addrQ = 4;
            best = Math.Max(best, FromWeights[addrQ] - (addrQ > 0 ? 5 : 0));
            if (DigitsIn(h.FromName, t.Digits) || DigitsIn(h.FromAddress, t.Digits)) best = Math.Max(best, 80);
        }
        if (any || t.Field == SearchField.Subject)
        {
            best = Math.Max(best, SubjectWeights[MatchQuality(h.Subject, t.Text)]);
            if (DigitsIn(h.Subject, t.Digits)) best = Math.Max(best, 50);
        }
        if (t.Field == SearchField.To)
        {
            best = Math.Max(best, RecipientWeights[Math.Max(MatchQuality(h.To, t.Text), MatchQuality(h.Cc, t.Text))]);
            if (DigitsIn(h.To, t.Digits) || DigitsIn(h.Cc, t.Digits)) best = Math.Max(best, 40);
        }
        return best == 0 ? 5 : best;
    }

    private static List<MessageHeader> RankSearchResults(List<MessageHeader> rows, List<SearchTerm> terms)
    {
        var phrase = terms.Count > 1 && terms.All(t => t.Field == SearchField.Any)
            ? string.Join(" ", terms.Select(t => t.Text))
            : null;
        return rows
            .Select(h =>
            {
                var score = terms.Sum(t => ScoreTerm(h, t));
                if (phrase != null)
                {
                    if (MatchQuality(h.FromName, phrase) >= 2) score += 60;
                    else if (MatchQuality(h.Subject, phrase) >= 2) score += 30;
                }
                return (h, score);
            })
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.h.Date)
            .Select(x => x.h)
            .ToList();
    }
}
