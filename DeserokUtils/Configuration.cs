using System;
using System.Collections.Generic;

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
	public int Version { get; set; } = 1;

	/// <summary>Diagnostic output to the Debug chat channel.</summary>
	public bool Verbose { get; set; } = true;

	// ── FateWatch ────────────────────────────────────────────────────────────────────────────

	public bool FateWatchEnabled { get; set; } = true;

	/// <summary>FATE names to track, matched case-insensitively against the live table.</summary>
	public List<string> TrackedFates { get; set; } = new() {
		"Persistent Pots",
		"Pleading Pots",
	};

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

	public bool AlertToast { get; set; } = true;
	public bool AlertChat { get; set; } = true;
	public bool AlertSound { get; set; } = true;

	public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
