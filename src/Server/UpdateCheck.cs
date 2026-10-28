using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using MuninMap.Util;

namespace MuninMap
{
    // Is there a newer Munin Map? Asks GitHub for the project's latest release a minute after the
    // server starts and then every 12 hours, on a thread of its own (one small request; GitHub allows
    // 60 an hour). The page shows a dot on its GitHub button when the release is newer than this.
    // Off with check_updates = false: then nothing is ever sent.
    internal static class UpdateCheck
    {
        public const string Repo = "https://github.com/cocopuff2u-gaming/munin-map";
        private const string Api = "https://api.github.com/repos/cocopuff2u-gaming/munin-map/releases/latest";

        public static volatile string Latest = "";      // "3.2.2", "" until known
        public static volatile string LatestUrl = "";   // that release's page
        private static Thread thread;

        public static void Start()
        {
            if (!MuninConfig.CHECK_UPDATES || thread != null) return;
            thread = new Thread(Loop) { IsBackground = true, Name = "MuninMap update check" };
            thread.Start();
        }

        private static void Loop()
        {
            Thread.Sleep(60 * 1000);
            while (true)
            {
                try { Check(); }
                catch (Exception e) { ZLog.Log("MuninMap: update check: " + e.Message); }
                Thread.Sleep(12 * 3600 * 1000);
            }
        }

        private static void Check()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "MuninMap/" + MuninMap.VERSION;
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                var d = JsonParser.Parse(wc.DownloadString(Api)) as Dictionary<string, object>;
                string tag = (JsonParser.Str(d, "tag_name") ?? "").TrimStart('v', 'V');
                if (tag.Length == 0) return;
                LatestUrl = JsonParser.Str(d, "html_url") ?? Repo + "/releases";
                Latest = tag;
                if (Newer(tag, MuninMap.VERSION)) ZLog.Log("MuninMap: version " + tag + " is out (this is " + MuninMap.VERSION + "): " + LatestUrl);
            }
        }

        // a newer than b, by number: "3.10.0" > "3.9.1" (anything after a '-' or '+' left out)
        public static bool Newer(string a, string b)
        {
            int[] A = Parts(a), B = Parts(b);
            for (int i = 0; i < Math.Max(A.Length, B.Length); i++)
            {
                int x = i < A.Length ? A[i] : 0, y = i < B.Length ? B[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        private static int[] Parts(string v)
        {
            v = (v ?? "").Split('-', '+')[0];
            var p = v.Split('.');
            var n = new int[p.Length];
            for (int i = 0; i < p.Length; i++) int.TryParse(p[i], out n[i]);
            return n;
        }

        public static bool UpdateAvailable => Latest.Length > 0 && Newer(Latest, MuninMap.VERSION);
    }
}
