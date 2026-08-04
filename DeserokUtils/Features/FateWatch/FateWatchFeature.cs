using System;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;

namespace DeserokUtils.Features.FateWatch;

/// <summary>
/// Warn before a cyclic FATE spawns. Built for Occult Crescent's pot FATEs, which run on a fixed
/// rotation that the game surfaces nowhere.
///
/// ⚠⚠ The whole design is shaped by one constraint: the FATE table holds only what is ACTIVE. A
/// spawn ten minutes out cannot be observed, so it is predicted from the last one seen. No anchor,
/// no prediction -- and the UI says "not seen yet" rather than inventing a countdown.
/// </summary>
internal sealed class FateWatchFeature: IDisposable {
	private readonly FateTracker tracker = new();
	private readonly IDtrBarEntry dtr;
	private string newFateName = string.Empty;

	public string TabTitle => "FateWatch";

	public FateWatchFeature() {
		Plugin.Commands.AddHandler("/fatewatch", new CommandInfo(this.OnCommand) {
			HelpMessage = "/fatewatch [list | anchor <name> <minsAgo> | next <name> <minsUntil>] -- timers, zone FATE list, or set the cycle by hand.",
		});

		Plugin.FateMinutes = this.tracker.MinutesUntilNext;

		this.dtr = Plugin.Dtr.Get("FateWatch");
		this.dtr.OnClick = _ => Plugin.OpenWindow();
		this.dtr.Shown = false;
	}

	public void Tick() {
		this.tracker.Tick();
		this.UpdateDtr();
	}

	/// <summary>
	/// The server bar shows the soonest tracked FATE, or nothing at all.
	///
	/// ⚠ Hidden rather than showing a placeholder when there is no prediction. A bar entry reading
	/// "--" is a thing you learn to ignore, and once ignored it is no longer a bar entry.
	/// </summary>
	private void UpdateDtr() {
		if (!Plugin.Config.DtrEnabled || !Plugin.Config.FateWatchEnabled) {
			this.dtr.Shown = false;
			return;
		}

		var soonest = this.tracker.Soonest();
		if (soonest is null) {
			this.dtr.Shown = false;
			return;
		}

		var (name, label, mins) = soonest.Value;

		// ⚠ No pot icon exists. BitmapFontIcon is a fixed set of game glyphs, so GoldStar is the
		// closest honest stand-in -- and swapping to Warning under five minutes carries urgency
		// without needing colour, which reads badly against the bar's own background anyway.
		var icon = mins <= 5 ? BitmapFontIcon.Warning : BitmapFontIcon.GoldStar;

		string text = mins < 1
			? $"<1m{(label.Length > 0 ? " " + label : "")}"
			: $"{Math.Floor(mins):0}m{(label.Length > 0 ? " " + label : "")}";

		this.dtr.Text = new SeStringBuilder().AddIcon(icon).AddText(text).Build();
		this.dtr.Tooltip = BuildTooltip();
		this.dtr.Shown = true;
	}

	private static SeString BuildTooltip() {
		var sb = new SeStringBuilder();
		sb.AddText("FateWatch");
		foreach (string n in Plugin.Config.TrackedFates) {
			Plugin.Config.FateLabels.TryGetValue(n, out string? lbl);
			sb.AddText($"\n{n}{(string.IsNullOrEmpty(lbl) ? "" : $" [{lbl}]")}: ");
			double? m = Plugin.FateMinutes(n);
			sb.AddText(m is null ? "not seen yet" : $"{m:0.#} min");
		}
		return sb.Build();
	}

	private void OnCommand(string command, string arguments) {
		string arg = arguments.Trim().ToLowerInvariant();

		if (arg is "list" or "here" or "zone") {
			// ⭐ The discovery command. Names from a wiki may be stale, localised, or simply wrong;
			// this reads them out of the running game, which cannot be.
			var active = FateTracker.ActiveFates();
			if (active.Count == 0) {
				Plugin.Chat.Print("[FateWatch] no FATEs active in this zone right now.");
				return;
			}

			Plugin.Chat.Print($"[FateWatch] {active.Count} active FATE(s):");
			foreach (var f in active.OrderBy(f => f.Name.TextValue)) {
				string tracked = FateTracker.IsTracked(f.Name.TextValue) ? "  [tracked]" : "";
				Plugin.Chat.Print(
					$"  {f.Name.TextValue}  (id {f.FateId}, lvl {f.Level}, {f.Progress}%, "
					+ $"{TimeSpan.FromSeconds(Math.Max(0, f.TimeRemaining)):mm\\:ss} left){tracked}");
			}
			return;
		}

		if (arg.StartsWith("anchor")) {
			string rest = arguments.Trim()[6..].Trim();
			double minsAgo = 0;
			int sp = rest.LastIndexOf(' ');
			if (sp > 0 && double.TryParse(rest[(sp + 1)..], out double parsed)) {
				minsAgo = parsed;
				rest = rest[..sp].Trim();
			}
			rest = rest.Trim('"');
			string? match = Plugin.Config.TrackedFates.FirstOrDefault(t => string.Equals(t, rest, StringComparison.OrdinalIgnoreCase));
			if (match is null) {
				// ⚠ Anchoring an untracked name would store a time nothing ever reads. Say so.
				Plugin.Chat.PrintError($"[FateWatch] \"{rest}\" is not tracked. Add it first, or check /fatewatch list.");
				return;
			}
			this.tracker.AnchorManually(match, minsAgo);
			return;
		}

		if (arg.StartsWith("next")) {
			string rest = arguments.Trim()[4..].Trim();
			double minsUntil = 0;
			int sp = rest.LastIndexOf(' ');
			if (sp > 0 && double.TryParse(rest[(sp + 1)..], out double parsed)) {
				minsUntil = parsed;
				rest = rest[..sp].Trim();
			}
			rest = rest.Trim('"');
			string? m2 = Plugin.Config.TrackedFates.FirstOrDefault(t => string.Equals(t, rest, StringComparison.OrdinalIgnoreCase));
			if (m2 is null) {
				Plugin.Chat.PrintError($"[FateWatch] \"{rest}\" is not tracked. Try /fatewatch list.");
				return;
			}
			this.tracker.AnchorForward(m2, minsUntil);
			return;
		}

		foreach (string name in Plugin.Config.TrackedFates) {
			double? mins = this.tracker.MinutesUntilNext(name);
			Plugin.Chat.Print(mins is null
				? $"[FateWatch] {name}: never seen yet -- no prediction possible."
				: $"[FateWatch] {name}: about {mins:0.#} min away.");
		}
	}

	// ── the tab ──────────────────────────────────────────────────────────────────────────────

	public void DrawTab() {
		var cfg = Plugin.Config;

		ImGui.TextWrapped(
			"Warns before a cyclic FATE spawns. The game never says when the next one is due, so it "
			+ "is predicted from the last one this plugin actually saw.");
		ImGui.Spacing();

		bool enabled = cfg.FateWatchEnabled;
		if (ImGui.Checkbox("Enabled", ref enabled)) { cfg.FateWatchEnabled = enabled; cfg.Save(); }

		Section("Tracked FATEs");
		if (ImGui.BeginTable("fw_tracked", 5,
			ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) {
			ImGui.TableSetupColumn("FATE");
			ImGui.TableSetupColumn("bar label", ImGuiTableColumnFlags.WidthFixed, 80f);
			ImGui.TableSetupColumn("next", ImGuiTableColumnFlags.WidthFixed, 110f);
			ImGui.TableSetupColumn("cycle", ImGuiTableColumnFlags.WidthFixed, 130f);
			ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30f);
			ImGui.TableHeadersRow();

			string? remove = null;
			foreach (string name in cfg.TrackedFates.ToList()) {
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				ImGui.TextUnformatted(name);

				// The short label that rides in the server bar -- "N" / "S". Editable because only
				// deserok knows which side each FATE is on; guessing it would put a confident wrong
				// direction in front of him at the exact moment he is deciding where to run.
				ImGui.TableNextColumn();
				cfg.FateLabels.TryGetValue(name, out string? lbl);
				string edit = lbl ?? string.Empty;
				ImGui.SetNextItemWidth(-1);
				if (ImGui.InputText($"##lbl{name}", ref edit, 8)) {
					cfg.FateLabels[name] = edit.Trim();
					cfg.Save();
				}

				ImGui.TableNextColumn();
				double? mins = this.tracker.MinutesUntilNext(name);
				if (mins is null) {
					// ⚠ Not a countdown of zero, and not blank. "Never seen" is a distinct state and
					// showing it as 0:00 would be a confident lie.
					ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "not seen yet");
				}
				else {
					var span = TimeSpan.FromMinutes(mins.Value);
					var colour = mins <= 5 ? new Vector4(1f, 0.5f, 0.4f, 1f)
						: mins <= 10 ? new Vector4(1f, 0.85f, 0.3f, 1f)
						: new Vector4(0.55f, 0.9f, 0.55f, 1f);
					ImGui.TextColored(colour, $"{span:mm\\:ss}");
				}

				ImGui.TableNextColumn();
				double cycle = FateTracker.EffectiveCycle(name);
				int samples = cfg.MeasuredIntervals.TryGetValue(name, out var l) ? l.Count : 0;
				// ⭐ Says whether the number is measured or merely assumed. A configured default and
				// a value derived from twelve observations deserve different confidence.
				// ⭐ Show BOTH: the slot gap and how long until this same FATE returns. The screenshot
				// bug was invisible precisely because only one number was on screen.
				double perFate = FateTracker.EffectivePerFateCycle(name);
				ImGui.TextUnformatted(samples >= 3
					? $"slot {cycle:0.#}m -> {perFate:0.#}m (measured, n={samples})"
					: $"slot {cycle:0.#}m -> {perFate:0.#}m (assumed)");

				ImGui.TableNextColumn();
				if (ImGui.SmallButton($"x##rm{name}"))
					remove = name;
			}

			ImGui.EndTable();

			if (remove is not null) {
				cfg.TrackedFates.Remove(remove);
				cfg.Save();
			}
		}

		ImGui.SetNextItemWidth(240f);
		ImGui.InputTextWithHint("##fw_add", "exact FATE name", ref this.newFateName, 128);
		ImGui.SameLine();
		if (ImGui.Button("Add") && this.newFateName.Trim().Length > 0) {
			string n = this.newFateName.Trim();
			if (!cfg.TrackedFates.Contains(n, StringComparer.OrdinalIgnoreCase)) {
				cfg.TrackedFates.Add(n);
				cfg.Save();
			}
			this.newFateName = string.Empty;
		}
		ImGui.TextDisabled("Run /fatewatch list in the zone to read exact names out of the game.");

		Section("Setting the clock by hand");
		ImGui.TextWrapped(
			"Two ways, because neither thing you actually know is an elapsed time:");
		ImGui.BulletText("/fatewatch next \"Daylight Pottery\" 10");
		ImGui.TextDisabled("   a fresh instance spawns its first pot ten minutes in, per the wiki --");
		ImGui.TextDisabled("   and this is also how you use 'north in 12' from shout chat.");
		ImGui.BulletText("/fatewatch anchor \"In a Pot of Bother\" 6");
		ImGui.TextDisabled("   for when one popped six minutes ago.");
		ImGui.TextWrapped(
			"Neither feeds the measured interval. A number from a stranger is not the same evidence as "
			+ "an observed spawn, and letting it in would corrupt the thing that corrects the assumption.");

		Section("Server bar");
		bool dtrOn = cfg.DtrEnabled;
		if (ImGui.Checkbox("Show in the server info bar", ref dtrOn)) { cfg.DtrEnabled = dtrOn; cfg.Save(); }
		bool onlyZone = cfg.DtrOnlyInZone;
		if (ImGui.Checkbox("Only in a zone where it has been seen", ref onlyZone)) { cfg.DtrOnlyInZone = onlyZone; cfg.Save(); }
		ImGui.TextDisabled("Shows the soonest one, e.g. \"12m N\". Hidden entirely when nothing can be predicted.");
		ImGui.TextDisabled("No pot icon exists in the game's font -- a gold star is the stand-in, and it turns to a warning under 5 min.");

		Section("Alerts");
		bool toast = cfg.AlertToast, chat = cfg.AlertChat, sound = cfg.AlertSound;
		if (ImGui.Checkbox("Toast popup", ref toast)) { cfg.AlertToast = toast; cfg.Save(); }
		ImGui.SameLine();
		if (ImGui.Checkbox("Chat", ref chat)) { cfg.AlertChat = chat; cfg.Save(); }
		ImGui.SameLine();
		if (ImGui.Checkbox("Sound", ref sound)) { cfg.AlertSound = sound; cfg.Save(); }

		ImGui.TextDisabled($"Warns at: {string.Join(", ", cfg.AlertMinutes.Select(m => $"{m:0}m"))} before.");

		double cycleMin = cfg.CycleMinutes;
		ImGui.SetNextItemWidth(120f);
		if (ImGui.InputDouble("Assumed cycle (min)", ref cycleMin, 1, 5, "%.1f")) {
			cfg.CycleMinutes = Math.Clamp(cycleMin, 1, 240);
			cfg.Save();
		}

		Section("Why it needs to see one first");
		ImGui.TextWrapped(
			"The game's FATE table only contains FATEs that are active right now. One spawning in ten "
			+ "minutes is not in it and cannot be read at all. So the countdown is the last observed "
			+ "spawn plus the cycle length -- which means the first one after installing is a "
			+ "surprise, and everything after it is predicted. Each real spawn re-anchors the clock, "
			+ "and once three intervals are recorded the measured value replaces the assumed one.");
	}

	private static void Section(string title) {
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextDisabled(title);
		ImGui.Spacing();
	}

	public void Dispose() {
		Plugin.Commands.RemoveHandler("/fatewatch");
		this.dtr.Remove();
	}
}
