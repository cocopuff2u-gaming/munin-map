using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using MuninMap.Util;

namespace MuninMap.Live
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

        // The weather to come in each biome: the same draw as above for the next periods. Picked by the
        // period alone, so it holds for everyone (alt biome variants aside, which add or block a few).
        // Main thread; made again only when the period changes.
        private static readonly KeyValuePair<Heightmap.Biome, string>[] forecastBiomes =
        {
            new KeyValuePair<Heightmap.Biome, string>(Heightmap.Biome.Meadows, "Meadows"), new KeyValuePair<Heightmap.Biome, string>(Heightmap.Biome.BlackForest, "Black Forest"),
            new KeyValuePair<Heightmap.Biome, string>(Heightmap.Biome.Swamp, "Swamp"), new KeyValuePair<Heightmap.Biome, string>(Heightmap.Biome.Mountain, "Mountains"),
            new KeyValuePair<Heightmap.Biome, string>(Heightmap.Biome.Plains, "Plains"), new KeyValuePair<Heightmap.Biome, string>(Heightmap.Biome.Mistlands, "Mistlands"),
            new KeyValuePair<Heightmap.Biome, string>(Heightmap.Biome.AshLands, "Ashlands"), new KeyValuePair<Heightmap.Biome, string>(Heightmap.Biome.DeepNorth, "Deep North"),
            new KeyValuePair<Heightmap.Biome, string>(Heightmap.Biome.Ocean, "Ocean"),
        };
        private const int ForecastPeriods = 6;
        private static string forecastJson = "null";
        private static long forecastPeriod = -1;

        public static string ForecastJson()
        {
            var env = EnvMan.instance;
            if (env == null || ZNet.instance == null) return forecastJson;
            long dur = Math.Max(1L, env.m_environmentDuration);
            long period = (long)ZNet.instance.GetTimeSeconds() / dur;
            if (period == forecastPeriod) return forecastJson;
            var state = UnityEngine.Random.state;
            try
            {
                var j = new JsonWriter(1024);
                j.BeginObject().Prop("start", period * dur).Prop("period", dur).Key("biomes").BeginArray();
                foreach (var b in forecastBiomes)
                {
                    var list = env.GetAvailableEnvironments(new BiomeSector(null, b.Key));
                    if (list == null || list.Count == 0) continue;
                    list = new List<EnvEntry>(list);   // (the game hands back a shared list)
                    j.BeginObject().Prop("biome", b.Value).Key("weather").BeginArray();
                    for (int k = 0; k < ForecastPeriods; k++)
                    {
                        UnityEngine.Random.InitState((int)(period + k));
                        EnvSetup setup = env.SelectWeightedEnvironment(list);
                        foreach (var e in list)
                        {
                            if (e.m_ashlandsOverride && b.Key == Heightmap.Biome.AshLands) setup = e.m_env;
                            if (e.m_deepnorthOverride && b.Key == Heightmap.Biome.DeepNorth) setup = e.m_env;
                        }
                        j.Value(setup != null ? Name(setup.m_name) : "");
                    }
                    j.End().End();
                }
                j.End().End();
                forecastJson = j.ToString();
                forecastPeriod = period;
            }
            catch { }
            finally { UnityEngine.Random.state = state; }
            return forecastJson;
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
