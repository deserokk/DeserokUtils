using System;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;

namespace DeserokUtils.Features.FateWatch;

internal sealed class FateWatchFeature: IDisposable {
	private readonly FateTracker tracker = new();
	private readonly IDtrBarEntry dtr;

	public string TabTitle => "PotWatch";

	public string Summary => "Warns you before an Occult Crescent pot FATE spawns. Four FATEs, on a cycle the game shows nowhere.";

	public FateWatchFeature() {
		Plugin.RegisterSub("potwatch", "Show cyclic FATE timers, or set a cycle by hand.", this.OnCommand);

		this.dtr = Plugin.Dtr.Get("PotWatch");
		this.dtr.OnClick = _ => Plugin.OpenWindow();
		this.dtr.Shown = false;
	}

	private DateTime lastDtr = DateTime.MinValue;

	public void Tick() {
		this.tracker.Tick();

		if (DateTime.UtcNow - this.lastDtr < TimeSpan.FromSeconds(1))
			return;
		this.lastDtr = DateTime.UtcNow;
		this.UpdateDtr();
	}

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
		sb.AddText("PotWatch");

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

	private static string? FindMember(string typed) =>
		Plugin.Config.Rotations
			.SelectMany(r => r.Members)
			.FirstOrDefault(m => string.Equals(m, typed, StringComparison.OrdinalIgnoreCase));

	private void OnCommand(string command, string arguments) {
		string arg = arguments.Trim().ToLowerInvariant();

		if (arg is "list" or "here" or "zone") {

			var active = FateTracker.ActiveFates();
			if (active.Count == 0) {
				Plugin.Chat.Print("[PotWatch] no FATEs active in this zone right now.");
				return;
			}

			Plugin.Chat.Print($"[PotWatch] {active.Count} active FATE(s):");
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

				Plugin.Chat.PrintError($"[PotWatch] \"{rest}\" is not tracked. Add it first, or check /potwatch list.");
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
				Plugin.Chat.PrintError($"[PotWatch] \"{rest}\" is not tracked. Try /potwatch list.");
				return;
			}
			this.tracker.AnchorForward(m2, minsUntil);
			return;
		}

		var here = FateTracker.CurrentRotation();
		if (here is null) {

			Plugin.Chat.Print(
				$"[PotWatch] no rotation for territory {Plugin.ClientState.TerritoryType}. Known: "
				+ string.Join(", ", Plugin.Config.Rotations.Select(r => $"{r.Zone} ({r.Territory})")));
			return;
		}

		Plugin.Chat.Print($"[PotWatch] {here.Zone}:");
		foreach (string name in here.Members) {
			double? mins = this.tracker.MinutesUntilNext(name);
			Plugin.Chat.Print(mins is null
				? $"  {name}: never seen yet -- no prediction possible."
				: $"  {name}: about {mins:0.#} min away.");
		}
	}

	public void DrawTab() {
		var cfg = Plugin.Config;

		var currentRotation = FateTracker.CurrentRotation();
		foreach (var rot in cfg.Rotations) {
			bool isHere = currentRotation is not null && currentRotation.Territory == rot.Territory;
			Section($"{rot.Zone}  (territory {rot.Territory}){(isHere ? "   << you are here" : "")}");
			this.DrawRotation(rot);
		}

		Section("Server bar");
		bool dtrOn = cfg.DtrEnabled;
		if (ImGui.Checkbox("Show in the server info bar", ref dtrOn)) { cfg.DtrEnabled = dtrOn; cfg.Save(); }

		Section("Alerts");
		bool toast = cfg.AlertToast, chat = cfg.AlertChat, sound = cfg.AlertSound;
		if (ImGui.Checkbox("Toast popup", ref toast)) { cfg.AlertToast = toast; cfg.Save(); }
		ImGui.SameLine();
		if (ImGui.Checkbox("Chat", ref chat)) { cfg.AlertChat = chat; cfg.Save(); }
		ImGui.SameLine();
		if (ImGui.Checkbox("Sound", ref sound)) { cfg.AlertSound = sound; cfg.Save(); }

		ImGui.TextDisabled($"Warns at: {string.Join(", ", cfg.AlertMinutes.Select(m => $"{m:0}m"))} before.");

		ImGui.Spacing();
		ImGui.TextDisabled(
			"Needs to see the FATE in your instance first to start tracking, unless set by hand.");
	}

	public void DrawDiagnostics() {
		var cfg = Plugin.Config;

		foreach (var rot in cfg.Rotations) {
			ImGui.TextDisabled($"{rot.Zone}  (territory {rot.Territory}, slot {rot.SlotMinutes:0.#}m)");

			foreach (var name in rot.Members) {
				int samples = cfg.MeasuredIntervals.TryGetValue(name, out var l) ? l.Count : 0;
				double cycle = FateTracker.EffectivePerFateCycle(name);
				string seen = cfg.LastSeen.TryGetValue(name, out var t)
					? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime.ToString("HH:mm:ss")
					: "never";

				ImGui.BulletText(
					$"{name}: every {cycle:0.#}m, {(samples >= 3 ? $"measured n={samples}" : "assumed")}"
					+ $", last seen {seen}");
			}

			ImGui.Spacing();
		}
	}

	private void DrawRotation(FateRotation rot) {
		var cfg = Plugin.Config;

		if (!ImGui.BeginTable($"fw_rot{rot.Territory}", 4,
			ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
			return;

		ImGui.TableSetupColumn("FATE");
		ImGui.TableSetupColumn("bar label", ImGuiTableColumnFlags.WidthFixed, 70f);
		ImGui.TableSetupColumn("next", ImGuiTableColumnFlags.WidthFixed, 100f);
		ImGui.TableSetupColumn("cycle", ImGuiTableColumnFlags.WidthFixed, 90f);
		ImGui.TableHeadersRow();

		foreach (string name in rot.Members) {
			ImGui.TableNextRow();

			ImGui.TableNextColumn();
			ImGui.TextUnformatted(name);

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

			ImGui.TextUnformatted($"every {FateTracker.EffectivePerFateCycle(name):0.#}m");

		}

		ImGui.EndTable();
	}

	private static void Section(string title) {
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextDisabled(title);
		ImGui.Spacing();
	}

	public void Dispose() {
		this.dtr.Remove();
	}
}
