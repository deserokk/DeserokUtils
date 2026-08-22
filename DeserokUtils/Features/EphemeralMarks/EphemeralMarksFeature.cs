using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

namespace DeserokUtils.Features.EphemeralMarks;

/// <summary>
/// A marker over the heads of the people you queued in with. Yours only -- nobody else sees it.
///
/// Requested by Bunny 2026-08-21: the default party marks are shared and contested, *"it shows for
/// everyone and everyone tends to fight for use of them"*, and in Frontlines she and Q lose deserok
/// in the chaos.
///
/// ## ⚠⚠ Why the game does not already solve this
///
/// Nameplate colour separates your PARTY from the ALLIANCE -- it does not separate your friends from
/// the five randoms assigned to your own party. deserok, correcting an earlier claim of mine:
/// *"in frontlines, your premade is in your party too, but you're generally lost in the other 6 you
/// get assigned with, so the blue name becomes indistinguishable."* Frontlines and alliance raids
/// fail identically.
///
/// ⭐ So the real condition is **how many identical-looking allies there are, and whether your
/// friends are a small subset of them**:
/// <code>
///  4  dungeon        trivial: with one friend you are half the group
///  8  normal raid    easy: eight names
/// 24  alliance raid  hard
/// 24+ Frontlines     hard, and moving
/// ??  field ops      dozens, unbounded
/// </code>
///
/// ⭐⭐ Alliance raid is included, and **Cover is the argument that carries it**: the ability can kill
/// the caster, so deserok is selective, and which ally is under the cursor changes whether the risk
/// is worth taking. That is decision-relevant, not convenience.
///
/// ## What it deliberately is not
///
/// ⚠ Not a command to mark someone -- that means marking during the chaos, which is exactly when you
/// cannot. ⚠ Not "all friends" -- floods a 72-player Frontline with people who do not matter right
/// now. ⚠ Not a whitelist of people you care about -- bigger, and it means keeping a list of specific
/// people, which is the direction to stay away from. "Who I came with" is self-scoping and forgets
/// itself.
/// </summary>
internal sealed class EphemeralMarksFeature: IDisposable {
	public string TabTitle => "Marks";

	private readonly MarkTracker tracker = new();

	public EphemeralMarksFeature() {
		Plugin.Commands.AddHandler("/dsumarks", new CommandInfo(this.OnCommand) {
			HelpMessage = "/dsumarks -- mark the people you came into large content with. 'on'/'off' to toggle.",
		});
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

	// ── the overlay ──────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// ⭐ Drawn on the BACKGROUND draw list rather than in a window -- no ImGui window to position,
	/// resize, click through or accidentally focus. It is an overlay, so it should not be a thing you
	/// can interact with.
	/// </summary>
	private void DrawOverlay() {
		if (!Plugin.Config.MarksEnabled || this.tracker.Idle is not null)
			return;

		var draw = ImGui.GetBackgroundDrawList();
		uint colour = ImGui.ColorConvertFloat4ToU32(Plugin.Config.MarksColour);

		// ⭐⭐ SCALED TO THE VIEWPORT, then by the user's slider. A marker sized in raw pixels occupies
		// a bigger FRACTION of a 1080p screen than a 1440p one, so the three people who will use this
		// -- 1440p large, 1440p small, 1080p -- would each need a different number for the same
		// apparent size. Dividing by the viewport height makes the default already right on all three,
		// and leaves the slider for taste rather than for fixing resolution.
		//
		// ⚠ Viewing distance is unknowable, which is exactly why the slider exists on top rather than
		// this trying to be clever about physical screen size.
		float scale = ImGui.GetIO().DisplaySize.Y / 1440f * Plugin.Config.MarksScale;

		foreach (var (obj, isLeader) in this.tracker.Marked()) {
			// ⚠ Head height in YALMS, not pixels, so it tracks distance the way the nameplate does.
			// A fixed pixel offset would clear the name up close and collide with it far away.
			// Configurable because races differ by a lot and this number was picked, not measured.
			var head = obj.Position with { Y = obj.Position.Y + Plugin.Config.MarksHeight };

			// ⚠⚠ THE RETURN VALUE IS THE WHOLE GUARD, and ignoring it is the classic bug: a position
			// behind the camera still yields a screen coordinate -- a mirrored one -- so markers
			// appear floating over empty space behind you.
			if (!Plugin.GameGui.WorldToScreen(head, out Vector2 screen))
				continue;

			if (isLeader)
				DrawStar(draw, screen, colour, scale);
			else
				DrawReticle(draw, screen, colour, scale);

			if (Plugin.Config.MarksShowNames) {
				string name = obj.Name.TextValue;
				var size = ImGui.CalcTextSize(name);
				var at = new Vector2(screen.X - size.X / 2f, screen.Y + 4f * scale);
				// ⚠ Shadowed. A light name over snow, sand or a spell effect is otherwise unreadable,
				// which is most of a Frontline.
				draw.AddText(at + new Vector2(1f, 1f), Shadow, name);
				draw.AddText(at, colour, name);
			}
		}
	}

	private const uint Shadow = 0xC0000000;

	/// <summary>
	/// An outlined diamond with a filled point and a small detached square above it.
	///
	/// ⭐ Deliberately in the visual language of the game's own target reticle -- a marker that looks
	/// like it belongs reads faster than one that looks like a debug overlay. **Outlines rather than a
	/// solid shape**, which is what keeps it legible over a bright or busy background instead of
	/// becoming a blob.
	///
	/// ⚠⚠ But not a colour a target reticle uses, and that is the point. A friend marker mistakable
	/// for "this is my current target" would be a worse misread than no marker at all. Same family,
	/// different signal.
	///
	/// ⚠ Every stroke is drawn twice, dark then coloured. Thin light lines vanish against snow.
	/// </summary>
	private static void DrawReticle(ImDrawListPtr draw, Vector2 at, uint colour, float s) {
		float halfWidth = 10f * s, tall = 32f * s, shoulder = 12.5f * s, square = 5f * s, gap = 8f * s;
		// ⚠ Stroke weights scale too, but with a floor -- a sub-pixel line does not anti-alias into
		// something faint, it flickers as the marker moves.
		float heavy = MathF.Max(1.5f, 3f * s), light = MathF.Max(1f, 1.6f * s);

		var left = new Vector2(at.X - halfWidth, at.Y - shoulder);
		var right = new Vector2(at.X + halfWidth, at.Y - shoulder);
		var top = new Vector2(at.X, at.Y - tall);

		// Lower half solid so the tip reads at a glance; upper half open so it does not become a lump
		// at distance.
		draw.AddTriangleFilled(left, right, at, Shadow);
		draw.AddTriangleFilled(
			left + new Vector2(1.5f * s, -0.5f * s), right + new Vector2(-1.5f * s, -0.5f * s),
			at + new Vector2(0f, -1.5f * s), colour);

		draw.AddQuad(top, right, at, left, Shadow, heavy);
		draw.AddQuad(top, right, at, left, colour, light);

		var sqA = new Vector2(at.X - square, at.Y - tall - gap - square * 2f);
		var sqB = new Vector2(at.X + square, at.Y - tall - gap);
		draw.AddRect(sqA, sqB, Shadow, 0f, ImDrawFlags.None, heavy);
		draw.AddRect(sqA, sqB, colour, 0f, ImDrawFlags.None, light);
	}

	/// <summary>
	/// A five-pointed star, for the party leader.
	///
	/// ⭐ deserok's idea, and it is asymmetric in exactly the useful direction: from the leader's
	/// client everyone else is a diamond, while from Bunny's and Q's the leader is a star. They follow
	/// him, so "where is he" is the question they are actually asking, and a distinct SHAPE answers it
	/// faster than a distinct colour -- shape survives peripheral vision and colour blindness, colour
	/// does not.
	///
	/// ⚠ Bigger than the diamond on purpose. It is the one you are looking for.
	/// </summary>
	private static void DrawStar(ImDrawListPtr draw, Vector2 at, uint colour, float s) {
		float outer = 19f * s, inner = 7.9f * s, lift = 18f * s;
		float heavy = MathF.Max(1.6f, 3.5f * s), light = MathF.Max(1f, 1.8f * s);
		var centre = new Vector2(at.X, at.Y - lift);

		Span<Vector2> points = stackalloc Vector2[10];
		for (int i = 0; i < 10; i++) {
			// Start at the top and alternate outer/inner radius. -PI/2 puts a point upward.
			float angle = -MathF.PI / 2f + i * MathF.PI / 5f;
			float radius = (i % 2 == 0) ? outer : inner;
			points[i] = centre + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
		}

		// ⚠ AddPolyline, not ten AddLine calls. Separate lines double-draw every corner -- which on a
		// star is ten lumpy joins -- and emit roughly twice the geometry. One closed polyline mitres
		// the corners properly and is the primitive this is for.
		draw.AddPolyline(ref points[0], 10, Shadow, ImDrawFlags.Closed, heavy);
		draw.AddPolyline(ref points[0], 10, colour, ImDrawFlags.Closed, light);
	}

	// ── the tab ──────────────────────────────────────────────────────────────────────────────

	public void DrawTab() {
		ImGui.TextWrapped(
			"Marks the people you came into large content with, so you can find them in a crowd. Only "
			+ "you see it -- unlike the game's party marks, which everyone shares and competes for.");
		ImGui.Spacing();

		bool enabled = Plugin.Config.MarksEnabled;
		if (ImGui.Checkbox("Show marks", ref enabled)) {
			Plugin.Config.MarksEnabled = enabled;
			Plugin.Config.Save();
		}

		Section("Right now");
		if (this.tracker.Idle is not null) {
			ImGui.TextDisabled($"idle -- {this.tracker.Idle}");
		}
		else {
			ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f),
				$"active in {this.tracker.Group}: marking {this.tracker.ResolvedCount} of {this.tracker.Snapshot.Count}");
		}
		if (this.tracker.Snapshot.Count > 0) {
			ImGui.Spacing();
			ImGui.TextDisabled("queued with:");
			foreach (var (name, _, leader) in this.tracker.Snapshot)
				ImGui.BulletText(leader ? $"{name}  (leader -- star)" : name);
		}

		Section("Where");
		ImGui.TextWrapped(
			"Three groups rather than a checkbox per content type. The game's own zone classification "
			+ "does the sorting underneath, so new field operations look after themselves.");
		ImGui.Spacing();
		Toggle("PvP", "Frontlines, Crystalline Conflict, Rival Wings",
			Plugin.Config.MarksInPvp, v => Plugin.Config.MarksInPvp = v);
		Toggle("Field operations", "Eureka, Bozja, Delubrum, Occult Crescent, Diadem, Cosmic Exploration",
			Plugin.Config.MarksInFieldOps, v => Plugin.Config.MarksInFieldOps = v);
		Toggle("Alliance raid", "24 players, and your friends look like the other 21",
			Plugin.Config.MarksInAllianceRaid, v => Plugin.Config.MarksInAllianceRaid = v);

		ImGui.Spacing();
		ImGui.TextWrapped(
			"Dungeons and normal raids are deliberately absent: four or eight names is easy to read, "
			+ "and with one friend you are already half the group.");

		ImGui.Spacing();
		bool everywhere = Plugin.Config.MarksEverywhere;
		if (ImGui.Checkbox("Show in ALL content (testing)", ref everywhere)) {
			Plugin.Config.MarksEverywhere = everywhere;
			Plugin.Config.Save();
		}
		ImGui.Indent();
		ImGui.TextDisabled("ignores the three toggles above, for tuning the marker somewhere convenient");
		ImGui.Unindent();

		Section("Group size limit");
		int limit = Plugin.Config.MarksMaxGroupSize;
		ImGui.SetNextItemWidth(140f);
		if (ImGui.InputInt("people##marks_limit", ref limit)) {
			Plugin.Config.MarksMaxGroupSize = Math.Clamp(limit, 1, 24);
			Plugin.Config.Save();
		}
		ImGui.TextWrapped(
			"If you came in with more than this, nothing is marked -- because marking everyone is the "
			+ "same as marking nobody. Frontlines caps premades at 4 and Crystalline Conflict at 2, so "
			+ "5 clears every real case while excluding content you enter as a whole group.");

		Section("Appearance");
		var colour = Plugin.Config.MarksColour;
		if (ImGui.ColorEdit4("Marker colour##marks", ref colour, ImGuiColorEditFlags.NoInputs)) {
			Plugin.Config.MarksColour = colour;
			Plugin.Config.Save();
		}
		bool names = Plugin.Config.MarksShowNames;
		if (ImGui.Checkbox("Show the name under the marker", ref names)) {
			Plugin.Config.MarksShowNames = names;
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
		if (ImGui.SliderFloat("Height above head##marks", ref height, 1.0f, 4.0f, "%.2f yalms")) {
			Plugin.Config.MarksHeight = height;
			Plugin.Config.Save();
		}
		ImGui.TextWrapped(
			"In yalms rather than pixels, so it tracks distance the way the nameplate does. Lalafell "
			+ "and Roegadyn differ by enough that one number will not suit both -- tune it for whoever "
			+ "you actually play with.");

		Section("How it picks people");
		ImGui.TextWrapped(
			"It remembers your party continuously while you are outside tracked content, and freezes "
			+ "that the moment you step inside. So it is whoever you came with, even for field "
			+ "operations that you walk into rather than queue for.");
		ImGui.Spacing();
		ImGui.TextWrapped(
			"⚠ A party formed after you are already inside will not be marked. Matching is by name and "
			+ "home world, nothing is stored, and the list is discarded when you leave.");
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

	/// <summary>ImGui.SeparatorText does not exist in this binding version; this is the stand-in.</summary>
	private static void Section(string title) {
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextDisabled(title);
		ImGui.Spacing();
	}

	public void Dispose() {
		Plugin.PluginInterface.UiBuilder.Draw -= this.DrawOverlay;
		Plugin.Commands.RemoveHandler("/dsumarks");
		this.tracker.Dispose();
	}
}
