using System;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface;

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
/// ## ⚠⚠ Deep dungeons are in for a SECOND reason, and the rule above does not cover them
///
/// A deep dungeon party is four people who are all your friends, so by the table above it should be
/// excluded outright -- there is nobody to be told apart from. deserok, 2026-08-22, after a
/// Heaven-on-High run: *"we lose each other constantly."*
///
/// ⭐ So the overlay does two jobs and only one of them was designed: **identification** in a crowd of
/// lookalikes, and **wayfinding** in a dark randomised maze. The second needs no crowd at all. Worth
/// stating outright, because the exclusion rules below are written for the first job and will keep
/// reading as though they are the whole story.
///
/// ## What it deliberately is not
///
/// ⚠ Not a command to mark someone -- that means marking during the chaos, which is exactly when you
/// cannot. ⚠ Not "all friends" -- floods a 72-player Frontline with people who do not matter right
/// now. ⚠ Not a whitelist of people you care about -- bigger, and it means keeping a list of specific
/// people, which is the direction to stay away from. "Who I came with" is self-scoping and forgets
/// itself.
///
/// ⚠⚠ THE SHAPE OVERRIDES ARE NOT AN EXCEPTION TO THAT, and the distinction is the whole reason they
/// were acceptable. They are consulted only for people the snapshot ALREADY produced, and can change
/// nothing except which glyph is drawn. The moment one influences who gets marked, it has become the
/// whitelist above. See <see cref="Configuration.MarksOverrides"/>.
/// </summary>
internal sealed unsafe class EphemeralMarksFeature: IDisposable {
	public string TabTitle => "Marks";

	private readonly MarkTracker tracker = new();

	/// <summary>
	/// ⚠ Constructed here but it builds nothing until a tag is actually drawn -- the atlas is created
	/// on first use, and the tag is off by default.
	/// </summary>
	private readonly MarkFont font = new(MarkFace.Axis);

	/// <summary>
	/// ⚠ A second atlas, and it earns its keep: the glyphs have to be rasterised at the size they are
	/// drawn for exactly the reason the tag did. Built lazily like the other one, so a config using
	/// only the reticle and star never pays for it.
	/// </summary>
	private readonly MarkFont iconFont = new(MarkFace.Icons);

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
		uint sharedColour = ImGui.ColorConvertFloat4ToU32(Plugin.Config.MarksColour);

		// ⭐⭐ SCALED TO THE VIEWPORT, then by the user's slider. A marker sized in raw pixels occupies
		// a bigger FRACTION of a 1080p screen than a 1440p one, so the three people who will use this
		// -- 1440p large, 1440p small, 1080p -- would each need a different number for the same
		// apparent size. Dividing by the viewport height makes the default already right on all three,
		// and leaves the slider for taste rather than for fixing resolution.
		//
		// ⚠ Viewing distance is unknowable, which is exactly why the slider exists on top rather than
		// this trying to be clever about physical screen size.
		float scale = ImGui.GetIO().DisplaySize.Y / 1440f * Plugin.Config.MarksScale;

		// ⚠ Keeps the smoothing dictionary bounded: anyone no longer marked is forgotten this frame.
		this.seenThisFrame.Clear();

		// ⭐ Locked ONCE per frame rather than once per marker. Four markers is four lock/unlock pairs
		// for an answer that cannot change within a frame.
		//
		// ⚠ Only touched when tags are on, so the font atlas is never built for anyone who leaves the
		// setting at its default.
		bool wantTags = Plugin.Config.MarksShowTag;
		float fontPx = ImGui.GetFontSize() * scale;
		if (wantTags)
			fontPx = this.font.Prepare(fontPx);
		using var locked = wantTags ? this.font.TryLock() : null;

		// ⭐ Prepared only if some glyph is actually in use. Checking costs a walk of at most a handful
		// of config entries once per frame, and it keeps the icon atlas from existing at all for
		// anyone happy with the two vector shapes.
		bool wantIcons = UsesIcons();
		float iconPx = 30f * scale;
		if (wantIcons)
			iconPx = this.iconFont.Prepare(iconPx);
		using var iconLock = wantIcons ? this.iconFont.TryLock() : null;
		ImFontPtr? iconFace = iconLock is not null ? iconLock.ImFont : null;

		foreach (var (obj, isLeader, tag, key) in this.tracker.Marked()) {
			this.seenThisFrame.Add(obj.GameObjectId);
			// ⚠ The WORLD part of the anchor -- roughly head height, so the marker sits on the body rather
			// than on the ground. The screen-space lift below is what keeps it clear at distance.
			var basePos = RenderPosition(obj);
			var head = basePos with { Y = basePos.Y + Plugin.Config.MarksHeight };

			// ⚠⚠ THE RETURN VALUE IS THE WHOLE GUARD, and ignoring it is the classic bug: a position
			// behind the camera still yields a screen coordinate -- a mirrored one -- so markers
			// appear floating over empty space behind you.
			if (!Plugin.GameGui.WorldToScreen(head, out Vector2 screen))
				continue;

			// ⚠⚠ A PIXEL LIFT ON TOP OF THE WORLD OFFSET, and the reason is the opposite of what I first
			// argued. A world-space offset projects to FEWER pixels as distance grows, so the marker
			// collapses onto the character exactly when they are far away and small -- overlapping the
			// nameplate, which is the case it most needs to stay clear of. A pixel offset does not
			// shrink. The world part anchors it to the body; the pixel part guarantees the gap.
			screen.Y -= Plugin.Config.MarksLift * scale;
			screen = this.Smooth(obj.GameObjectId, screen);

			// ⭐ An override beats the leader star, deliberately. Two reasons, deserok 2026-08-23:
			// it is simpler, and *"anyone who cares enough to have a shape would also care enough to
			// know who the leader is"*. The alternative -- leader wins -- means somebody's chosen glyph
			// silently disappears whenever that person happens to be leading, which is the worst kind
			// of intermittent.
			//
			// ⚠⚠ This is the ONLY thing an override touches. It cannot make somebody marked; `key`
			// only exists for people already in the snapshot.
			// ⚠ Shadowing `colour` on purpose: everything below this point -- the glyph AND the tag --
			// must use the same one, or a customised person gets a pink tag under a green marker.
			var (shape, glyph, own) = StyleFor(key, isLeader);
			uint colour = own is null ? sharedColour : ImGui.ColorConvertFloat4ToU32(own.Value);

			MarkShapes.Draw(draw, shape, glyph, iconFace, iconPx, screen, colour, scale);

			if (wantTags && tag.Length > 0) {
				// ⚠⚠ Measured with the SAME font and size it is drawn at. Measuring with the default
				// font and scaling the result would centre the tag against metrics belonging to a
				// different typeface -- invisible at one glyph, visibly off at two.
				//
				// ⚠ Via the font stack, because this ImGui binding's ImFontPtr has no CalcTextSizeA.
				// Pushed and popped per marker rather than around the loop: two glyphs is nothing to
				// measure, and a push that cannot outlive its own if-block cannot be leaked.
				Vector2 glyphs;
				if (locked is not null) {
					ImGui.PushFont(locked.ImFont);
					glyphs = ImGui.CalcTextSize(tag);
					ImGui.PopFont();
				}
				else {
					glyphs = ImGui.CalcTextSize(tag) * (fontPx / ImGui.GetFontSize());
				}

				// ⚠ Rounded to whole pixels. A glyph quad landing on a half pixel is resampled even
				// when the atlas size is exactly right, which would undo most of the point of building
				// the font. ⭐ Safe here in a way it was NOT for the marker itself: rounding the shape
				// position made the jitter dramatically worse, but that was the smoothing input --
				// this rounds only the final text placement, and the smoothed value it comes from is
				// already stable when standing still.
				var at = new Vector2(
					MathF.Round(screen.X - glyphs.X / 2f),
					MathF.Round(screen.Y + 3f * scale));

				// ⚠ Shadowed. Two light glyphs over snow, sand or a spell effect are otherwise
				// unreadable, which is most of a Frontline.
				var face = locked is not null ? locked.ImFont : ImGui.GetFont();
				draw.AddText(face, fontPx, at + new Vector2(1f, 1f), Shadow, tag);
				draw.AddText(face, fontPx, at, colour, tag);
			}
		}

		// ⚠ Forget anyone no longer marked, so the smoothing map cannot grow across a session.
		if (this.smoothed.Count > this.seenThisFrame.Count) {
			foreach (ulong stale in this.smoothed.Keys.Where(k => !this.seenThisFrame.Contains(k)).ToList())
				this.smoothed.Remove(stale);
		}
	}

	private readonly System.Collections.Generic.HashSet<ulong> seenThisFrame = new();

	private readonly System.Collections.Generic.Dictionary<ulong, Vector2> smoothed = new();

	/// <summary>
	/// A low-pass filter on the screen position.
	///
	/// ## ⚠⚠ This treats the symptom, and that is a deliberate concession
	///
	/// Both position sources were tried -- the logical `IGameObject.Position` and the render transform
	/// on the draw object -- and both jitter while the game's own nameplate on the SAME character is
	/// steady. Rounding to whole pixels then made it dramatically worse, which is the useful clue:
	/// rounding cannot create motion, only convert sub-pixel motion into whole-pixel snapping. So the
	/// position genuinely oscillates by under a pixel and anti-aliasing was hiding it.
	///
	/// ⭐ The most likely cause is one this code cannot reach: an overlay drawn at present time reads
	/// a transform at a different point in the frame than the game used to rasterise the character.
	/// The nameplate is steady because the game draws it with the matching transform. Nothing
	/// available from outside fixes that, so the honest options were "leave it jittering" or "filter
	/// it", and filtering wins.
	///
	/// ⚠ FRAMERATE-INDEPENDENT, via the exponential form rather than a fixed lerp factor. A plain
	/// `lerp(a, b, 0.3f)` smooths three times harder at 30fps than at 120 -- so it would feel
	/// different on each of the three machines this is for, which is the bug one layer up from the one
	/// being fixed.
	///
	/// ⚠ Entries are dropped when a target stops being marked, so the dictionary cannot grow.
	/// </summary>
	private Vector2 Smooth(ulong id, Vector2 target) {
		if (!this.smoothed.TryGetValue(id, out var previous)) {
			this.smoothed[id] = target;
			return target;
		}

		// ⚠ A jump means they teleported, respawned, or came back into view -- snap rather than glide
		// a marker across the screen. 250px at any sane framerate is not real movement.
		if (Vector2.DistanceSquared(previous, target) > 250f * 250f) {
			this.smoothed[id] = target;
			return target;
		}

		// ⭐⭐ ADAPTIVE, because a single time constant cannot win. A filter heavy enough to kill the
		// jitter also lags every camera pan -- deserok: "it does tend to lag behind camera movements
		// but it's preferable to jitter." It does not have to be a trade: the two signals differ by an
		// order of magnitude in size. Jitter is sub-pixel frame to frame; a camera pan moves a marker
		// tens of pixels. So the filter reads the delta and picks its own strength -- heavy while
		// nearly still, almost absent while genuinely moving.
		float delta = Vector2.Distance(previous, target);
		float motion = Math.Clamp(delta / MotionPixels, 0f, 1f);
		float tau = StillSeconds + (MovingSeconds - StillSeconds) * motion;

		float dt = ImGui.GetIO().DeltaTime;
		float alpha = 1f - MathF.Exp(-dt / MathF.Max(tau, 0.0001f));
		var next = Vector2.Lerp(previous, target, Math.Clamp(alpha, 0f, 1f));
		this.smoothed[id] = next;
		return next;
	}

	/// <summary>Delta at which the motion is treated as entirely real and smoothing stops.</summary>
	private const float MotionPixels = 6f;

	/// <summary>⚠ Heavy -- for when the target is nearly still and every pixel of movement is noise.</summary>
	private const float StillSeconds = 0.09f;

	/// <summary>⚠ Nearly nothing, so a camera pan is followed the frame it happens.</summary>
	private const float MovingSeconds = 0.004f;

	/// <summary>
	/// Where the character is actually DRAWN this frame, not where the game logically thinks it is.
	///
	/// ⚠⚠ THIS IS WHY THE MARKER JITTERED. `IGameObject.Position` is the logical position, updated on
	/// the game's tick; the model you are looking at is interpolated every frame. Anchoring to the
	/// logical value makes the marker step while the character glides underneath it -- most visible
	/// when they are moving, which in a Frontline is always. Nameplates do not jitter because they
	/// follow the render transform, which is what this reads.
	///
	/// ⚠ Falls back to the logical position when there is no draw object -- someone loading in, or out
	/// of render range. A marker in very slightly the wrong place beats no marker.
	/// </summary>
	private static unsafe Vector3 RenderPosition(Dalamud.Game.ClientState.Objects.Types.IGameObject obj) {
		var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
		if (native is null || native->DrawObject is null)
			return obj.Position;

		var p = native->DrawObject->Object.Position;
		return new Vector3(p.X, p.Y, p.Z);
	}

	private const uint Shadow = 0xC0000000;

	/// <summary>
	/// Which glyph this person gets: an override if they have one, otherwise the leader/member default.
	///
	/// ⚠ A linear scan, in the draw loop, and that is fine here in a way the object-table scan was
	/// NOT. That one was ~600 entries with a string compare each, every frame. This is at most four
	/// markers against a handful of hand-typed overrides -- a couple of dozen compares -- and doing it
	/// per frame rather than at the 1 Hz resolve is what makes an edit in the tab show up instantly.
	///
	/// ⚠⚠ Note what this function CANNOT do: it is only ever called for somebody already in the
	/// snapshot, so an override cannot add anyone. Keep it that way.
	/// </summary>
	/// <summary>
	/// Whether anything currently configured needs the icon atlas.
	///
	/// ⚠ Deliberately does NOT ask which people are marked -- it asks what is configured, so the
	/// answer is stable and the atlas does not get built and dropped as party members come and go.
	/// </summary>
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
			"Grouped rather than a checkbox per content type. The game's own zone classification does "
			+ "the sorting underneath, so new field operations and deep dungeons look after themselves.");
		ImGui.Spacing();
		Toggle("PvP", "Frontlines, Crystalline Conflict, Rival Wings",
			Plugin.Config.MarksInPvp, v => Plugin.Config.MarksInPvp = v);
		Toggle("Field operations", "Eureka, Bozja, Delubrum, Occult Crescent, Diadem, Cosmic Exploration",
			Plugin.Config.MarksInFieldOps, v => Plugin.Config.MarksInFieldOps = v);
		Toggle("Alliance raid", "24 players, and your friends look like the other 21",
			Plugin.Config.MarksInAllianceRaid, v => Plugin.Config.MarksInAllianceRaid = v);
		Toggle("Deep dungeon", "Palace of the Dead, Heaven-on-High, Eureka Orthos -- for finding each other in the maze, not for telling you apart",
			Plugin.Config.MarksInDeepDungeon, v => Plugin.Config.MarksInDeepDungeon = v);

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

		Section("Shapes");
		ImGui.Text("Party leader");
		ImGui.SameLine(130f);
		var leaderShape = Plugin.Config.MarksLeaderShape;
		int leaderGlyph = Plugin.Config.MarksLeaderGlyph;
		if (this.GlyphPicker("leader", ref leaderShape, ref leaderGlyph)) {
			Plugin.Config.MarksLeaderShape = leaderShape;
			Plugin.Config.MarksLeaderGlyph = leaderGlyph;
			Plugin.Config.Save();
		}

		ImGui.Text("Everyone else");
		ImGui.SameLine(130f);
		var memberShape = Plugin.Config.MarksMemberShape;
		int memberGlyph = Plugin.Config.MarksMemberGlyph;
		if (this.GlyphPicker("member", ref memberShape, ref memberGlyph)) {
			Plugin.Config.MarksMemberShape = memberShape;
			Plugin.Config.MarksMemberGlyph = memberGlyph;
			Plugin.Config.Save();
		}

		ImGui.Spacing();
		ImGui.TextWrapped(
			// ⚠ A placeholder, never a real character. An example name in shipped UI reads as an
			// endorsement of that person, or as the author signing their own plugin, and neither is
			// what a format hint is for.
			"Give a specific person their own shape. Type their name and home world exactly as the "
			+ "nameplate shows them, e.g. First Last@Server.");
		ImGui.TextDisabled(
			"This only changes how someone already being marked looks. It never marks anyone.");
		ImGui.TextDisabled(
			"Tick the box for a colour of their own. Avoid red, blue and yellow in PvP - they are the "
			+ "Frontline team colours.");
		ImGui.Spacing();

		// ⚠ Indexed, and REMOVAL BREAKS OUT of the loop rather than continuing. Mutating the list
		// mid-enumeration is the ordinary version of this bug; the ids also stop matching the rows.
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
			if (this.GlyphPicker($"over{i}", ref shape, ref entryGlyph, 110f)) {
				entry.Shape = shape;
				entry.Glyph = entryGlyph;
				Plugin.Config.Save();
			}

			// ⭐ A checkbox for "own colour" plus a swatch, rather than a swatch that is always live.
			// Unticked means this person follows the shared colour, and keeps following it when you
			// change it later -- see MarkOverride.Colour for why that is worth the extra control.
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

	/// ⭐ A shortlist for the empty search, because opening a picker onto 1382 alphabetical entries
	/// starting with "AddressBook" is worse than six good ones. Typing searches everything.
	private static readonly FontAwesomeIcon[] Popular = {
		FontAwesomeIcon.Heart, FontAwesomeIcon.Star, FontAwesomeIcon.Crown, FontAwesomeIcon.Skull,
		FontAwesomeIcon.Paw, FontAwesomeIcon.Cat, FontAwesomeIcon.Ghost, FontAwesomeIcon.Snowflake,
		FontAwesomeIcon.Gem, FontAwesomeIcon.Bolt, FontAwesomeIcon.Fire, FontAwesomeIcon.Moon,
		FontAwesomeIcon.Sun, FontAwesomeIcon.Leaf, FontAwesomeIcon.Fish, FontAwesomeIcon.Dragon,
		FontAwesomeIcon.Crosshairs, FontAwesomeIcon.LocationArrow, FontAwesomeIcon.Bullseye,
		FontAwesomeIcon.Anchor, FontAwesomeIcon.Bell, FontAwesomeIcon.Cookie,
	};

	/// ⚠ Built once. Enum.GetValues over 1382 entries per frame would be the per-frame audit again,
	/// in a tab nobody has open most of the time.
	private static (string Name, int Char)[]? allIcons;

	private static (string Name, int Char)[] AllIcons() =>
		allIcons ??= Enum.GetValues<FontAwesomeIcon>()
			.Select(i => (Name: i.ToString(), Char: (int)i))
			.Where(i => i.Char > 0)
			.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();

	private string iconFilter = string.Empty;

	/// <summary>
	/// Shape, plus a glyph picker when the shape is Icon.
	///
	/// ⚠ The search is capped at 80 results and SAYS SO when it truncates. A silent cap reads as
	/// "that icon does not exist", which would send somebody looking for a bug that is not there.
	/// </summary>
	private bool GlyphPicker(string id, ref MarkShape shape, ref int glyph, float width = 150f) {
		bool changed = false;

		ImGui.SetNextItemWidth(width);
		if (ImGui.BeginCombo($"##shape{id}", MarkShapes.Label(shape))) {
			foreach (MarkShape option in new[] { MarkShape.Reticle, MarkShape.Star, MarkShape.Icon }) {
				if (ImGui.Selectable(MarkShapes.Label(option), option == shape)) {
					shape = option;
					changed = true;
				}
			}

			ImGui.EndCombo();
		}

		if (shape != MarkShape.Icon)
			return changed;

		ImGui.SameLine();
		// ⚠ Copied out of the ref parameter: a ref cannot be captured by the lambda.
		int chosen = glyph;
		string current = AllIcons().FirstOrDefault(i => i.Char == chosen).Name ?? "pick";
		if (ImGui.Button($"{current}##pick{id}"))
			ImGui.OpenPopup($"glyphs{id}");

		if (ImGui.BeginPopup($"glyphs{id}")) {
			ImGui.SetNextItemWidth(220f);
			string filter = this.iconFilter;
			if (ImGui.InputTextWithHint($"##filter{id}", "search 1382 icons", ref filter, 32))
				this.iconFilter = filter;

			var matches = this.iconFilter.Trim().Length == 0
				? Popular.Select(i => (Name: i.ToString(), Char: (int)i))
				: AllIcons().Where(i => i.Name.Contains(this.iconFilter.Trim(), StringComparison.OrdinalIgnoreCase));

			var shown = matches.Take(80).ToList();

			if (ImGui.BeginChild($"list{id}", new Vector2(240f, 260f))) {
				foreach (var (name, code) in shown) {
					// ⭐ The glyph itself beside the name -- Dalamud's prebuilt icon font is fine here,
					// since a menu is drawn at one size and never scaled.
					using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
						ImGui.Text(char.ConvertFromUtf32(code));

					ImGui.SameLine();
					if (ImGui.Selectable($"{name}##{id}{code}", code == glyph)) {
						glyph = code;
						changed = true;
						ImGui.CloseCurrentPopup();
					}
				}

				if (shown.Count == 80)
					ImGui.TextDisabled("...first 80. Narrow the search.");
			}

			ImGui.EndChild();
			ImGui.EndPopup();
		}

		return changed;
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
		this.font.Dispose();
		this.iconFont.Dispose();
	}
}
