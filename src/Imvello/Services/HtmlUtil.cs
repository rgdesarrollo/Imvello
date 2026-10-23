using System.Net;
using System.Text.RegularExpressions;
using MimeKit;

namespace Imvello.Services;

public static partial class HtmlUtil
{
    [GeneratedRegex(@"<(script|style|head)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"<br\s*/?>|</p>|</div>|</tr>|</li>|</h\d>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex BlankLinesRegex();

    public static string HtmlToText(string html)
    {
        var s = BlockRegex().Replace(html, "");
        s = BreakRegex().Replace(s, "\n");
        s = TagRegex().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = SpaceRegex().Replace(s, " ");
        s = BlankLinesRegex().Replace(s, "\n\n");
        return s.Trim();
    }

    public static string TextToHtml(string text) =>
        "<div style=\"white-space:pre-wrap;font-family:Consolas,'Segoe UI',monospace\">" + WebUtility.HtmlEncode(text) + "</div>";

    public static string ExtractText(MimeMessage msg)
    {
        var text = msg.TextBody ?? (msg.HtmlBody != null ? HtmlToText(msg.HtmlBody) : "");
        return text.Length > 65536 ? text[..65536] : text;
    }

    public static string Escape(string? s) => WebUtility.HtmlEncode(s ?? "");
}
