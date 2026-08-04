using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.ClientState.Fates;

namespace DeserokUtils.Features.FateWatch;

/// <summary>
/// Watches the live FATE table for tracked names appearing, and predicts the next spawn.
///
/// ⚠⚠ THE CONSTRAINT THAT SHAPES ALL OF THIS: IFateTable only contains FATEs that are ACTIVE RIGHT
/// NOW. A FATE spawning in ten minutes is not in it and cannot be read. So "ten minutes away" is
/// never observed -- it is derived from when the last one appeared plus a cycle length, and it is
/// only as trustworthy as that anchor.
///
/// Which means: no prediction at all until one spawn has been seen. That is a real limitation, not
/// a bug, and the UI says so rather than showing a confident countdown built on nothing.
/// </summary>
internal sealed class FateTracker {
	private readonly HashSet<uint> presentLastPoll = new();

	/// <summary>Alert thresholds already fired for the current prediction, so each fires once.</summary>
	private readonly Dictionary<string, HashSet<double>> firedAlerts = new(StringComparer.OrdinalIgnoreCase);

	private DateTime lastPoll = DateTime.MinValue;

	/// <summary>
	/// Polling once a second is plenty for something on a half-hour cycle, and keeps this off the
	/// per-frame path entirely.
	/// </summary>
	private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);

	public void Tick() {
		if (!Plugin.Config.FateWatchEnabled)
			return;
		if (DateTime.UtcNow - this.lastPoll < PollEvery)
			return;
		this.lastPoll = DateTime.UtcNow;

		try {
			this.PollTable();
			this.CheckAlerts();
		}
		catch (Exception ex) {
			// Guard, but report -- a tracker that silently stopped looks exactly like a FATE that
			// never spawned, and that is the whole thing this exists to tell apart.
			Plugin.Log.Error(ex, "FateWatch: tick failed");
		}
	}

	private void PollTable() {
		var seenThisPoll = new HashSet<uint>();

		foreach (IFate? fate in Plugin.Fates) {
			if (fate is null)
				continue;

			seenThisPoll.Add(fate.FateId);

			// Only the TRANSITION matters. A FATE sitting in the table for ten minutes must not
			// re-record its spawn time every second, or the anchor walks forward and the prediction
			// drifts to "always thirty minutes from now".
			if (this.presentLastPoll.Contains(fate.FateId))
				continue;

			string name = fate.Name.TextValue;

			// ⭐ BOTH channels, deliberately. Diag goes to the Debug chat tab, which is where he will
			// look during play -- but chat scrollback is finite and a two-hour session in a busy zone
			// would push the early spawns out. Log.Information persists to dalamud.log on disk, so the
			// record survives the session and can be read back afterwards rather than transcribed.
			string line = $"FATE appeared: {name} | id={fate.FateId} lvl={fate.Level} "
				+ $"terr={Plugin.ClientState.TerritoryType} remaining={fate.TimeRemaining:0}s "
				+ $"pos=({fate.Position.X:0},{fate.Position.Z:0})";
			Plugin.Diag(line);
			Plugin.Log.Information("[FateWatch] " + line);

			if (IsTracked(name))
				this.RecordSpawn(name);
		}

		this.presentLastPoll.Clear();
		foreach (uint id in seenThisPoll)
			this.presentLastPoll.Add(id);
	}

	private void RecordSpawn(string name) {
		var cfg = Plugin.Config;
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

		if (cfg.LastSeen.TryGetValue(name, out long previous) && previous > 0) {
			double gapMinutes = (now - previous) / 60.0;

			// ⚠ Only record a gap that could plausibly be one cycle. Logging back in after two days
			// would otherwise contribute a 2,880-minute "interval" and poison the evidence.
			if (gapMinutes is > 1 and < 240) {
				if (!cfg.MeasuredIntervals.TryGetValue(name, out var list))
					cfg.MeasuredIntervals[name] = list = new List<double>();
				list.Add(Math.Round(gapMinutes, 2));
				if (list.Count > 20)
					list.RemoveAt(0);

				Plugin.Diag($"FateWatch: {name} interval measured at {gapMinutes:0.0} min "
					+ $"(configured cycle is {cfg.CycleMinutes:0.#})");
			}
			else {
				Plugin.Diag($"FateWatch: {name} gap of {gapMinutes:0.0} min ignored as not-a-cycle.");
			}
		}

		cfg.LastSeen[name] = now;
		cfg.LastSeenTerritory[name] = Plugin.ClientState.TerritoryType;
		this.firedAlerts.Remove(name);
		this.firedAlerts.Remove("__rotation");
		cfg.Save();

		cfg.FateLabels.TryGetValue(name, out string? lbl);
		Plugin.Announce($"{name}{(string.IsNullOrEmpty(lbl) ? "" : $" ({lbl})")} is up now.");
	}

	/// <summary>
	/// Anchor the cycle by hand, for what deserok already does manually: see one running, or ask
	/// shout chat. If someone says it popped six minutes ago, that is the same information the
	/// tracker would have got by watching -- there is no reason to make him wait for the next one
	/// just because the plugin was not looking.
	///
	/// ⚠ Does NOT contribute to MeasuredIntervals. A number relayed through a stranger and a
	/// remembered "about six minutes" are not the same quality of evidence as an observed spawn,
	/// and letting them into the measurement would quietly corrupt the thing that corrects the
	/// assumption.
	/// </summary>
	public void AnchorManually(string name, double minutesAgo) {
		var cfg = Plugin.Config;
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		cfg.LastSeen[name] = now - (long)(minutesAgo * 60);
		cfg.LastSeenTerritory[name] = Plugin.ClientState.TerritoryType;
		this.firedAlerts.Remove(name);
		cfg.Save();

		double? next = this.MinutesUntilNext(name);
		Plugin.Chat.Print($"[FateWatch] anchored {name} to {minutesAgo:0.#} min ago"
			+ (next is null ? "." : $" -- next in about {next:0.#} min."));
	}

	/// <summary>
	/// Anchor FORWARD: 'the next one is in N minutes'.
	///
	/// ⭐ Covers the two things actually known in the wild, neither of which is an elapsed time.
	/// The wiki rule -- a fresh instance spawns its first pot ten minutes in -- and shout chat
	/// saying 'north in 12'. Expressing either through the elapsed-time anchor means doing modular
	/// arithmetic in your head while the content is running, which nobody does correctly.
	/// </summary>
	public void AnchorForward(string name, double minutesUntil) {
		var cfg = Plugin.Config;
		double cycle = EffectivePerFateCycle(name);
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

		// Last spawn = one full cycle before the next one is due.
		cfg.LastSeen[name] = now - (long)((cycle - minutesUntil) * 60);
		cfg.LastSeenTerritory[name] = Plugin.ClientState.TerritoryType;
		this.firedAlerts.Remove(name);
		this.firedAlerts.Remove("__rotation");
		cfg.Save();

		cfg.FateLabels.TryGetValue(name, out string? lbl);
		Plugin.Chat.Print($"[FateWatch] next {name}{(string.IsNullOrEmpty(lbl) ? "" : $" ({lbl})")} "
			+ $"set to {minutesUntil:0.#} min from now.");
	}

	private void CheckAlerts() {
		var cfg = Plugin.Config;

		// In rotation mode there is ONE upcoming event, not one per member. Alerting per FATE would
		// fire twice for every slot -- once correctly, once for the member that is not next.
		if (cfg.RotationMode) {
			var next = this.NextInRotation();
			if (next is null)
				return;
			var (rname, rlabel, rmins) = next.Value;
			if (!this.firedAlerts.TryGetValue("__rotation", out var rfired))
				this.firedAlerts["__rotation"] = rfired = new HashSet<double>();
			foreach (double threshold in cfg.AlertMinutes.OrderByDescending(m => m)) {
				if (rmins <= threshold && !rfired.Contains(threshold)) {
					rfired.Add(threshold);
					Plugin.Announce($"{rname}{(rlabel.Length > 0 ? $" ({rlabel})" : "")} in about {threshold:0} minutes.");
				}
			}
			return;
		}

		foreach (string name in cfg.TrackedFates) {
			double? minutesAway = this.MinutesUntilNext(name);
			if (minutesAway is null || minutesAway < 0)
				continue;

			if (!this.firedAlerts.TryGetValue(name, out var fired))
				this.firedAlerts[name] = fired = new HashSet<double>();

			foreach (double threshold in cfg.AlertMinutes.OrderByDescending(m => m)) {
				if (minutesAway <= threshold && !fired.Contains(threshold)) {
					fired.Add(threshold);
					Plugin.Announce($"{name} in about {threshold:0} minutes.");
				}
			}
		}
	}

	/// <summary>
	/// Minutes until the next predicted spawn, or null when there is no anchor to predict from.
	/// ⚠ Null and zero are different answers and callers must not conflate them: one means "no
	/// idea", the other means "now".
	/// </summary>
	public double? MinutesUntilNext(string name) {
		var cfg = Plugin.Config;
		if (!cfg.LastSeen.TryGetValue(name, out long last) || last <= 0)
			return null;

		// ⚠⚠ In rotation mode the same FATE recurs every cycle * memberCount, NOT every cycle. Two
		// alternating pot FATEs 30 minutes apart means each one returns in 60 -- and predicting
		// either at +30 lands exactly when the OTHER is due. Confident, and wrong every time.
		double cycle = EffectiveCycle(name) * (cfg.RotationMode ? RotationLength() : 1);
		double elapsed = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - last) / 60.0;

		// Carry forward across missed cycles: if you were logged out for two hours, the next spawn
		// is the next multiple, not one that already passed.
		double remaining = cycle - (elapsed % cycle);
		return remaining;
	}

	/// <summary>
	/// The next member of the rotation, whichever FATE it is.
	///
	/// ⭐ This is the question actually being asked. "When is Daylight Pottery" matters far less
	/// than "when is the next pot, and which side do I run to" -- and the rotation answers that from
	/// a single observation of ANY member, rather than needing to have seen each one separately.
	/// </summary>
	public (string Name, string Label, double Minutes)? NextInRotation() {
		var cfg = Plugin.Config;
		if (!cfg.RotationMode || cfg.TrackedFates.Count == 0)
			return null;

		// Anchor on the most recent sighting of ANY member.
		string? lastName = null;
		long lastAt = 0;
		foreach (string n in cfg.TrackedFates) {
			if (cfg.LastSeen.TryGetValue(n, out long t) && t > lastAt) {
				lastAt = t;
				lastName = n;
			}
		}
		if (lastName is null)
			return null;

		double cycle = EffectiveCycle(lastName);
		double elapsed = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastAt) / 60.0;

		// How many slots have gone by since that sighting, and therefore how far round the ring.
		int slotsPassed = (int)Math.Floor(elapsed / cycle) + 1;
		double remaining = (slotsPassed * cycle) - elapsed;

		int lastIndex = cfg.TrackedFates.FindIndex(
			t => string.Equals(t, lastName, StringComparison.OrdinalIgnoreCase));
		if (lastIndex < 0)
			lastIndex = 0;

		string next = cfg.TrackedFates[(lastIndex + slotsPassed) % cfg.TrackedFates.Count];
		cfg.FateLabels.TryGetValue(next, out string? label);
		return (next, label ?? string.Empty, remaining);
	}

	/// <summary>
	/// The configured cycle, unless enough real intervals have been measured to disagree with it.
	/// ⭐ Median rather than mean: one missed observation produces a double-length gap, and a mean
	/// would let that single outlier drag every prediction late.
	/// </summary>
	/// <summary>
	/// How many slots the ring has, i.e. how many cycles before the SAME FATE comes round again.
	///
	/// ⚠⚠ Derived from distinct members, and this coupling is the thing to watch: tracking a third,
	/// unrelated FATE would silently change the pot timings, because 'what I track' and 'what is in
	/// the rotation' are two different ideas sharing one list. Fine while there is exactly one
	/// rotation; split them the moment there are two.
	/// </summary>
	public static int RotationLength() {
		var cfg = Plugin.Config;
		int n = cfg.TrackedFates.Distinct(StringComparer.OrdinalIgnoreCase).Count();
		return Math.Max(1, n);
	}

	/// <summary>The real gap between two spawns of the SAME fate, which is what a person means.</summary>
	public static double EffectivePerFateCycle(string name)
		=> EffectiveCycle(name) * (Plugin.Config.RotationMode ? RotationLength() : 1);

	public static double EffectiveCycle(string name) {
		var cfg = Plugin.Config;
		if (cfg.MeasuredIntervals.TryGetValue(name, out var list) && list.Count >= 3) {
			var sorted = list.OrderBy(x => x).ToList();
			return sorted[sorted.Count / 2];
		}
		return cfg.CycleMinutes;
	}

	/// <summary>
	/// The tracked FATE due soonest, with its label, or null when nothing can be predicted.
	/// Used by the server bar, which has room for exactly one thing.
	/// </summary>
	public (string Name, string Label, double Minutes)? Soonest() {
		// ⭐ Rotation first: one sighting of ANY member answers 'next pot, which side', which is the
		// question. Per-FATE timers are the fallback for tracked things that do not rotate.
		if (Plugin.Config.RotationMode) {
			var rot = this.NextInRotation();
			if (rot is not null) {
				if (Plugin.Config.DtrOnlyInZone) {
					bool anyHere = Plugin.Config.LastSeenTerritory.Values.Any(t => t == Plugin.ClientState.TerritoryType);
					if (!anyHere)
						return null;
				}
				return rot;
			}
			return null;
		}

		(string, string, double)? best = null;

		foreach (string name in Plugin.Config.TrackedFates) {
			double? mins = this.MinutesUntilNext(name);
			if (mins is null)
				continue;

			if (Plugin.Config.DtrOnlyInZone) {
				// Only in a zone where this FATE has actually been seen. Learned, not hardcoded --
				// nothing to get wrong, and it covers whatever gets tracked later for free.
				if (!Plugin.Config.LastSeenTerritory.TryGetValue(name, out uint terr)
					|| terr != Plugin.ClientState.TerritoryType)
					continue;
			}

			Plugin.Config.FateLabels.TryGetValue(name, out string? label);
			if (best is null || mins.Value < best.Value.Item3)
				best = (name, label ?? string.Empty, mins.Value);
		}

		return best;
	}

	public static bool IsTracked(string name)
		=> Plugin.Config.TrackedFates.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));

	/// <summary>Everything currently in the table, for the discovery command.</summary>
	public static List<IFate> ActiveFates() {
		var list = new List<IFate>();
		foreach (IFate? fate in Plugin.Fates) {
			if (fate is not null)
				list.Add(fate);
		}
		return list;
	}
}
