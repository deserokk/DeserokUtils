using System;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

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
	private string newFateName = string.Empty;

	public string TabTitle => "FateWatch";

	public FateWatchFeature() {
		Plugin.Commands.AddHandler("/fatewatch", new CommandInfo(this.OnCommand) {
			HelpMessage = "/fatewatch [list] -- show tracked FATE timers, or list every FATE active in this zone.",
		});
	}

	public void Tick() => this.tracker.Tick();

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
		if (ImGui.BeginTable("fw_tracked", 4,
			ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) {
			ImGui.TableSetupColumn("FATE");
			ImGui.TableSetupColumn("next", ImGuiTableColumnFlags.WidthFixed, 110f);
			ImGui.TableSetupColumn("cycle", ImGuiTableColumnFlags.WidthFixed, 130f);
			ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30f);
			ImGui.TableHeadersRow();

			string? remove = null;
			foreach (string name in cfg.TrackedFates.ToList()) {
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				ImGui.TextUnformatted(name);

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
				ImGui.TextUnformatted(samples >= 3
					? $"{cycle:0.#}m (measured, n={samples})"
					: $"{cycle:0.#}m (assumed)");

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

	public void Dispose() => Plugin.Commands.RemoveHandler("/fatewatch");
}
