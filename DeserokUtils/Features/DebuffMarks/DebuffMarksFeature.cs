using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;

using DeserokUtils.Features.EphemeralMarks;

namespace DeserokUtils.Features.DebuffMarks;

/// <summary>
/// Put an icon over the head of anyone carrying a status you care about.
///
/// ## Why it exists
///
/// Bunny plays Samurai in PvP, where the limit break <b>Zantetsuken</b> reads: *"If target is
/// afflicted with Kuzushi, deals damage equal to 100% of their maximum HP."* So the question she needs
/// answered mid-fight is "which of these people is marked for execution" -- and PvP moves faster than
/// reading debuff icons off a target frame.
///
/// deserok, setting the boundary before anyone asked: *"I can maybe make something that puts an icon
/// over their heads for you, but I will never make something that presses the LB for you."* ⭐ That is
/// the same read-only line that made WeakAuras safe to share with strangers, and it is worth holding
/// deliberately rather than by accident.
///
/// ## ⚠⚠ SourceId is not optional, because Kuzushi is source-attributed
///
/// The status text says *"Damage taken from **the samurai who applied this effect** is increased."*
/// So "does this target have status 3202" is the WRONG question -- another Samurai's Kuzushi will not
/// make your limit break execute. Marking on presence alone would be confidently wrong at the exact
/// moment somebody spends an LB on it, which is worse than showing nothing.
///
/// ## ⚠ Scanning, throttled rather than per-frame
///
/// A Frontline holds ~72 players and each carries up to thirty statuses. Checking all of that every
/// frame is the per-frame audit this project keeps re-learning, somewhere it would actually hurt. The
/// scan runs at 10 Hz and the draw path reads the result -- the same shape as EphemeralMarks' 1 Hz
/// resolve, but faster, because a four-second debuff cannot wait a second.
///
/// ⭐ Reuses <see cref="MarkShapes"/> and <see cref="MarkFont"/> unchanged: the glyph rendering, the
/// halo, and the rasterise-at-drawn-size fix are Frontline-tested already.
///
/// ⚠ The position and smoothing loop IS duplicated from EphemeralMarks, deliberately and with a
/// trigger: extract a shared overlay when a THIRD feature wants one. Refactoring a feature two people
/// are actively testing, to save forty lines, is the wrong trade today.
/// </summary>
internal sealed class DebuffMarksFeature: IDisposable {
	public string TabTitle => "Debuffs";

	private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(100);

	private readonly MarkFont font = new(MarkFace.Icons);
	private readonly List<(ulong Id, int Entry)> hits = new();
	private readonly Dictionary<ulong, Vector2> smoothed = new();
	private readonly HashSet<ulong> seenThisFrame = new();

	/// <summary>
	/// Name -&gt; every status id that carries it, resolved once per name.
	///
	/// ⚠⚠ A SET, not one id. Three separate statuses are called "Reprisal" and two are called
	/// "Kuzushi"-adjacent; picking one silently watches the wrong number and the feature looks broken
	/// rather than misconfigured. ⭐ Watching all of them is correct anyway: they are the same effect
	/// with different provenance, and the source filter is what narrows it to yours.
	/// </summary>
	private readonly Dictionary<string, uint[]> resolved = new(StringComparer.OrdinalIgnoreCase);

	private DateTime lastScan = DateTime.MinValue;
	private DateTime lastFrame = DateTime.UtcNow;

	public DebuffMarksFeature() {
		Plugin.Commands.AddHandler("/dsudebuffs", new CommandInfo(this.OnCommand) {
			HelpMessage = "/dsudebuffs -- mark anyone carrying a status you are watching. 'on'/'off' to toggle.",
		});
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

	/// <summary>
	/// ⚠ Leaves immediately when nothing is watched. A feature switched off or left unconfigured must
	/// cost one bool test, not a walk of the object table.
	/// </summary>
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

					// ⚠⚠ The source check. A status applied by somebody else can be the wrong answer
					// even though its name matches -- see the class notes.
					if (watch.MineOnly && status.SourceId != self)
						continue;

					this.hits.Add((chara.GameObjectId, i));
					break;
				}
			}
		}
	}

	private void Draw() {
		// ⚠ The preview has to survive the "nothing is marked" early-out, or it would be invisible in
		// exactly the situation it exists for: standing somewhere quiet, setting the height.
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

			// ⚠⚠ The return value is the whole guard: a position behind the camera still yields a
			// coordinate, a mirrored one, so ignoring it floats markers over empty space behind you.
			if (!Plugin.GameGui.WorldToScreen(head, out Vector2 screen))
				continue;

			screen.Y -= Plugin.Config.DebuffMarksLift * scale;
			screen = this.Smooth(id, screen, dt);

			MarkShapes.Draw(draw, watch.Shape, watch.Glyph, face, iconPx, screen,
				ImGui.ColorConvertFloat4ToU32(watch.Colour), scale);
		}

		// ⭐ The preview, drawn last so it sits over everything else. Uses the first watched entry so it
		// previews the shape you are actually configuring, and falls back to a plain skull when the list
		// is empty -- which is the state somebody setting this up for the first time is in.
		if (preview && Plugin.Objects.LocalPlayer is { } me) {
			var watch = Plugin.Config.DebuffMarks.Count > 0 ? Plugin.Config.DebuffMarks[0] : new DebuffMark();
			var head = me.Position with { Y = me.Position.Y + Plugin.Config.DebuffMarksHeight };
			if (Plugin.GameGui.WorldToScreen(head, out Vector2 self)) {
				self.Y -= Plugin.Config.DebuffMarksLift * scale;
				MarkShapes.Draw(draw, watch.Shape, watch.Glyph, face, iconPx, self,
					ImGui.ColorConvertFloat4ToU32(watch.Colour), scale);
			}
		}

		// ⚠ Forget anyone no longer marked, so the smoothing map cannot grow across a session.
		if (this.smoothed.Count > this.seenThisFrame.Count) {
			foreach (ulong stale in new List<ulong>(this.smoothed.Keys)) {
				if (!this.seenThisFrame.Contains(stale))
					this.smoothed.Remove(stale);
			}
		}
	}

	/// <summary>
	/// ⚠ The adaptive filter from EphemeralMarks, needed for the same reason: both position sources
	/// oscillate sub-pixel against the game's own nameplate. A FIXED smoother cannot win -- heavy
	/// enough to kill the wobble is heavy enough to lag every camera pan -- so it reads the frame delta
	/// and picks its own strength.
	/// </summary>
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


	// ── the tab ──────────────────────────────────────────────────────────────────────────────

	public void DrawTab() {
		ImGui.TextWrapped(
			"Puts an icon over the head of anyone carrying a status you name. Read-only: it shows you "
			+ "what is happening, it never presses anything.");
		ImGui.Spacing();

		bool on = Plugin.Config.DebuffMarksEnabled;
		if (ImGui.Checkbox("Enabled##debuffs", ref on)) {
			Plugin.Config.DebuffMarksEnabled = on;
			Plugin.Config.Save();
		}

		ImGui.SameLine();
		ImGui.TextDisabled($"({this.hits.Count} marked right now)");

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

		// ⚠ Indexed, and removal BREAKS OUT rather than continuing -- mutating the list mid-enumeration
		// is the ordinary version of this bug, and the ImGui ids stop matching the rows besides.
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

			// ⚠ Says out loud whether the name matched. A typo would otherwise be a feature that simply
			// never fires, and "it does nothing" is the hardest failure to debug from the outside.
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

			// ⭐⭐ The source filter, and it is the setting most likely to be turned off by somebody who
			// does not know why it is there. Kuzushi reads "damage taken from THE SAMURAI WHO APPLIED
			// this effect" -- another Samurai's copy will not make your limit break execute, so marking
			// on it is confidently wrong at the worst possible moment.
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

	/// <summary>
	/// Every status id carrying this name, matched case-insensitively and memoised.
	///
	/// ⚠ Empty when nothing matches, which is what the tab reports as "no such status".
	///
	/// ⚠ The sheet walk happens once per distinct name, not per scan. It is a few thousand string
	/// extractions and belongs nowhere near a 10 Hz loop.
	/// </summary>
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
		Plugin.Commands.RemoveHandler("/dsudebuffs");
		this.font.Dispose();
	}
}
