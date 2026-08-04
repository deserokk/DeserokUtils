using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Configuration;

namespace DeserokUtils;

/// <summary>
/// Persisted settings. Dalamud stores this as JSON next to the plugin.
///
/// ⚠ Observed spawn times live here on purpose: a prediction is only as good as its anchor, and an
/// anchor that dies on logout means the first half hour of every session is blind.
/// </summary>
[Serializable]
public sealed class Configuration: IPluginConfiguration {
	/// <summary>
	/// 1 = initial. 2 = real Occult Crescent pot FATE names, replacing wiki guesses that turned out
	/// to be different FATEs entirely.
	/// ⚠ A default only applies to a config that does not exist yet. Once one is on disk, changing
	/// a default here is INERT -- the migration below is what actually reaches an existing install.
	/// </summary>
	public int Version { get; set; } = 1;

	public const int CurrentVersion = 2;

	/// <summary>Diagnostic output to the Debug chat channel.</summary>
	public bool Verbose { get; set; } = true;

	// ── FateWatch ────────────────────────────────────────────────────────────────────────────

	public bool FateWatchEnabled { get; set; } = true;

	/// <summary>FATE names to track, matched case-insensitively against the live table.</summary>
	/// <summary>
	/// ⭐ Confirmed from the live FATE table 2026-08-03, not from a wiki. The two rotate: Daylight
	/// Pottery (north) at 21:31:35, In a Pot of Bother (south) at 22:01:39 -- thirty minutes and
	/// four seconds apart, in territory 1346.
	/// </summary>
	public List<string> TrackedFates { get; set; } = new() {
		"Daylight Pottery",
		"In a Pot of Bother",
	};

	/// <summary>
	/// ⚠⚠ These FATEs ALTERNATE. The 30-minute figure is the gap between *consecutive members*, so
	/// each individual one recurs every 60 minutes. Treating them as two independent 30-minute
	/// timers predicts each at exactly the moment the OTHER is due -- confidently, and wrong every
	/// single time.
	///
	/// So the rotation is the unit, not the FATE. TrackedFates is the ring, in order.
	/// </summary>
	public bool RotationMode { get; set; } = true;

	/// <summary>
	/// Minutes between spawns of the same FATE.
	///
	/// ⚠ A DEFAULT, not a fact. Nothing published confirms it and the plugin measures the real
	/// interval as spawns accumulate -- see MeasuredIntervals. Change it here if the measurement
	/// disagrees; do not assume this number is right because it is written down.
	/// </summary>
	public double CycleMinutes { get; set; } = 30.0;

	/// <summary>Minutes before a predicted spawn to warn at. Descending.</summary>
	public List<double> AlertMinutes { get; set; } = new() { 10, 5 };

	/// <summary>Last observed spawn per FATE name, as unix seconds.</summary>
	public Dictionary<string, long> LastSeen { get; set; } = new();

	/// <summary>
	/// Gaps between consecutive observed spawns, in minutes, per FATE. This is the evidence for
	/// whether CycleMinutes is correct -- kept rather than averaged away so an outlier is visible
	/// as an outlier instead of quietly dragging the mean.
	/// </summary>
	public Dictionary<string, List<double>> MeasuredIntervals { get; set; } = new();

	/// <summary>
	/// Short label per FATE for the server bar -- "N" / "S".
	///
	/// ⚠ Separate dictionary rather than a field on a richer TrackedFates type, purely so an
	/// existing saved config keeps deserialising. Changing the shape of a list that is already on
	/// disk loses whatever was in it, and the spawn anchors are the expensive thing to lose.
	/// </summary>
	public Dictionary<string, string> FateLabels { get; set; } = new() {
		["Daylight Pottery"] = "N",
		["In a Pot of Bother"] = "S",
	};

	/// <summary>Territory each FATE was last seen in, so the bar can hide itself elsewhere.</summary>
	public Dictionary<string, uint> LastSeenTerritory { get; set; } = new();

	public bool DtrEnabled { get; set; } = true;

	/// <summary>
	/// Only show the bar entry in a zone where a tracked FATE has actually been seen.
	/// ⭐ Learned rather than hardcoded: no zone id to get wrong, and it works for whatever else
	/// gets tracked later without anyone editing a list.
	/// </summary>
	public bool DtrOnlyInZone { get; set; } = true;

	public bool AlertToast { get; set; } = true;
	public bool AlertChat { get; set; } = true;
	public bool AlertSound { get; set; } = true;

	/// <summary>
	/// ⚠⚠ Reaches an EXISTING config, which a changed default cannot. The v1 defaults were two FATE
	/// names taken from a wiki that turned out to be different FATEs; they never spawned, so they
	/// can be removed safely -- but only if they were never actually seen, in case the wiki was
	/// right about somewhere this was not tested.
	/// </summary>
	public void Migrate() {
		if (this.Version >= CurrentVersion)
			return;

		string[] wikiGuesses = { "Persistent Pots", "Pleading Pots" };
		foreach (string stale in wikiGuesses) {
			if (this.LastSeen.ContainsKey(stale))
				continue;   // it really spawned somewhere; leave it alone
			this.TrackedFates.RemoveAll(t => string.Equals(t, stale, StringComparison.OrdinalIgnoreCase));
			this.FateLabels.Remove(stale);
		}

		foreach (var (name, label) in new[] { ("Daylight Pottery", "N"), ("In a Pot of Bother", "S") }) {
			if (!this.TrackedFates.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
				this.TrackedFates.Add(name);
			if (!this.FateLabels.ContainsKey(name))
				this.FateLabels[name] = label;
		}

		this.Version = CurrentVersion;
		this.Save();
		Plugin.Log.Information($"config migrated to v{CurrentVersion}: pot FATE names corrected");
	}

	public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
