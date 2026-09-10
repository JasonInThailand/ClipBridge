using System.Text.RegularExpressions;

namespace ClipBridge.Core;

public enum Transform { None, Plain, Upper, Lower, Trim, CleanUrl }

public static class Transforms
{
    private static readonly string[] TrackingParams =
    {
        "utm_source","utm_medium","utm_campaign","utm_term","utm_content","utm_id","fbclid","gclid","gclsrc","dclid",
        "msclkid","mc_cid","mc_eid","igshid","yclid","_hsenc","_hsmi","vero_id","ref_src","si","feature","spm","_ga",
    };

    private static readonly Regex UrlRx = new(@"https?://[^\s<>""']+", RegexOptions.Compiled);

    public static string Apply(string text, Transform t) => t switch
    {
        Transform.Upper => text.ToUpperInvariant(),
        Transform.Lower => text.ToLowerInvariant(),
        Transform.Trim => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim())).Trim(),
        Transform.CleanUrl => UrlRx.Replace(text, m => CleanOne(m.Value)),
        _ => text,
    };

    private static string CleanOne(string url)
    {
        try
        {
            var trailing = "";
            while (url.Length > 0 && ".,;)".Contains(url[^1])) { trailing = url[^1] + trailing; url = url[..^1]; }
            var u = new Uri(url);
            if (string.IsNullOrEmpty(u.Query)) return url + trailing;
            var kept = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(kv => !TrackingParams.Contains(kv.Split('=')[0], StringComparer.OrdinalIgnoreCase))
                .ToList();
            var b = new UriBuilder(u) { Query = kept.Count == 0 ? "" : string.Join("&", kept) };
            var s = b.Uri.ToString();
            if (u.IsDefaultPort) s = s.Replace(":" + u.Port + "/", "/");
            return s + trailing;
        }
        catch { return url; }
    }
}
