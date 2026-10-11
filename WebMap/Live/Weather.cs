using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WebMap.Live
{
    // The weather and wind a player has where they stand. A dedicated server never picks any (the
    // game picks it around the camera, EnvMan.UpdateEnvironment), but the pick is the same for
    // everyone: the time period seeds Unity's random, then a weighted draw from the biome's
    // environments. Done here the same way, for a player's position. Wind: its direction is the same
    // world-wide (four octaves seeded by time, EnvMan.UpdateWind), its strength comes from the weather.
    internal static class Weather
    {
        public struct Now { public string name; public float windDeg, wind; }   // wind 0..1, blowing towards windDeg (0 = north)

        private static readonly Dictionary<string, string> pretty = new Dictionary<string, string>();

        // main thread (Unity's random, the world generator)
        public static bool At(Vector3 pos, out Now now)
        {
            now = default;
            var env = EnvMan.instance;
            var wg = WorldGenerator.instance;
            if (env == null || wg == null || ZNet.instance == null) return false;
            long sec = (long)ZNet.instance.GetTimeSeconds();
            var state = UnityEngine.Random.state;
            try
            {
                EnvSetup setup = null;
                var sector = wg.GetBiomeSector(pos);
                var list = sector != null ? env.GetAvailableEnvironments(sector) : null;
                if (list != null && list.Count > 0)
                {
                    UnityEngine.Random.InitState((int)(sec / Math.Max(1L, env.m_environmentDuration)));
                    setup = env.SelectWeightedEnvironment(list);
                    // (the game passes the height as the second coordinate here: the same, so the same pick)
                    bool ash = WorldGenerator.IsAshlands(pos.x, pos.z), north = WorldGenerator.IsDeepnorth(pos.x, pos.y);
                    foreach (var e in list)
                    {
                        if (e.m_ashlandsOverride && ash) setup = e.m_env;
                        if (e.m_deepnorthOverride && north) setup = e.m_env;
                    }
                }
                if (setup == null) return false;
                float angle = 0f, intensity = 0.5f;
                env.AddWindOctave(sec, 1, ref angle, ref intensity);
                env.AddWindOctave(sec, 2, ref angle, ref intensity);
                env.AddWindOctave(sec, 4, ref angle, ref intensity);
                env.AddWindOctave(sec, 8, ref angle, ref intensity);
                now.windDeg = Mathf.Repeat(angle * Mathf.Rad2Deg, 360f);
                now.wind = Mathf.Clamp(Mathf.Lerp(setup.m_windMin, setup.m_windMax, intensity), 0.05f, 1f);
                now.name = Name(setup.m_name);
                return true;
            }
            catch { return false; }
            finally { UnityEngine.Random.state = state; }
        }

        // "ThunderStorm" -> "Thunderstorm", "Twilight_SnowStorm" -> "Snowstorm", "DeepForest Mist" -> "Forest mist"
        private static string Name(string env)
        {
            if (string.IsNullOrEmpty(env)) return "";
            if (pretty.TryGetValue(env, out string n)) return n;
            string s = env;
            foreach (var pre in new[] { "Twilight_", "Mistlands_", "Ashlands_", "DeepNorth_", "Heath " })
                if (s.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) s = s.Substring(pre.Length);
            s = s.Replace("DeepForest", "Forest").Replace("SnowStorm", "Snowstorm").Replace("ThunderStorm", "Thunderstorm").Replace('_', ' ');
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && char.IsUpper(c) && char.IsLower(s[i - 1])) sb.Append(' ');
                sb.Append(sb.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            }
            n = sb.ToString().Trim();
            if (n == "Ashrain") n = "Ash rain";
            pretty[env] = n;
            return n;
        }
    }
}
