using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace MuninMap.Util
{
    // How much of the game's own thread Munin takes, by job, for the perf_report setting: each main-
    // thread coroutine is run through Wrap, which times every step it takes (nested steps included,
    // run in place the way Unity would run them), and hot hooks time themselves with Start/Stop. Every
    // 5 minutes one log line: milliseconds a second per job, and the longest single step. Off, it
    // costs a flag check.
    internal static class Perf
    {
        private sealed class Slot { public long ticks, longest; public int steps; }
        private static readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>();
        private static readonly Stopwatch since = Stopwatch.StartNew();
        public static bool On => MuninConfig.PERF_REPORT;

        public static long Start() => On ? Stopwatch.GetTimestamp() : 0L;

        public static void Stop(string job, long start)
        {
            if (start == 0L) return;
            long t = Stopwatch.GetTimestamp() - start;
            lock (slots)
            {
                if (!slots.TryGetValue(job, out var s)) slots[job] = s = new Slot();
                s.ticks += t; s.steps++; if (t > s.longest) s.longest = t;
            }
        }

        // a coroutine, timed step by step (yielded IEnumerators are run inside it, as Unity runs them)
        public static IEnumerator Wrap(string job, IEnumerator body)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(body);
            while (stack.Count > 0)
            {
                long t0 = Start();
                object yielded = null;
                bool waiting = false;
                while (stack.Count > 0)
                {
                    var top = stack.Peek();
                    bool more;
                    try { more = top.MoveNext(); }
                    catch (Exception e) { UnityEngine.Debug.LogWarning("MuninMap: " + job + ": " + e.Message); more = false; }
                    if (!more) { stack.Pop(); continue; }
                    if (top.Current is IEnumerator inner) { stack.Push(inner); continue; }
                    yielded = top.Current; waiting = true;
                    break;
                }
                Stop(job, t0);
                if (waiting) yield return yielded;
            }
        }

        public static void Report()
        {
            if (!On) return;
            double secs = since.Elapsed.TotalSeconds;
            if (secs < 300) return;
            List<KeyValuePair<string, Slot>> list;
            lock (slots) { list = slots.ToList(); slots.Clear(); }
            since.Restart();
            double toMs = 1000.0 / Stopwatch.Frequency, total = 0;
            var sb = new StringBuilder();
            foreach (var kv in list.OrderByDescending(k => k.Value.ticks))
            {
                double ms = kv.Value.ticks * toMs;
                total += ms;
                sb.Append($"; {kv.Key} {ms / secs:F2} ms/s (longest {kv.Value.longest * toMs:F1} ms, {kv.Value.steps} steps)");
            }
            UnityEngine.Debug.Log($"MuninMap perf over {secs:F0} s: the game's thread {total / secs:F2} ms/s in all{sb}");
        }
    }
}
