using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace DeserokUtils;

internal static class CallCounter {
	private static volatile bool armed;
	private static DateTime until;
	private static DateTime nextFlush;
	private static readonly Dictionary<string, int> ThisSecond = new();
	private static readonly Dictionary<string, (long Total, int Peak)> Run = new();
	private static readonly TimeSpan ArmFor = TimeSpan.FromMinutes(2);

	public static void Register() {
		Plugin.RegisterSub("perfsniff", "count how often the tooltip and macro icon hooks fire, for 2 minutes",
			(_, _) => Arm());
	}

	public static void Hit(string key) {
		if (!armed) return;

		lock (ThisSecond) {
			ThisSecond[key] = ThisSecond.TryGetValue(key, out var n) ? n + 1 : 1;
		}
	}

	private static void Arm() {
		if (armed) {
			Plugin.Chat.Print("[DeserokUtils] perfsniff is already running.");
			return;
		}

		lock (ThisSecond) {
			ThisSecond.Clear();
			Run.Clear();
		}

		until = DateTime.UtcNow + ArmFor;
		nextFlush = DateTime.UtcNow.AddSeconds(1);
		armed = true;
		Plugin.Framework.Update += Tick;

		SniffLog.Mark("PERF SNIFF ARMED");
		Plugin.Chat.Print("[DeserokUtils] counting hook calls for 2 minutes. Hover some items and open your hotbars.");
	}

	private static void Tick(IFramework framework) {
		var now = DateTime.UtcNow;
		if (now < nextFlush) return;
		nextFlush = now.AddSeconds(1);

		lock (ThisSecond) {
			if (ThisSecond.Count > 0) {
				SniffLog.Write("perf " + string.Join("  ", ThisSecond.Select(kv => $"{kv.Key}={kv.Value}/s")));

				foreach (var (key, n) in ThisSecond) {
					var was = Run.TryGetValue(key, out var r) ? r : (0L, 0);
					Run[key] = (was.Item1 + n, Math.Max(was.Item2, n));
				}

				ThisSecond.Clear();
			}
		}

		if (now < until) return;

		armed = false;
		Plugin.Framework.Update -= Tick;

		var summary = Run.Count == 0
			? "nothing fired"
			: string.Join(", ", Run.Select(kv => $"{kv.Key} {kv.Value.Total} total, peak {kv.Value.Peak}/s"));

		SniffLog.Mark($"PERF SNIFF STOPPED - {summary}");
		Plugin.Chat.Print($"[DeserokUtils] perfsniff done: {summary}.");
	}

	public static void Dispose() {
		if (!armed) return;
		armed = false;
		Plugin.Framework.Update -= Tick;
	}
}
