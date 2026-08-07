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
	/// Where we were last time we looked, so a move can be noticed at all.
	///
	/// ⚠⚠ An anchor is a statement about ONE instance. The pots are thirty minutes apart but NOT
	/// pegged to the clock, so a fresh instance starts a fresh ring from whenever it started -- which
	/// makes a carried-over anchor not stale-but-close, but meaningless. Null means "have not looked
	/// yet", which is also the state after a plugin reload.
	/// </summary>
	private (uint Territory, uint Instance)? lastPlace;

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
			// Order matters: drop anchors that no longer apply BEFORE predicting from them, or the
			// tick you leave the zone still gets one last callout out of the door.
			this.CheckPlace();
			this.PollTable();
			this.CheckAlerts();
		}
		catch (Exception ex) {
			// Guard, but report -- a tracker that silently stopped looks exactly like a FATE that
			// never spawned, and that is the whole thing this exists to tell apart.
			Plugin.Log.Error(ex, "FateWatch: tick failed");
		}
	}

	/// <summary>
	/// Notice that the anchors no longer belong to where we are, and drop them.
	///
	/// ⭐ Polled off the existing one-second tick rather than hooking IClientState.TerritoryChanged,
	/// because that event covers only ONE of the three ways an anchor outlives its instance. It does
	/// not fire for a plugin reload, and it does not fire for logging in somewhere else -- and both
	/// of those leave a previous instance's anchor sitting in the config, which is exactly the
	/// "still calling out pots while stood in Ul'dah" symptom. One comparison catches all three, and
	/// there is no event to unsubscribe in Dispose.
	/// </summary>
	private void CheckPlace() {
		if (!Plugin.ClientState.IsLoggedIn) {
			// Logging out ends the instance for certain, so nothing survives it.
			if (this.lastPlace is not null) {
				this.lastPlace = null;
				this.DropAnchorsNotAt(0, 0, "logged out");
			}
			return;
		}

		uint territory = Plugin.ClientState.TerritoryType;

		// ⚠ Mid-load is not a place. Territory reads 0 across a loading screen, and pruning against
		// that would throw away a good anchor every time one went past.
		if (territory == 0)
			return;

		uint instance = (uint)Plugin.ClientState.Instance;
		if (this.lastPlace is { } was && was.Territory == territory && was.Instance == instance)
			return;

		this.lastPlace = (territory, instance);
		this.DropAnchorsNotAt(territory, instance, $"now in territory {territory} instance {instance}");
	}

	/// <summary>
	/// Drop every anchor that was not made at this exact place.
	///
	/// ⭐ Keeps MeasuredIntervals. How long the cycle is, is evidence about the FATEs themselves and
	/// holds in every instance; WHEN the last one popped does not. Those two are the reason the
	/// anchor and the measurement were separate things to begin with.
	///
	/// ⭐ Free side effect worth naming: RecordSpawn measures a gap against the previous anchor, so
	/// clearing it also stops the first spawn after a zone change from contributing a garbage
	/// interval measured across two different instances.
	/// </summary>
	private void DropAnchorsNotAt(uint territory, uint instance, string reason) {
		var cfg = Plugin.Config;

		var stale = cfg.LastSeen.Keys.Where(n => !AnchoredAt(n, territory, instance)).ToList();
		if (stale.Count == 0)
			return;

		foreach (string name in stale) {
			cfg.LastSeen.Remove(name);
			cfg.LastSeenTerritory.Remove(name);
			cfg.LastSeenInstance.Remove(name);
			this.firedAlerts.Remove(name);
		}

		this.firedAlerts.Remove("__rotation");
		cfg.Save();

		// ⭐ Both channels again: a countdown vanishing is the kind of thing that reads as the plugin
		// having broken, so the reason wants to be somewhere he can find it afterwards.
		string line = $"FateWatch: dropped {stale.Count} anchor(s) -- {reason}: {string.Join(", ", stale)}";
		Plugin.Diag(line);
		Plugin.Log.Information("[FateWatch] " + line);
	}

	/// <summary>Whether this FATE's anchor was made at the given place. Territory 0 matches nothing.</summary>
	private static bool AnchoredAt(string name, uint territory, uint instance) {
		var cfg = Plugin.Config;

		if (territory == 0)
			return false;
		if (!cfg.LastSeenTerritory.TryGetValue(name, out uint anchoredTerritory) || anchoredTerritory != territory)
			return false;

		// ⚠ Missing instance means the anchor predates the field. Treat it as matching rather than
		// stale, so updating the plugin mid-session does not bin the anchor for the zone he is stood
		// in. It gets a real instance stamped on it at the next spawn.
		if (!cfg.LastSeenInstance.TryGetValue(name, out uint anchoredInstance))
			return true;

		return anchoredInstance == instance;
	}

	/// <summary>Record WHERE an anchor was made, which is what lets it be retired later.</summary>
	private static void StampPlace(string name) {
		var cfg = Plugin.Config;
		cfg.LastSeenTerritory[name] = Plugin.ClientState.TerritoryType;
		cfg.LastSeenInstance[name] = (uint)Plugin.ClientState.Instance;
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
		StampPlace(name);
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
		StampPlace(name);
		this.firedAlerts.Remove(name);
		cfg.Save();

		double? next = this.MinutesUntilNext(name);
		Plugin.Chat.Print($"[FateWatch] anchored {name} to {minutesAgo:0.#} min ago"
			+ (next is null ? "." : $" -- next in about {next:0.#} min.")
			+ " Clears when you leave this instance.");
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
		StampPlace(name);
		this.firedAlerts.Remove(name);
		this.firedAlerts.Remove("__rotation");
		cfg.Save();

		cfg.FateLabels.TryGetValue(name, out string? lbl);
		Plugin.Chat.Print($"[FateWatch] next {name}{(string.IsNullOrEmpty(lbl) ? "" : $" ({lbl})")} "
			+ $"set to {minutesUntil:0.#} min from now. Clears when you leave this instance.");
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
			double perFate = sorted[sorted.Count / 2];

			// ⚠⚠ MeasuredIntervals holds SAME-FATE gaps -- about 60 min -- because that is the only
			// thing RecordSpawn can observe: it sees "Daylight Pottery", then "Daylight Pottery"
			// again. This method returns the SLOT gap, about 30. Returning the stored number raw
			// doubled every prediction the moment a third sample landed, and it looked like the
			// timer drifting rather than the units being wrong.
			return perFate / (cfg.RotationMode ? RotationLength() : 1);
		}

		// ⚠ The fallback is already a slot gap, so it is NOT divided. The two branches return the
		// same unit by different routes, which is exactly the trap above.
		return cfg.CycleMinutes;
	}

	/// <summary>
	/// The tracked FATE due soonest, with its label, or null when nothing can be predicted.
	/// Used by the server bar, which has room for exactly one thing.
	/// </summary>
	public (string Name, string Label, double Minutes)? Soonest() {
		// ⭐ Rotation first: one sighting of ANY member answers 'next pot, which side', which is the
		// question. Per-FATE timers are the fallback for tracked things that do not rotate.
		//
		// ⭐ No zone check here. There used to be one, and it is now structurally impossible to need:
		// an anchor cannot outlive the instance it was made in, so "there is a prediction at all"
		// already means "you are where it applies".
		if (Plugin.Config.RotationMode)
			return this.NextInRotation();

		(string, string, double)? best = null;

		foreach (string name in Plugin.Config.TrackedFates) {
			double? mins = this.MinutesUntilNext(name);
			if (mins is null)
				continue;

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
