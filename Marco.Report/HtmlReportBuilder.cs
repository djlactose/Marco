using System.Text;
using Marco.Core.Compliance;
using Marco.Core.Lifecycle;
using Marco.Export;

namespace Marco.Report;

/// <summary>
/// Builds a self-contained, client-ready HTML assessment report — a curated narrative (executive summary, an OS
/// chart, prioritized findings, an asset appendix), distinct from the raw CSV/JSON exports. Pure and testable:
/// no file IO except reading the branding logo, which is inlined as a data URI so the output is one file. Every
/// machine-derived string is HTML-encoded (see Html).
/// </summary>
public sealed class HtmlReportBuilder
{
    public string Build(ReportInput input)
    {
        var accent = Sanitize(input.Branding.AccentColor) ?? "#2A6FB0";
        var machines = input.Document.Machines;
        var sb = new StringBuilder();

        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>{Html.Encode(Title(input))}</title>");
        sb.Append(Styles(accent));
        sb.Append("</head><body>");

        AppendHeader(sb, input);
        AppendSummary(sb, input, accent);
        AppendFindings(sb, input);
        if (input.IncludeAppendix) AppendAppendix(sb, machines);
        AppendFooter(sb, input);

        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>The report's title. ClientName carries the full chosen title (the dialog already composes
    /// "Client — Network Assessment"); fall back to a generic title only when none was given.</summary>
    private static string Title(ReportInput input)
        => string.IsNullOrWhiteSpace(input.ClientName) ? "Network Assessment" : input.ClientName!;

    private void AppendHeader(StringBuilder sb, ReportInput input)
    {
        sb.Append("<header class=\"report-head\">");
        if (LogoDataUri(input.Branding.LogoPath) is { } logo)
            sb.Append($"<img class=\"logo\" src=\"{logo}\" alt=\"logo\">");
        sb.Append("<div>");
        sb.Append($"<h1>{Html.Encode(Title(input))}</h1>");
        var by = new List<string>();
        if (input.Branding.CompanyName is { } co) by.Add(Html.Encode(co));
        by.Add($"Generated {input.GeneratedAt:yyyy-MM-dd HH:mm}");
        if (input.Branding.PreparedBy is { } pb) by.Add("by " + Html.Encode(pb));
        sb.Append($"<p class=\"muted\">{string.Join(" · ", by)}</p>");
        sb.Append("</div></header>");
    }

    private void AppendSummary(StringBuilder sb, ReportInput input, string accent)
    {
        var machines = input.Document.Machines;
        int alive = machines.Count(m => IsAlive(m.Status));
        int inventoried = machines.Count(m => m.Collectors.Any(c => c.Status == Marco.Core.Model.CollectorStatus.Ok));

        sb.Append("<section><h2>Executive summary</h2><div class=\"cards\">");
        Card(sb, machines.Count.ToString(), "devices discovered");
        Card(sb, alive.ToString(), "alive");
        Card(sb, inventoried.ToString(), "inventoried");
        if (input.Fleet is { AverageScore: { } score })
            sb.Append($"<div class=\"card score\">{SvgCharts.ScoreDonut(score, accent)}<div class=\"muted\">fleet compliance</div></div>");
        sb.Append("</div>");

        var osCounts = machines
            .Select(m => m.Os.Caption)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .GroupBy(c => c!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(x => x.Item2)
            .Take(10)
            .ToList();
        if (osCounts.Count > 0)
        {
            sb.Append("<h3>Operating systems</h3>");
            sb.Append(SvgCharts.HorizontalBars(osCounts, accent));
        }
        sb.Append("</section>");
    }

    private void AppendFindings(StringBuilder sb, ReportInput input)
    {
        var findings = new List<(string Sev, string Text)>();

        foreach (var m in input.Document.Machines)
        {
            string who = Html.Encode(m.Name is { Length: > 0 } ? $"{m.Name} ({m.Address})" : m.Address);

            if (m.Lifecycle is { OsSupport: OsSupportStatus.EndOfLife } lc)
                findings.Add(("critical", $"{who}: {Html.Encode(lc.SupportSummary ?? "operating system past end of support")}"));
            else if (m.Lifecycle is { OsSupport: OsSupportStatus.EndingSoon } soon)
                findings.Add(("high", $"{who}: {Html.Encode(soon.SupportSummary ?? "operating system nearing end of support")}"));

            if (m.Compliance is { } comp)
                foreach (var r in comp.Results.Where(r => r.Status == RuleStatus.Fail
                    && r.Severity is RuleSeverity.Critical or RuleSeverity.High))
                    findings.Add((r.Severity == RuleSeverity.Critical ? "critical" : "high",
                        $"{who}: {Html.Encode(r.Name)}{(r.Detail is { } d ? " — " + Html.Encode(d) : "")}"));

            foreach (var d in m.Disks ?? Array.Empty<Marco.Core.Model.DiskInfo>())
                if (d.SmartPredictFailure == true || string.Equals(d.HealthStatus, "Unhealthy", StringComparison.OrdinalIgnoreCase))
                    findings.Add(("critical", $"{who}: disk {Html.Encode(d.Model)} predicts failure"));
        }

        sb.Append("<section><h2>Attention needed</h2>");
        if (findings.Count == 0)
        {
            sb.Append("<p class=\"muted\">No critical or high-severity findings.</p></section>");
            return;
        }
        var order = new Dictionary<string, int> { ["critical"] = 0, ["high"] = 1 };
        sb.Append("<ul class=\"findings\">");
        foreach (var (sev, text) in findings.OrderBy(f => order.GetValueOrDefault(f.Sev, 9)))
            sb.Append($"<li class=\"sev-{sev}\"><span class=\"tag\">{sev}</span>{text}</li>");
        sb.Append("</ul></section>");
    }

    private void AppendAppendix(StringBuilder sb, IReadOnlyList<MachineDto> machines)
    {
        sb.Append("<section class=\"appendix\"><h2>Asset appendix</h2>");
        sb.Append("<div class=\"table-scroll\"><table><thead><tr>");
        foreach (var h in new[] { "Address", "Name", "Type", "OS", "Model", "Serial", "Compliance", "OS EOL" })
            sb.Append($"<th>{h}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var m in machines.OrderBy(m => m.Address))
        {
            sb.Append("<tr>");
            Cell(sb, m.Address);
            Cell(sb, m.Name);
            Cell(sb, m.DeviceType.ToString());
            Cell(sb, m.Os.Caption);
            Cell(sb, m.System.Model);
            Cell(sb, m.System.SerialNumber);
            Cell(sb, m.Compliance?.Score is { } s ? $"{s}%" : "");
            Cell(sb, m.Lifecycle?.Display);
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div></section>");
    }

    private static void AppendFooter(StringBuilder sb, ReportInput input)
    {
        sb.Append("<footer class=\"muted\">");
        sb.Append($"Marco {Html.Encode(input.Document.Metadata.Version)} · scan {input.Document.Metadata.Timestamp:yyyy-MM-dd HH:mm} · "
            + $"EOL data {Html.Encode(input.EolTableUpdated)}");
        sb.Append("</footer>");
    }

    private static void Card(StringBuilder sb, string value, string label)
        => sb.Append($"<div class=\"card\"><div class=\"num\">{Html.Encode(value)}</div><div class=\"muted\">{Html.Encode(label)}</div></div>");

    private static void Cell(StringBuilder sb, string? text) => sb.Append($"<td>{Html.Encode(text)}</td>");

    private static bool IsAlive(Marco.Core.Model.MachineStatus s)
        => s is not (Marco.Core.Model.MachineStatus.Pending or Marco.Core.Model.MachineStatus.Unreachable
            or Marco.Core.Model.MachineStatus.Cancelled or Marco.Core.Model.MachineStatus.Scanning);

    /// <summary>Only accept a simple hex color as the accent — it is injected into CSS, so nothing else is allowed.</summary>
    private static string? Sanitize(string? color)
    {
        if (color is null) return null;
        var c = color.Trim();
        if (c.Length is 4 or 7 && c[0] == '#' && c[1..].All(Uri.IsHexDigit)) return c;
        return null;
    }

    private static string? LogoDataUri(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext is ".jpg" or ".jpeg" ? "image/jpeg" : ext == ".gif" ? "image/gif" : "image/png";
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return null; }
    }

    private static string Styles(string accent) => "<style>"
        + "*{box-sizing:border-box}"
        + "body{font-family:'Segoe UI',system-ui,sans-serif;color:#222;margin:0;padding:32px;max-width:960px;margin:auto;line-height:1.5}"
        + ".report-head{display:flex;align-items:center;gap:18px;border-bottom:3px solid " + accent + ";padding-bottom:16px;margin-bottom:24px}"
        + ".logo{max-height:64px;max-width:200px}"
        + "h1{margin:0;font-size:26px}h2{color:" + accent + ";border-bottom:1px solid #eee;padding-bottom:6px;margin-top:32px}"
        + "h3{margin-top:20px;font-size:15px}"
        + ".muted{color:#6B7280;font-size:13px}"
        + ".cards{display:flex;flex-wrap:wrap;gap:16px;margin:16px 0}"
        + ".card{background:#F5F6F8;border:1px solid #e3e6ea;border-radius:8px;padding:16px 22px;min-width:120px;text-align:center}"
        + ".card .num{font-size:30px;font-weight:700;color:" + accent + "}"
        + ".card.score{display:flex;flex-direction:column;align-items:center;gap:4px}"
        + "ul.findings{list-style:none;padding:0}"
        + "ul.findings li{padding:8px 12px;border-radius:6px;margin-bottom:6px;background:#fafafa;border-left:4px solid #ccc}"
        + "li.sev-critical{border-left-color:#C0392B;background:#FDECEA}"
        + "li.sev-high{border-left-color:#E67E22;background:#FEF5E7}"
        + ".tag{display:inline-block;font-size:11px;font-weight:700;text-transform:uppercase;color:#fff;background:#999;border-radius:3px;padding:1px 6px;margin-right:8px}"
        + "li.sev-critical .tag{background:#C0392B}li.sev-high .tag{background:#E67E22}"
        + ".table-scroll{overflow-x:auto}"
        + "table{border-collapse:collapse;width:100%;font-size:13px;margin-top:8px}"
        + "th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #eee;white-space:nowrap}"
        + "th{background:#F5F6F8}"
        + "footer{margin-top:40px;border-top:1px solid #eee;padding-top:12px}"
        + "@media print{body{padding:0}h2{break-after:avoid}ul.findings li,tr{break-inside:avoid}.appendix{break-before:page}}"
        + "</style>";
}
