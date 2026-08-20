using System.Text;
using System.Text.RegularExpressions;

namespace Marco.Core.Update;

/// <summary>
/// Turns a GitHub release body (markdown, typically the --generate-notes changelog) into plain text fit for
/// a WPF TextBlock: headers become their own line, bullets become •, links collapse to their text, the
/// "by @user in https://…/pull/N" attribution tails and the Full Changelog line are dropped. Pure and
/// culture-free so it is unit-testable.
/// </summary>
public static class ReleaseNotesText
{
    private static readonly Regex Image = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Attribution = new(@"\s+by\s+@[\w-]+(\s+in\s+\S+)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Header = new(@"^#{1,6}\s+", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+", RegexOptions.Compiled);
    private static readonly Regex HtmlComment = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex MidlineSpaceRun = new(@"(?<=\S)  +(?=\S)", RegexOptions.Compiled);

    public static string Clean(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var text = HtmlComment.Replace(markdown.Replace("\r\n", "\n"), "");
        var sb = new StringBuilder();
        bool lastBlank = true; // swallow leading blank lines

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            line = Image.Replace(line, "");
            line = Link.Replace(line, "$1");
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            if (line.TrimStart().StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase))
                continue; // the release link covers it, and a bare compare URL is noise in a TextBlock
            line = Header.Replace(line, "");
            line = Bullet.Replace(line, "$1• ");
            line = Attribution.Replace(line, "");
            line = MidlineSpaceRun.Replace(line, " "); // e.g. the gap a removed image leaves behind

            bool blank = line.Trim().Length == 0;
            if (blank && lastBlank) continue; // collapse runs of blank lines
            sb.Append(blank ? "" : line).Append('\n');
            lastBlank = blank;
        }

        // TrimEnd only: leading blank lines were swallowed above, and a first line that starts indented
        // (a nested bullet) should keep its indentation.
        return sb.ToString().TrimEnd();
    }
}
