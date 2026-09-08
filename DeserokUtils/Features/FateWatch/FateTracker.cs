using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.ClientState.Fates;

namespace DeserokUtils.Features.FateWatch;

internal sealed class FateTracker {
	private readonly HashSet<uint> presentLastPoll = new();

	private readonly Dictionary<string, HashSet<double>> firedAlerts = new(StringComparer.OrdinalIgnoreCase);

	private DateTime lastPoll = DateTime.MinValue;

	private (uint Territory, uint Instance)? lastPlace;

	private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);

	public void Tick() {
		if (!Plugin.Config.FateWatchEnabled)
			return;
		if (DateTime.UtcNow - this.lastPoll < PollEvery)
			return;
		this.lastPoll = DateTime.UtcNow;

		try {

			this.CheckPlace();
			this.PollTable();
			this.CheckAlerts();
		}
		catch (Exception ex) {

			Plugin.Log.Error(ex, "PotWatch: tick failed");
		}
	}

	private void CheckPlace() {
		if (!Plugin.ClientState.IsLoggedIn) {

			if (this.lastPlace is not null) {
				this.lastPlace = null;
				this.DropAnchorsNotAt(0, 0, "logged out");
			}
			return;
		}

		uint territory = Plugin.ClientState.TerritoryType;

		if (territory == 0)
			return;

		uint instance = (uint)Plugin.ClientState.Instance;
		if (this.lastPlace is { } was && was.Territory == territory && was.Instance == instance)
			return;

		this.lastPlace = (territory, instance);
		this.DropAnchorsNotAt(territory, instance, $"now in territory {territory} instance {instance}");
	}

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

		string line = $"PotWatch: dropped {stale.Count} anchor(s) -- {reason}: {string.Join(", ", stale)}";
		Plugin.Diag(line);
		Plugin.Log.Information("[PotWatch] " + line);
	}

	private static bool AnchoredAt(string name, uint territory, uint instance) {
		var cfg = Plugin.Config;

		if (territory == 0)
			return false;
		if (!cfg.LastSeenTerritory.TryGetValue(name, out uint anchoredTerritory) || anchoredTerritory != territory)
			return false;

		if (!cfg.LastSeenInstance.TryGetValue(name, out uint anchoredInstance))
			return true;

		return anchoredInstance == instance;
	}

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

			if (this.presentLastPoll.Contains(fate.FateId))
				continue;

			string name = fate.Name.TextValue;

			string line = $"FATE appeared: {name} | id={fate.FateId} lvl={fate.Level} "
				+ $"terr={Plugin.ClientState.TerritoryType} remaining={fate.TimeRemaining:0}s "
				+ $"pos=({fate.Position.X:0},{fate.Position.Z:0})";
			Plugin.Diag(line);
			Plugin.Log.Information("[PotWatch] " + line);

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

			if (gapMinutes is > 1 and < 240) {
				if (!cfg.MeasuredIntervals.TryGetValue(name, out var list))
					cfg.MeasuredIntervals[name] = list = new List<double>();
				list.Add(Math.Round(gapMinutes, 2));
				if (list.Count > 20)
					list.RemoveAt(0);

				Plugin.Diag($"PotWatch: {name} interval measured at {gapMinutes:0.0} min "
					+ $"(expected about {EffectivePerFateCycle(name):0.#})");
			}
			else {
				Plugin.Diag($"PotWatch: {name} gap of {gapMinutes:0.0} min ignored as not-a-cycle.");
			}
		}

		cfg.LastSeen[name] = now;
		StampPlace(name);
		this.firedAlerts.Remove(name);
		this.firedAlerts.Remove("__rotation");
		cfg.Save();

		string lbl = LabelFor(name);
		Plugin.Announce($"{name}{(string.IsNullOrEmpty(lbl) ? "" : $" ({lbl})")} is up now.");
	}

	public void AnchorManually(string name, double minutesAgo) {
		var cfg = Plugin.Config;
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		cfg.LastSeen[name] = now - (long)(minutesAgo * 60);
		StampPlace(name);
		this.firedAlerts.Remove(name);
		cfg.Save();

		double? next = this.MinutesUntilNext(name);
		Plugin.Chat.Print($"[PotWatch] anchored {name} to {minutesAgo:0.#} min ago"
			+ (next is null ? "." : $" -- next in about {next:0.#} min.")
			+ " Clears when you leave this instance.");
	}

	public void AnchorForward(string name, double minutesUntil) {
		var cfg = Plugin.Config;
		double cycle = EffectivePerFateCycle(name);
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

		cfg.LastSeen[name] = now - (long)((cycle - minutesUntil) * 60);
		StampPlace(name);
		this.firedAlerts.Remove(name);
		this.firedAlerts.Remove("__rotation");
		cfg.Save();

		string lbl = LabelFor(name);
		Plugin.Chat.Print($"[PotWatch] next {name}{(string.IsNullOrEmpty(lbl) ? "" : $" ({lbl})")} "
			+ $"set to {minutesUntil:0.#} min from now. Clears when you leave this instance.");
	}

	private void CheckAlerts() {
		var cfg = Plugin.Config;

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
	}

	public double? MinutesUntilNext(string name) {
		var cfg = Plugin.Config;
		if (!cfg.LastSeen.TryGetValue(name, out long last) || last <= 0)
			return null;

		double cycle = EffectiveCycle(name) * RotationLength(RotationOf(name));
		double elapsed = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - last) / 60.0;

		double remaining = cycle - (elapsed % cycle);
		return remaining;
	}

	public (string Name, string Label, double Minutes)? NextInRotation() {
		var cfg = Plugin.Config;

		var rotation = CurrentRotation();
		if (rotation is null || rotation.Members.Count == 0)
			return null;

		string? lastName = null;
		long lastAt = 0;
		foreach (string n in rotation.Members) {
			if (cfg.LastSeen.TryGetValue(n, out long t) && t > lastAt) {
				lastAt = t;
				lastName = n;
			}
		}
		if (lastName is null)
			return null;

		double cycle = EffectiveCycle(lastName);
		double elapsed = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastAt) / 60.0;

		int slotsPassed = (int)Math.Floor(elapsed / cycle) + 1;
		double remaining = (slotsPassed * cycle) - elapsed;

		int lastIndex = rotation.Members.FindIndex(
			t => string.Equals(t, lastName, StringComparison.OrdinalIgnoreCase));
		if (lastIndex < 0)
			lastIndex = 0;

		string next = rotation.Members[(lastIndex + slotsPassed) % rotation.Members.Count];
		rotation.Labels.TryGetValue(next, out string? label);
		return (next, label ?? string.Empty, remaining);
	}

	public static FateRotation? RotationIn(uint territory)
		=> Plugin.Config.Rotations.FirstOrDefault(r => r.Territory == territory);

	public static FateRotation? CurrentRotation() => RotationIn(Plugin.ClientState.TerritoryType);

	public static FateRotation? RotationOf(string name)
		=> Plugin.Config.Rotations.FirstOrDefault(
			r => r.Members.Any(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase)));

	public static int RotationLength(FateRotation? rotation)
		=> Math.Max(1, rotation?.Members.Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 1);

	public static double EffectivePerFateCycle(string name)
		=> EffectiveCycle(name) * RotationLength(RotationOf(name));

	public static double EffectiveCycle(string name) {
		var cfg = Plugin.Config;
		var rotation = RotationOf(name);

		if (cfg.MeasuredIntervals.TryGetValue(name, out var list) && list.Count >= 3) {
			var sorted = list.OrderBy(x => x).ToList();
			double perFate = sorted[sorted.Count / 2];

			return perFate / RotationLength(rotation);
		}

		return rotation?.SlotMinutes ?? 30.0;
	}

	public (string Name, string Label, double Minutes)? Soonest() {

		return this.NextInRotation();
	}

	public static bool IsTracked(string name) => RotationOf(name) is not null;

	public static string LabelFor(string name) {
		var rotation = RotationOf(name);
		return rotation is not null && rotation.Labels.TryGetValue(name, out string? label)
			? label ?? string.Empty
			: string.Empty;
	}

	public static List<IFate> ActiveFates() {
		var list = new List<IFate>();
		foreach (IFate? fate in Plugin.Fates) {
			if (fate is not null)
				list.Add(fate);
		}
		return list;
	}
}
