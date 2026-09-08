using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;

using DeserokUtils.Features.EphemeralMarks;

namespace DeserokUtils.Features.DebuffMarks;

internal sealed class DebuffMarksFeature: IDisposable {
	public string TabTitle => "Debuffs";

	public string Summary => "Puts an icon over the head of anyone carrying a status you name.";

	private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(100);

	private readonly MarkFont font = new(MarkFace.Icons);
	private readonly List<(ulong Id, int Entry)> hits = new();
	private readonly Dictionary<ulong, Vector2> smoothed = new();
	private readonly HashSet<ulong> seenThisFrame = new();

	private readonly Dictionary<string, uint[]> resolved = new(StringComparer.OrdinalIgnoreCase);

	private DateTime lastScan = DateTime.MinValue;
	private DateTime lastFrame = DateTime.UtcNow;

	public DebuffMarksFeature() {
		Plugin.RegisterSub("debuffs", "Toggle the icon over anyone carrying a status you watch.", this.OnCommand);
		Plugin.PluginInterface.UiBuilder.Draw += this.Draw;
	}

	public int MarkedNow => this.hits.Count;

	private void OnCommand(string command, string arguments) {
		string arg = arguments.Trim().ToLowerInvariant();
		if (arg is "on" or "off" or "toggle" or "") {
			if (arg.Length > 0) {
				Plugin.Config.DebuffMarksEnabled = arg switch {
					"on" => true,
					"off" => false,
					_ => !Plugin.Config.DebuffMarksEnabled,
				};
				Plugin.Config.Save();
			}

			Plugin.Chat.Print($"[Debuffs] {(Plugin.Config.DebuffMarksEnabled ? "ON" : "off")}, watching "
				+ $"{Plugin.Config.DebuffMarks.Count} status(es), {this.hits.Count} marked right now.");
			return;
		}

		Plugin.Chat.PrintError($"[Debuffs] unknown argument \"{arg}\". Use on, off, or nothing.");
	}

	public void Tick() {
		if (!Plugin.Config.DebuffMarksEnabled || Plugin.Config.DebuffMarks.Count == 0)
			return;
		if (DateTime.UtcNow - this.lastScan < ScanInterval)
			return;

		this.lastScan = DateTime.UtcNow;
		this.hits.Clear();

		uint self = Plugin.Objects.LocalPlayer?.EntityId ?? 0;

		foreach (var obj in Plugin.Objects) {
			if (obj is not IBattleChara chara || !chara.IsValid())
				continue;

			for (int i = 0; i < Plugin.Config.DebuffMarks.Count; i++) {
				var watch = Plugin.Config.DebuffMarks[i];
				if (!watch.Enabled)
					continue;

				uint[] ids = this.IdsFor(watch.Status);
				if (ids.Length == 0)
					continue;

				foreach (var status in chara.StatusList) {
					if (Array.IndexOf(ids, status.StatusId) < 0)
						continue;

					if (watch.MineOnly && status.SourceId != self)
						continue;

					this.hits.Add((chara.GameObjectId, i));
					break;
				}
			}
		}
	}

	private void Draw() {

		if (Plugin.PluginInterface.UiBuilder.CutsceneActive)
			return;

		bool preview = Plugin.Config.DebuffMarksPreview;
		if (!Plugin.Config.DebuffMarksEnabled || (this.hits.Count == 0 && !preview))
			return;

		var draw = ImGui.GetBackgroundDrawList();
		float scale = ImGui.GetIO().DisplaySize.Y / 1440f * Plugin.Config.DebuffMarksScale;
		float iconPx = this.font.Prepare(30f * scale);
		using var locked = this.font.TryLock();
		ImFontPtr? face = locked is not null ? locked.ImFont : null;

		float dt = (float)(DateTime.UtcNow - this.lastFrame).TotalSeconds;
		this.lastFrame = DateTime.UtcNow;
		this.seenThisFrame.Clear();

		foreach (var (id, entry) in this.hits) {
			if (entry >= Plugin.Config.DebuffMarks.Count)
				continue;

			var obj = Plugin.Objects.SearchById(id);
			if (obj is null || !obj.IsValid())
				continue;

			var watch = Plugin.Config.DebuffMarks[entry];
			this.seenThisFrame.Add(id);

			var head = obj.Position with { Y = obj.Position.Y + Plugin.Config.DebuffMarksHeight };

			if (!Plugin.GameGui.WorldToScreen(head, out Vector2 screen))
				continue;

			screen.Y -= Plugin.Config.DebuffMarksLift * scale;
			screen = this.Smooth(id, screen, dt);

			MarkShapes.Draw(draw, watch.Shape, watch.Glyph, face, iconPx, screen,
				ImGui.ColorConvertFloat4ToU32(watch.Colour), scale);
		}

		if (preview && Plugin.Objects.LocalPlayer is { } me) {
			var watch = Plugin.Config.DebuffMarks.Count > 0 ? Plugin.Config.DebuffMarks[0] : new DebuffMark();
			var head = me.Position with { Y = me.Position.Y + Plugin.Config.DebuffMarksHeight };
			if (Plugin.GameGui.WorldToScreen(head, out Vector2 self)) {
				self.Y -= Plugin.Config.DebuffMarksLift * scale;
				MarkShapes.Draw(draw, watch.Shape, watch.Glyph, face, iconPx, self,
					ImGui.ColorConvertFloat4ToU32(watch.Colour), scale);
			}
		}

		if (this.smoothed.Count > this.seenThisFrame.Count) {
			foreach (ulong stale in new List<ulong>(this.smoothed.Keys)) {
				if (!this.seenThisFrame.Contains(stale))
					this.smoothed.Remove(stale);
			}
		}
	}

	private Vector2 Smooth(ulong id, Vector2 target, float dt) {
		if (!this.smoothed.TryGetValue(id, out var previous)) {
			this.smoothed[id] = target;
			return target;
		}

		float motion = Math.Clamp(Vector2.Distance(previous, target) / 6f, 0f, 1f);
		float tau = 0.09f + ((0.004f - 0.09f) * motion);
		float alpha = 1f - MathF.Exp(-dt / MathF.Max(tau, 0.0001f));
		var next = previous + ((target - previous) * alpha);
		this.smoothed[id] = next;
		return next;
	}

	public void DrawTab() {
		bool preview = Plugin.Config.DebuffMarksPreview;
		if (ImGui.Checkbox("Show one on me (for positioning)##debuffs", ref preview)) {
			Plugin.Config.DebuffMarksPreview = preview;
			Plugin.Config.Save();
		}

		float scale = Plugin.Config.DebuffMarksScale;
		ImGui.SetNextItemWidth(160f);
		if (ImGui.SliderFloat("Size##debuffs", ref scale, 0.4f, 2.5f, "%.2fx")) {
			Plugin.Config.DebuffMarksScale = scale;
			Plugin.Config.Save();
		}

		float height = Plugin.Config.DebuffMarksHeight;
		ImGui.SetNextItemWidth(160f);
		if (ImGui.SliderFloat("Anchor height##debuffs", ref height, 0f, 4f, "%.2f yalms")) {
			Plugin.Config.DebuffMarksHeight = height;
			Plugin.Config.Save();
		}

		float lift = Plugin.Config.DebuffMarksLift;
		ImGui.SetNextItemWidth(160f);
		if (ImGui.SliderFloat("Clearance##debuffs", ref lift, 0f, 120f, "%.0f px")) {
			Plugin.Config.DebuffMarksLift = lift;
			Plugin.Config.Save();
		}

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();
		ImGui.TextWrapped("Type the status name exactly as the game spells it, then pick a shape and colour.");
		ImGui.Spacing();

		for (int i = 0; i < Plugin.Config.DebuffMarks.Count; i++) {
			var entry = Plugin.Config.DebuffMarks[i];

			bool live = entry.Enabled;
			if (ImGui.Checkbox($"##debuffOn{i}", ref live)) {
				entry.Enabled = live;
				Plugin.Config.Save();
			}

			ImGui.SameLine();
			ImGui.SetNextItemWidth(150f);
			string name = entry.Status;
			if (ImGui.InputText($"##debuffName{i}", ref name, 48)) {
				entry.Status = name;
				Plugin.Config.Save();
			}

			ImGui.SameLine();
			uint[] ids = this.IdsFor(entry.Status);
			if (entry.Status.Length == 0)
				ImGui.TextDisabled("(empty)");
			else if (ids.Length == 0)
				ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "no such status");
			else
				ImGui.TextDisabled(ids.Length == 1 ? $"#{ids[0]}" : $"{ids.Length} ids");

			ImGui.SameLine();
			var shape = entry.Shape;
			int glyph = entry.Glyph;
			if (MarkShapes.GlyphPicker($"debuff{i}", ref shape, ref glyph, 110f)) {
				entry.Shape = shape;
				entry.Glyph = glyph;
				Plugin.Config.Save();
			}

			ImGui.SameLine();
			var colour = entry.Colour;
			if (ImGui.ColorEdit4($"##debuffCol{i}", ref colour, ImGuiColorEditFlags.NoInputs)) {
				entry.Colour = colour;
				Plugin.Config.Save();
			}

			ImGui.SameLine();
			bool mine = entry.MineOnly;
			if (ImGui.Checkbox($"mine##debuffMine{i}", ref mine)) {
				entry.MineOnly = mine;
				Plugin.Config.Save();
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Only count the status when YOU applied it. Most combat statuses are\nattributed to their caster, so leaving this off can mark the wrong target.");

			ImGui.SameLine();
			if (ImGui.Button($"Remove##debuffDel{i}")) {
				Plugin.Config.DebuffMarks.RemoveAt(i);
				Plugin.Config.Save();
				break;
			}
		}

		ImGui.Spacing();
		if (ImGui.Button("+ Watch a status##debuffs")) {
			Plugin.Config.DebuffMarks.Add(new DebuffMark());
			Plugin.Config.Save();
		}
	}

	public void DrawDiagnostics() {
		ImGui.TextDisabled($"marked right now: {this.hits.Count}");

		foreach (var (id, entry) in this.hits) {
			var watched = entry >= 0 && entry < Plugin.Config.DebuffMarks.Count
				? Plugin.Config.DebuffMarks[entry].Status
				: "?";
			ImGui.BulletText($"{id:X}  {watched}");
		}
	}

	private uint[] IdsFor(string name) {
		string wanted = name.Trim();
		if (wanted.Length == 0)
			return Array.Empty<uint>();

		if (this.resolved.TryGetValue(wanted, out uint[]? cached))
			return cached;

		var found = new List<uint>();
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>();
		if (sheet is not null) {
			foreach (var row in sheet) {
				if (string.Equals(row.Name.ExtractText(), wanted, StringComparison.OrdinalIgnoreCase))
					found.Add(row.RowId);
			}
		}

		Plugin.Log.Information($"DebuffMarks: \"{wanted}\" resolved to {found.Count} status id(s): "
			+ (found.Count > 0 ? string.Join(", ", found) : "none"));
		return this.resolved[wanted] = found.ToArray();
	}

	public void Dispose() {
		Plugin.PluginInterface.UiBuilder.Draw -= this.Draw;
		this.font.Dispose();
	}
}
