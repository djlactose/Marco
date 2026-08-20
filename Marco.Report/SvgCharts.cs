using System.Globalization;
using System.Text;

namespace Marco.Report;

/// <summary>Inline-SVG charts for the report — no JavaScript, no external libraries, so the HTML is one
/// self-contained file that prints and archives cleanly.</summary>
public static class SvgCharts
{
    /// <summary>A horizontal bar chart (used for OS distribution). Values are drawn proportional to the max.</summary>
    public static string HorizontalBars(IReadOnlyList<(string Label, int Value)> items, string accent)
    {
        if (items.Count == 0) return "";
        int max = Math.Max(1, items.Max(i => i.Value));
        const int rowH = 26, labelW = 200, barMax = 320, pad = 4;
        int height = items.Count * rowH + pad * 2;
        int width = labelW + barMax + 60;

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {width} {height}\" width=\"100%\" style=\"max-width:{width}px\" role=\"img\">");
        int y = pad;
        foreach (var (label, value) in items)
        {
            int barW = (int)Math.Round((double)value / max * barMax);
            sb.Append($"<text x=\"0\" y=\"{y + 17}\" font-size=\"13\" fill=\"#333\">{Html.Encode(Truncate(label, 30))}</text>");
            sb.Append($"<rect x=\"{labelW}\" y=\"{y + 5}\" width=\"{Math.Max(1, barW)}\" height=\"15\" rx=\"2\" fill=\"{accent}\" />");
            sb.Append($"<text x=\"{labelW + barW + 6}\" y=\"{y + 17}\" font-size=\"12\" fill=\"#666\">{value}</text>");
            y += rowH;
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>A donut showing a single percentage (the fleet compliance score).</summary>
    public static string ScoreDonut(int percent, string accent)
    {
        percent = Math.Clamp(percent, 0, 100);
        const int size = 120, stroke = 14;
        double r = (size - stroke) / 2.0;
        double circ = 2 * Math.PI * r;
        double filled = circ * percent / 100.0;
        string color = percent >= 80 ? accent : percent >= 50 ? "#B8860B" : "#C0392B";
        var inv = CultureInfo.InvariantCulture;

        return $"<svg viewBox=\"0 0 {size} {size}\" width=\"{size}\" height=\"{size}\" role=\"img\">"
             + $"<circle cx=\"{size / 2}\" cy=\"{size / 2}\" r=\"{r.ToString("0.#", inv)}\" fill=\"none\" stroke=\"#eee\" stroke-width=\"{stroke}\" />"
             + $"<circle cx=\"{size / 2}\" cy=\"{size / 2}\" r=\"{r.ToString("0.#", inv)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{stroke}\" "
             + $"stroke-dasharray=\"{filled.ToString("0.#", inv)} {circ.ToString("0.#", inv)}\" stroke-linecap=\"round\" "
             + $"transform=\"rotate(-90 {size / 2} {size / 2})\" />"
             + $"<text x=\"{size / 2}\" y=\"{size / 2 + 6}\" text-anchor=\"middle\" font-size=\"26\" font-weight=\"700\" fill=\"#333\">{percent}%</text>"
             + "</svg>";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
