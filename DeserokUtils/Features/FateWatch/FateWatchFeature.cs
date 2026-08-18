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

		this.dtr = Plugin.Dtr.Get("FateWatch");
		this.dtr.OnClick = _ => Plugin.OpenWindow();
		this.dtr.Shown = false;
	}

	/// <summary>
	/// ⚠⚠ The bar is throttled, and it was not. UpdateDtr ran EVERY FRAME: Soonest() re-derived the
	/// prediction, and BuildTooltip allocated a fresh SeString with a line per tracked FATE. Sixty
	/// times a second, to display a number in whole minutes that changes once a minute.
	///
	/// ⭐ Matched to the tracker's own 1s poll, because nothing can change between polls anyway --
	/// the bar was redrawing from data that provably had not moved.
	/// </summary>
	private DateTime lastDtr = DateTime.MinValue;

	public void Tick() {
		this.tracker.Tick();

		if (DateTime.UtcNow - this.lastDtr < TimeSpan.FromSeconds(1))
			return;
		this.lastDtr = DateTime.UtcNow;
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
		this.dtr.Tooltip = this.BuildTooltip();
		this.dtr.Shown = true;
	}

	private SeString BuildTooltip() {
		var sb = new SeStringBuilder();
		sb.AddText("FateWatch");

		// ⚠ Only THIS zone's ring. Listing every rotation would put North Horn's pots in the tooltip
		// while stood in South Horn, next to timers that cannot apply here.
		var rotation = FateTracker.CurrentRotation();
		if (rotation is null) {
			sb.AddText("\nno rotation for this zone");
			return sb.Build();
		}

		sb.AddText($"\n{rotation.Zone}");
		foreach (string n in rotation.Members) {
			string lbl = FateTracker.LabelFor(n);
			sb.AddText($"\n{n}{(lbl.Length == 0 ? "" : $" [{lbl}]")}: ");
			double? m = this.tracker.MinutesUntilNext(n);
			sb.AddText(m is null ? "not seen yet" : $"{m:0.#} min");
		}
		return sb.Build();
	}

	/// <summary>
	/// Find a FATE by name across EVERY rotation, not just this zone's.
	///
	/// ⚠ Deliberately not scoped to the current territory: anchoring by hand from shout chat is
	/// exactly the case where you might be setting up a zone you are about to travel to.
	/// </summary>
	private static string? FindMember(string typed) =>
		Plugin.Config.Rotations
			.SelectMany(r => r.Members)
			.FirstOrDefault(m => string.Equals(m, typed, StringComparison.OrdinalIgnoreCase));

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
			string? match = FindMember(rest);
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
			string? m2 = FindMember(rest);
			if (m2 is null) {
				Plugin.Chat.PrintError($"[FateWatch] \"{rest}\" is not tracked. Try /fatewatch list.");
				return;
			}
			this.tracker.AnchorForward(m2, minsUntil);
			return;
		}

		var here = FateTracker.CurrentRotation();
		if (here is null) {
			// ⚠ Says which zones it DOES know. "Nothing here" alone reads as broken; naming the
			// rotations it has makes it obvious this is a zone without one rather than a dead plugin.
			Plugin.Chat.Print(
				$"[FateWatch] no rotation for territory {Plugin.ClientState.TerritoryType}. Known: "
				+ string.Join(", ", Plugin.Config.Rotations.Select(r => $"{r.Zone} ({r.Territory})")));
			return;
		}

		Plugin.Chat.Print($"[FateWatch] {here.Zone}:");
		foreach (string name in here.Members) {
			double? mins = this.tracker.MinutesUntilNext(name);
			Plugin.Chat.Print(mins is null
				? $"  {name}: never seen yet -- no prediction possible."
				: $"  {name}: about {mins:0.#} min away.");
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

		// ⭐ One table PER ROTATION. A single flat list is what made two zones' pots look like one
		// four-member ring, which halved every prediction in both.
		var currentRotation = FateTracker.CurrentRotation();
		foreach (var rot in cfg.Rotations) {
			bool isHere = currentRotation is not null && currentRotation.Territory == rot.Territory;
			Section($"{rot.Zone}  (territory {rot.Territory}){(isHere ? "   << you are here" : "")}");
			this.DrawRotation(rot, isHere);
		}


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
		ImGui.TextDisabled("Shows the soonest one, e.g. \"12m N\". Hidden entirely when nothing can be predicted.");
		ImGui.TextDisabled("Only ever appears in the instance the timer was anchored in -- leaving clears it.");
		ImGui.TextDisabled("No pot icon exists in the game's font -- a gold star is the stand-in, and it turns to a warning under 5 min.");

		Section("Alerts");
		bool toast = cfg.AlertToast, chat = cfg.AlertChat, sound = cfg.AlertSound;
		if (ImGui.Checkbox("Toast popup", ref toast)) { cfg.AlertToast = toast; cfg.Save(); }
		ImGui.SameLine();
		if (ImGui.Checkbox("Chat", ref chat)) { cfg.AlertChat = chat; cfg.Save(); }
		ImGui.SameLine();
		if (ImGui.Checkbox("Sound", ref sound)) { cfg.AlertSound = sound; cfg.Save(); }

		ImGui.TextDisabled($"Warns at: {string.Join(", ", cfg.AlertMinutes.Select(m => $"{m:0}m"))} before.");

		Section("Why it needs to see one first");
		ImGui.TextWrapped(
			"The game's FATE table only contains FATEs that are active right now. One spawning in ten "
			+ "minutes is not in it and cannot be read at all. So the countdown is the last observed "
			+ "spawn plus the cycle length -- which means the first one after installing is a "
			+ "surprise, and everything after it is predicted. Each real spawn re-anchors the clock, "
			+ "and once three intervals are recorded the measured value replaces the assumed one.");

		ImGui.Spacing();
		ImGui.TextWrapped(
			"And why the timers empty when you leave: the pots are thirty minutes apart but not pegged "
			+ "to the clock, so a fresh instance starts a fresh cycle. Carrying the old anchor across "
			+ "would give you a confident countdown to nothing -- and alerts in Ul'dah. The measured "
			+ "cycle length is kept; only the 'last seen' is dropped.");
	}

	/// <summary>
	/// One rotation's members, timers and measured cycles.
	///
	/// ⚠ The ORDER of the rows is the ring order, and it is load-bearing -- it decides which FATE
	/// comes next. The up/down buttons exist because the order cannot be inferred: the plugin only
	/// ever sees one member at a time, so it can never observe the ring itself.
	/// </summary>
	private void DrawRotation(FateRotation rot, bool isHere) {
		var cfg = Plugin.Config;

		if (!ImGui.BeginTable($"fw_rot{rot.Territory}", 6,
			ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
			return;

		ImGui.TableSetupColumn("FATE");
		ImGui.TableSetupColumn("bar label", ImGuiTableColumnFlags.WidthFixed, 70f);
		ImGui.TableSetupColumn("next", ImGuiTableColumnFlags.WidthFixed, 100f);
		ImGui.TableSetupColumn("cycle", ImGuiTableColumnFlags.WidthFixed, 190f);
		ImGui.TableSetupColumn("order", ImGuiTableColumnFlags.WidthFixed, 56f);
		ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 28f);
		ImGui.TableHeadersRow();

		string? remove = null;
		int moveFrom = -1, moveTo = -1;

		for (int i = 0; i < rot.Members.Count; i++) {
			string name = rot.Members[i];
			ImGui.TableNextRow();

			ImGui.TableNextColumn();
			ImGui.TextUnformatted(name);

			// ⚠ Editable, because only deserok knows which end of the map each one is on. Guessing
			// would put a confident wrong direction in front of him at the moment he is deciding
			// where to run -- and South Horn's are blank precisely because nobody has looked yet.
			ImGui.TableNextColumn();
			rot.Labels.TryGetValue(name, out string? lbl);
			string edit = lbl ?? string.Empty;
			ImGui.SetNextItemWidth(-1);
			if (ImGui.InputText($"##lbl{rot.Territory}{name}", ref edit, 8)) {
				rot.Labels[name] = edit.Trim();
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
			// ⭐ Show BOTH numbers: the slot gap and how long until this same FATE returns. The
			// unit-mismatch bug was invisible precisely because only one of them was on screen.
			double cycle = FateTracker.EffectiveCycle(name);
			double perFate = FateTracker.EffectivePerFateCycle(name);
			int samples = cfg.MeasuredIntervals.TryGetValue(name, out var l) ? l.Count : 0;
			ImGui.TextUnformatted(samples >= 3
				? $"slot {cycle:0.#}m -> {perFate:0.#}m (measured, n={samples})"
				: $"slot {cycle:0.#}m -> {perFate:0.#}m (assumed)");

			ImGui.TableNextColumn();
			if (ImGui.SmallButton($"^##up{rot.Territory}{name}") && i > 0) { moveFrom = i; moveTo = i - 1; }
			ImGui.SameLine();
			if (ImGui.SmallButton($"v##dn{rot.Territory}{name}") && i < rot.Members.Count - 1) { moveFrom = i; moveTo = i + 1; }

			ImGui.TableNextColumn();
			if (ImGui.SmallButton($"x##rm{rot.Territory}{name}"))
				remove = name;
		}

		ImGui.EndTable();

		if (moveFrom >= 0) {
			(rot.Members[moveFrom], rot.Members[moveTo]) = (rot.Members[moveTo], rot.Members[moveFrom]);
			cfg.Save();
		}
		if (remove is not null) {
			rot.Members.Remove(remove);
			rot.Labels.Remove(remove);
			cfg.Save();
		}

		double slot = rot.SlotMinutes;
		ImGui.SetNextItemWidth(120f);
		if (ImGui.InputDouble($"Assumed slot gap (min)##slot{rot.Territory}", ref slot, 1, 5, "%.1f")) {
			rot.SlotMinutes = Math.Clamp(slot, 1, 240);
			cfg.Save();
		}
		ImGui.SameLine();
		ImGui.TextDisabled($"so the same one returns every ~{rot.SlotMinutes * Math.Max(1, rot.Members.Count):0.#}m");

		// ⚠ Adding is offered only for the zone you are standing in: that is the only place
		// /fatewatch list can give you the exact name, and a typo here tracks nothing, silently.
		if (!isHere)
			return;

		ImGui.SetNextItemWidth(240f);
		ImGui.InputTextWithHint($"##fw_add{rot.Territory}", "exact FATE name", ref this.newFateName, 128);
		ImGui.SameLine();
		if (ImGui.Button($"Add##{rot.Territory}") && this.newFateName.Trim().Length > 0) {
			string n = this.newFateName.Trim();
			if (!rot.Members.Contains(n, StringComparer.OrdinalIgnoreCase)) {
				rot.Members.Add(n);
				cfg.Save();
			}
			this.newFateName = string.Empty;
		}
		ImGui.TextDisabled("Run /fatewatch list to read exact names out of the running game.");
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
