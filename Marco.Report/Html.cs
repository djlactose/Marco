using System.Text;

namespace Marco.Report;

/// <summary>HTML encoding for report content. Every machine-derived string (hostname, model, software name) is
/// attacker-influenceable, so it is encoded before it reaches the document — the report is meant to be opened in
/// a browser.</summary>
public static class Html
{
    public static string Encode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
