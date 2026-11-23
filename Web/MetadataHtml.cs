using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Airi.Web;

internal static partial class MetadataHtml
{
    private static readonly string[] ReleaseDateFormats = ["yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyy年M月d日"];

    public static string HasClass(string cssClass) =>
        $"contains(concat(' ', normalize-space(@class), ' '), ' {cssClass} ')";

    public static IEnumerable<HtmlNode> Nodes(HtmlNode? root, string path) =>
        root?.SelectNodes(path) as IEnumerable<HtmlNode> ?? [];

    public static string Text(HtmlNode? node) => Text(node?.InnerText);

    public static string Text(string? text) =>
        Whitespace().Replace(HtmlEntity.DeEntitize(text ?? string.Empty), " ").Trim();

    public static string Label(HtmlNode? node) => Text(node).TrimEnd(':', '：').Trim();

    public static DateOnly? Date(string value) =>
        DateOnly.TryParseExact(value, ReleaseDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    public static string? SingleValue(IEnumerable<string> values)
    {
        var distinct = CleanList(values);
        return distinct.Length == 1 ? distinct[0] : null;
    }

    public static string[] CleanList(IEnumerable<string> values) =>
        values.Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool IsLabel(string value, params string[] labels) =>
        labels.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string[] Keywords(HtmlNode node)
    {
        var values = CleanList(Nodes(node, ".//a").Select(link => Text(link)));
        return values.Length > 0 ? values : Keywords(Text(node));
    }

    public static string[] Keywords(string value) =>
        CleanList(value.Split([',', '，', '、', ';'], StringSplitOptions.TrimEntries));

    public static string Description(HtmlNode? node)
    {
        if (node is null)
            return string.Empty;

        var lines = ParagraphBreak().Replace(node.InnerHtml, "\n").Split('\n');
        return string.Join("\n", lines.Select(line => Text(Markup().Replace(line, string.Empty))).Where(line => line.Length > 0));
    }

    public static string Title(string shownTitle, string shownCode)
    {
        if (string.Equals(shownTitle, shownCode, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return shownTitle.StartsWith(shownCode + " ", StringComparison.OrdinalIgnoreCase)
            ? shownTitle[(shownCode.Length + 1)..]
            : shownTitle;
    }

    public static bool IsNoResults(HtmlNode root)
    {
        var text = Text(root.SelectSingleNode("//body") ?? root);
        return new[] { "検索結果がありません", "該当する作品がありません", "找不到符合", "沒有找到", "没有找到", "No results found" }
            .Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"</p\s*>|<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Markup();
}
