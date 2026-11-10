using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;

namespace MuninMap.Server
{
    // The link preview card (#29): Discord, Slack, iMessage and friends read Open Graph tags from the
    // page when someone posts the map's address. The title and description are always there; the
    // picture comes from the preview_image setting (a file served at /preview-image.<ext>, or a URL).
    internal static class Preview
    {
        private static string cachedFile;
        private static DateTime cachedStamp;
        private static byte[] cachedBytes;

        // the setting as a file path: environment variables expanded ($VAR, ${VAR}, %VAR%, a leading ~),
        // relative paths under BepInEx; null when it's a URL or empty
        internal static string FilePath()
        {
            string v = (MuninConfig.PREVIEW_IMAGE ?? "").Trim().Trim('"');
            if (v.Length == 0 || IsUrl(v)) return null;
            v = Environment.ExpandEnvironmentVariables(v);
            v = Regex.Replace(v, @"\$\{(\w+)\}|\$(\w+)", m => Environment.GetEnvironmentVariable(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) ?? m.Value);
            if (v == "~" || v.StartsWith("~/", StringComparison.Ordinal)) v = (Environment.GetEnvironmentVariable("HOME") ?? "") + v.Substring(1);
            try { return Path.GetFullPath(Path.IsPathRooted(v) ? v : Path.Combine(Paths.BepInExRootPath, v)); } catch { return null; }
        }

        private static bool IsUrl(string v) => v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        internal static string ContentType(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                default: return null;
            }
        }

        // the picture's bytes, re-read when the file changes; null when there is none
        internal static byte[] Image(out string ctype)
        {
            ctype = null;
            string f = FilePath();
            if (f == null || (ctype = ContentType(f)) == null) return null;
            try
            {
                var info = new FileInfo(f);
                if (!info.Exists || info.Length > 8L * 1024 * 1024) return null;
                lock (typeof(Preview))
                {
                    if (cachedFile != f || cachedStamp != info.LastWriteTimeUtc || cachedBytes == null)
                    {
                        cachedBytes = File.ReadAllBytes(f); cachedFile = f; cachedStamp = info.LastWriteTimeUtc;
                    }
                    return cachedBytes;
                }
            }
            catch { return null; }
        }

        private static bool warned;

        // The tags, for this request: apps need absolute addresses, from map_url when it's set, else from
        // the address the request came in on (X-Forwarded-* first, for a reverse proxy).
        internal static string Tags(WebSocketSharp.Net.HttpListenerRequest req, string title)
        {
            string desc = string.IsNullOrEmpty(MuninConfig.PREVIEW_DESCRIPTION) ? "Live map of " + title : MuninConfig.PREVIEW_DESCRIPTION;
            string page = BaseUrl(req);
            var sb = new StringBuilder("\n");
            Meta(sb, "property", "og:type", "website");
            Meta(sb, "property", "og:site_name", "Munin Map");
            Meta(sb, "property", "og:title", title);
            Meta(sb, "property", "og:description", desc);
            Meta(sb, "name", "description", desc);
            if (page != null) Meta(sb, "property", "og:url", page + "/");
            string v = (MuninConfig.PREVIEW_IMAGE ?? "").Trim().Trim('"');
            string img = null;
            if (IsUrl(v)) img = v;
            else if (v.Length > 0)
            {
                byte[] b = Image(out string ctype);
                if (b != null && page != null) img = page + "/preview-image" + Path.GetExtension(FilePath()).ToLowerInvariant() + "?v=" + Fnv(b).ToString("x");
                else if (b == null && !warned) { warned = true; ZLog.LogWarning("MuninMap: preview_image '" + v + "' is not a PNG, JPEG, WebP or GIF file that can be read (looked at " + FilePath() + ")"); }
            }
            if (img != null)
            {
                Meta(sb, "property", "og:image", img);
                Meta(sb, "name", "twitter:card", "summary_large_image");
                Meta(sb, "name", "twitter:image", img);
            }
            else Meta(sb, "name", "twitter:card", "summary");
            Meta(sb, "name", "twitter:title", title);
            Meta(sb, "name", "twitter:description", desc);
            return sb.ToString();
        }

        private static string BaseUrl(WebSocketSharp.Net.HttpListenerRequest req)
        {
            string set = (MuninConfig.URL ?? "").Trim();
            if (IsUrl(set))
            {
                int q = set.IndexOfAny(new[] { '?', '#' }); if (q >= 0) set = set.Substring(0, q);
                return set.TrimEnd('/');
            }
            try
            {
                string proto = First(req.Headers["X-Forwarded-Proto"]) ?? req.Url.Scheme;
                string host = First(req.Headers["X-Forwarded-Host"]) ?? req.Headers["Host"] ?? req.Url.Authority;
                if (string.IsNullOrEmpty(host) || (proto != "http" && proto != "https") || !Regex.IsMatch(host, @"^[A-Za-z0-9.\-\[\]:]+$")) return null;
                return proto + "://" + host;
            }
            catch { return null; }
        }

        private static string First(string h) { if (string.IsNullOrEmpty(h)) return null; int c = h.IndexOf(','); return (c >= 0 ? h.Substring(0, c) : h).Trim().ToLowerInvariant(); }

        private static void Meta(StringBuilder sb, string attr, string key, string value) =>
            sb.Append("<meta ").Append(attr).Append("=\"").Append(key).Append("\" content=\"").Append(Html(value)).Append("\">\n");

        private static string Html(string s) => (s ?? "").Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static uint Fnv(byte[] d) { uint h = 2166136261; foreach (byte b in d) h = (h ^ b) * 16777619; return h; }
    }
}
