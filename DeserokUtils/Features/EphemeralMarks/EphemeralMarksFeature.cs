using System;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface;

namespace DeserokUtils.Features.EphemeralMarks;

internal sealed unsafe class EphemeralMarksFeature: IDisposable {
	public string TabTitle => "Marks";

	public string Summary => "A marker over the heads of the people you queued into large-scale content with, displayed only to you.";

	private readonly MarkTracker tracker = new();

	private readonly MarkFont font = new(MarkFace.Axis);

	private readonly MarkFont iconFont = new(MarkFace.Icons);

	public EphemeralMarksFeature() {
		Plugin.RegisterSub("marks", "Toggle the private markers over the people you queued in with.", this.OnCommand);
		Plugin.PluginInterface.UiBuilder.Draw += this.DrawOverlay;
	}

	public void Tick() => this.tracker.Tick();

	private void OnCommand(string command, string arguments) {
		string arg = arguments.Trim().ToLowerInvariant();

		switch (arg) {
			case "on" or "off" or "toggle":
				Plugin.Config.MarksEnabled = arg switch {
					"on" => true,
					"off" => false,
					_ => !Plugin.Config.MarksEnabled,
				};
				Plugin.Config.Save();
				Plugin.Chat.Print($"[Marks] {(Plugin.Config.MarksEnabled ? "ON" : "off")}.");
				return;

			case "":
				Plugin.Chat.Print(this.tracker.Idle is null
					? $"[Marks] active in {this.tracker.Group} -- marking {this.tracker.ResolvedCount} of "
						+ $"{this.tracker.Snapshot.Count} ({string.Join(", ", System.Linq.Enumerable.Select(this.tracker.Snapshot, s => s.Name))})."
					: $"[Marks] idle: {this.tracker.Idle}.");
				return;
		}

		Plugin.Chat.PrintError($"[Marks] unknown argument \"{arg}\". Use on, off, or nothing.");
	}

	private void DrawOverlay() {

		if (Plugin.PluginInterface.UiBuilder.CutsceneActive)
			return;

		bool preview = Plugin.Config.MarksPreview;
		if (!Plugin.Config.MarksEnabled || (this.tracker.Idle is not null && !preview))
			return;

		var draw = ImGui.GetBackgroundDrawList();
		uint sharedColour = ImGui.ColorConvertFloat4ToU32(Plugin.Config.MarksColour);

		float scale = ImGui.GetIO().DisplaySize.Y / 1440f * Plugin.Config.MarksScale;

		this.seenThisFrame.Clear();

		bool wantTags = Plugin.Config.MarksShowTag;
		float fontPx = ImGui.GetFontSize() * scale;
		if (wantTags)
			fontPx = this.font.Prepare(fontPx);
		using var locked = wantTags ? this.font.TryLock() : null;

		bool wantIcons = UsesIcons();
		float iconPx = 30f * scale;
		if (wantIcons)
			iconPx = this.iconFont.Prepare(iconPx);
		using var iconLock = wantIcons ? this.iconFont.TryLock() : null;
		ImFontPtr? iconFace = iconLock is not null ? iconLock.ImFont : null;

		foreach (var (obj, isLeader, tag, key) in this.tracker.Marked()) {
			this.seenThisFrame.Add(obj.GameObjectId);

			var basePos = RenderPosition(obj);
			var head = basePos with { Y = basePos.Y + Plugin.Config.MarksHeight };

			if (!Plugin.GameGui.WorldToScreen(head, out Vector2 screen))
				continue;

			screen.Y -= Plugin.Config.MarksLift * scale;
			screen = this.Smooth(obj.GameObjectId, screen);

			var (shape, glyph, own) = StyleFor(key, isLeader);
			uint colour = own is null ? sharedColour : ImGui.ColorConvertFloat4ToU32(own.Value);

			MarkShapes.Draw(draw, shape, glyph, iconFace, iconPx, screen, colour, scale);

			if (wantTags && tag.Length > 0) {

				Vector2 glyphs;
				if (locked is not null) {
					ImGui.PushFont(locked.ImFont);
					glyphs = ImGui.CalcTextSize(tag);
					ImGui.PopFont();
				}
				else {
					glyphs = ImGui.CalcTextSize(tag) * (fontPx / ImGui.GetFontSize());
				}

				var at = new Vector2(
					MathF.Round(screen.X - glyphs.X / 2f),
					MathF.Round(screen.Y + 3f * scale));

				var face = locked is not null ? locked.ImFont : ImGui.GetFont();
				draw.AddText(face, fontPx, at + new Vector2(1f, 1f), Shadow, tag);
				draw.AddText(face, fontPx, at, colour, tag);
			}
		}

		if (preview && Plugin.Objects.LocalPlayer is { } me) {
			var selfHead = me.Position with { Y = me.Position.Y + Plugin.Config.MarksHeight };
			if (Plugin.GameGui.WorldToScreen(selfHead, out Vector2 selfScreen)) {
				selfScreen.Y -= Plugin.Config.MarksLift * scale;
				MarkShapes.Draw(draw, Plugin.Config.MarksMemberShape, Plugin.Config.MarksMemberGlyph,
					iconFace, iconPx, selfScreen, sharedColour, scale);
			}
		}

		if (this.smoothed.Count > this.seenThisFrame.Count) {
			foreach (ulong stale in this.smoothed.Keys.Where(k => !this.seenThisFrame.Contains(k)).ToList())
				this.smoothed.Remove(stale);
		}
	}

	private readonly System.Collections.Generic.HashSet<ulong> seenThisFrame = new();

	private readonly System.Collections.Generic.Dictionary<ulong, Vector2> smoothed = new();

	private Vector2 Smooth(ulong id, Vector2 target) {
		if (!this.smoothed.TryGetValue(id, out var previous)) {
			this.smoothed[id] = target;
			return target;
		}

		if (Vector2.DistanceSquared(previous, target) > 250f * 250f) {
			this.smoothed[id] = target;
			return target;
		}

		float delta = Vector2.Distance(previous, target);
		float motion = Math.Clamp(delta / MotionPixels, 0f, 1f);
		float tau = StillSeconds + (MovingSeconds - StillSeconds) * motion;

		float dt = ImGui.GetIO().DeltaTime;
		float alpha = 1f - MathF.Exp(-dt / MathF.Max(tau, 0.0001f));
		var next = Vector2.Lerp(previous, target, Math.Clamp(alpha, 0f, 1f));
		this.smoothed[id] = next;
		return next;
	}

	private const float MotionPixels = 6f;

	private const float StillSeconds = 0.09f;

	private const float MovingSeconds = 0.004f;

	private static unsafe Vector3 RenderPosition(Dalamud.Game.ClientState.Objects.Types.IGameObject obj) {
		var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
		if (native is null || native->DrawObject is null)
			return obj.Position;

		var p = native->DrawObject->Object.Position;
		return new Vector3(p.X, p.Y, p.Z);
	}

	private const uint Shadow = 0xC0000000;

	private static bool UsesIcons() {
		if (Plugin.Config.MarksLeaderShape != MarkShape.Reticle && Plugin.Config.MarksLeaderShape != MarkShape.Star)
			return true;
		if (Plugin.Config.MarksMemberShape != MarkShape.Reticle && Plugin.Config.MarksMemberShape != MarkShape.Star)
			return true;

		foreach (var over in Plugin.Config.MarksOverrides) {
			if (over.Shape != MarkShape.Reticle && over.Shape != MarkShape.Star)
				return true;
		}

		return false;
	}

	private static (MarkShape Shape, int Glyph, Vector4? Colour) StyleFor(string key, bool leader) {
		foreach (var over in Plugin.Config.MarksOverrides) {
			if (over.Who.Length > 0 && string.Equals(over.Who.Trim(), key, StringComparison.OrdinalIgnoreCase))
				return (over.Shape, over.Glyph, over.Colour);
		}

		return leader
			? (Plugin.Config.MarksLeaderShape, Plugin.Config.MarksLeaderGlyph, null)
			: (Plugin.Config.MarksMemberShape, Plugin.Config.MarksMemberGlyph, null);
	}

	public void DrawTab() {
		Section("Where");
		Toggle("PvP", "Frontlines, Crystalline Conflict, Rival Wings",
			Plugin.Config.MarksInPvp, v => Plugin.Config.MarksInPvp = v);
		Toggle("Field operations", "Eureka, Bozja, Delubrum, Occult Crescent, Diadem, Cosmic Exploration",
			Plugin.Config.MarksInFieldOps, v => Plugin.Config.MarksInFieldOps = v);
		Toggle("Alliance raid", "24 players, and your friends look like the other 21",
			Plugin.Config.MarksInAllianceRaid, v => Plugin.Config.MarksInAllianceRaid = v);
		Toggle("Deep dungeon", "Palace of the Dead, Heaven-on-High, Eureka Orthos -- for finding each other in the maze, not for telling you apart",
			Plugin.Config.MarksInDeepDungeon, v => Plugin.Config.MarksInDeepDungeon = v);

		Section("Group size limit");
		int limit = Plugin.Config.MarksMaxGroupSize;
		ImGui.SetNextItemWidth(140f);
		if (ImGui.InputInt("people##marks_limit", ref limit)) {
			Plugin.Config.MarksMaxGroupSize = Math.Clamp(limit, 1, 24);
			Plugin.Config.Save();
		}

		ImGui.TextDisabled("Come in with more than this and nothing is marked.");

		Section("Shapes");
		ImGui.Text("Party leader");
		ImGui.SameLine(130f);
		var leaderShape = Plugin.Config.MarksLeaderShape;
		int leaderGlyph = Plugin.Config.MarksLeaderGlyph;
		if (MarkShapes.GlyphPicker("leader", ref leaderShape, ref leaderGlyph)) {
			Plugin.Config.MarksLeaderShape = leaderShape;
			Plugin.Config.MarksLeaderGlyph = leaderGlyph;
			Plugin.Config.Save();
		}

		ImGui.Text("Everyone else");
		ImGui.SameLine(130f);
		var memberShape = Plugin.Config.MarksMemberShape;
		int memberGlyph = Plugin.Config.MarksMemberGlyph;
		if (MarkShapes.GlyphPicker("member", ref memberShape, ref memberGlyph)) {
			Plugin.Config.MarksMemberShape = memberShape;
			Plugin.Config.MarksMemberGlyph = memberGlyph;
			Plugin.Config.Save();
		}

		ImGui.Spacing();
		ImGui.TextWrapped(

			"Give a specific person their own shape. Type their name and home world exactly as the "
			+ "nameplate shows them, e.g. First Last@Server.");
		ImGui.TextDisabled(
			"This only changes how someone already being marked looks. It never marks anyone.");
		ImGui.TextDisabled(
			"Tick the box for a colour of their own. Avoid red, blue and yellow in PvP - they are the "
			+ "Frontline team colours.");
		ImGui.Spacing();

		for (int i = 0; i < Plugin.Config.MarksOverrides.Count; i++) {
			var entry = Plugin.Config.MarksOverrides[i];

			ImGui.SetNextItemWidth(190f);
			string who = entry.Who;
			if (ImGui.InputText($"##marksWho{i}", ref who, 64)) {
				entry.Who = who;
				Plugin.Config.Save();
			}

			ImGui.SameLine();
			var shape = entry.Shape;
			int entryGlyph = entry.Glyph;
			if (MarkShapes.GlyphPicker($"over{i}", ref shape, ref entryGlyph, 110f)) {
				entry.Shape = shape;
				entry.Glyph = entryGlyph;
				Plugin.Config.Save();
			}

			ImGui.SameLine();
			bool ownColour = entry.Colour is not null;
			if (ImGui.Checkbox($"##marksOwnCol{i}", ref ownColour)) {
				entry.Colour = ownColour ? Plugin.Config.MarksColour : null;
				Plugin.Config.Save();
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Give this person their own colour, instead of the shared one.");

			if (entry.Colour is { } own) {
				ImGui.SameLine();
				var col = own;
				if (ImGui.ColorEdit4($"##marksCol{i}", ref col, ImGuiColorEditFlags.NoInputs)) {
					entry.Colour = col;
					Plugin.Config.Save();
				}
			}

			ImGui.SameLine();
			if (ImGui.Button($"Remove##marksDel{i}")) {
				Plugin.Config.MarksOverrides.RemoveAt(i);
				Plugin.Config.Save();
				break;
			}
		}

		if (ImGui.Button("+ Add a person##marks")) {
			Plugin.Config.MarksOverrides.Add(new MarkOverride());
			Plugin.Config.Save();
		}

		Section("Appearance");
		var colour = Plugin.Config.MarksColour;
		if (ImGui.ColorEdit4("Marker colour##marks", ref colour, ImGuiColorEditFlags.NoInputs)) {
			Plugin.Config.MarksColour = colour;
			Plugin.Config.Save();
		}
		bool previewToggle = Plugin.Config.MarksPreview;
		if (ImGui.Checkbox("Show one on me (for positioning)##marks", ref previewToggle)) {
			Plugin.Config.MarksPreview = previewToggle;
			Plugin.Config.Save();
		}

		bool names = Plugin.Config.MarksShowTag;
		if (ImGui.Checkbox("Show a P2-style tag under the marker", ref names)) {
			Plugin.Config.MarksShowTag = names;
			Plugin.Config.Save();
		}

		float scale = Plugin.Config.MarksScale;
		ImGui.SetNextItemWidth(160f);
		if (ImGui.SliderFloat("Size##marks", ref scale, 0.4f, 2.5f, "%.2fx")) {
			Plugin.Config.MarksScale = scale;
			Plugin.Config.Save();
		}
		ImGui.TextWrapped(
			"On top of an automatic scale from your resolution, so this is taste rather than a fix for "
			+ "screen size — 1080p and 1440p already get the same apparent size at 1.00x.");

		ImGui.Spacing();
		float height = Plugin.Config.MarksHeight;
		ImGui.SetNextItemWidth(160f);
		if (ImGui.SliderFloat("Anchor height##marks", ref height, 0.0f, 4.0f, "%.2f yalms")) {
			Plugin.Config.MarksHeight = height;
			Plugin.Config.Save();
		}
		ImGui.TextWrapped(
			"Where on the character the marker anchors, in yalms -- roughly head height. Lalafell and "
			+ "Roegadyn differ by enough that one number will not suit both.");

		ImGui.Spacing();
		float lift = Plugin.Config.MarksLift;
		ImGui.SetNextItemWidth(160f);
		if (ImGui.SliderFloat("Clearance##marks", ref lift, 0f, 120f, "%.0f px")) {
			Plugin.Config.MarksLift = lift;
			Plugin.Config.Save();
		}
		ImGui.TextWrapped(
			"How far above the anchor it floats, in screen pixels. This is the one that keeps it off "
			+ "the nameplate at range: a purely world-space offset shrinks with distance, so it "
			+ "collapses onto someone exactly when they are far away and small.");

	}

	private static void Toggle(string label, string hint, bool value, Action<bool> set) {
		bool v = value;
		if (ImGui.Checkbox(label, ref v)) {
			set(v);
			Plugin.Config.Save();
		}
		ImGui.Indent();
		ImGui.TextDisabled(hint);
		ImGui.Unindent();
	}

	private static void Section(string title) {
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextDisabled(title);
		ImGui.Spacing();
	}

	public void Dispose() {
		Plugin.PluginInterface.UiBuilder.Draw -= this.DrawOverlay;
		this.tracker.Dispose();
		this.font.Dispose();
		this.iconFont.Dispose();
	}
}
