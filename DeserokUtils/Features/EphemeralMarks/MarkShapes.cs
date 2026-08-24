using System;
using System.Linq;
using System.Numerics;

using Dalamud.Interface;

using Dalamud.Bindings.ImGui;

namespace DeserokUtils.Features.EphemeralMarks;

/// <summary>
/// Which glyph to draw over somebody. Requested by Bunny in #dev-notes, 2026-08-22:
/// *"Add different symbols for marks please :) Heart is needed^"*
/// </summary>
/// <remarks>
/// ⚠ The numbers are persisted, so these are append-only. Reordering renames everybody's saved
/// choices into whatever now sits at that index.
/// </remarks>
public enum MarkShape {
	/// <summary>The bespoke reticle. ⭐ Kept vector because it is deliberately in FFXIV's own visual
	/// language, which no general-purpose icon set can imitate.</summary>
	Reticle = 0,

	/// <summary>The tuned five-point star. Kept vector for the same reason.</summary>
	Star = 1,

	// ⚠⚠ 2-6 WERE Heart, Circle, Triangle, Square and Cross, hand-rolled as vector outlines and
	// retired within the hour. Font Awesome ships all of them and 1377 more, drawn properly. The
	// numbers stay reserved and a migration maps them onto Icon -- reusing them would silently turn
	// somebody's saved heart into whatever now sits at 2.

	/// <summary>A Font Awesome glyph, chosen by codepoint. See the Icon fields on the config.</summary>
	Icon = 7,
}

/// <summary>
/// Every marker glyph, in one place.
///
/// ⭐ Two rules hold across all of them, and a new shape that breaks either will look wrong in a way
/// that is hard to name afterwards:
///
/// 1. **Outlines, not solids.** A filled shape becomes a blob over a bright or busy background. The
///    reticle's small filled tip is the deliberate exception -- it is what makes the point read at a
///    glance.
/// 2. **Every stroke twice, dark then coloured.** Thin light lines vanish against snow, sand, and
///    most of a Frontline.
///
/// ⚠ Stroke weights scale with the marker but carry a floor. A sub-pixel line does not anti-alias
/// into something faint -- it flickers as the marker moves, which is worse than being too thick.
/// </summary>
internal static class MarkShapes {
	private const uint Shadow = 0xC0000000;

	public static string Label(MarkShape shape) => shape switch {
		MarkShape.Reticle => "Reticle",
		MarkShape.Star => "Star",
		MarkShape.Icon => "Icon",
		_ => "Icon",
	};

	public static void Draw(
		ImDrawListPtr draw, MarkShape shape, int glyph, ImFontPtr? icons, float iconPx,
		Vector2 at, uint colour, float s) {
		switch (shape) {
			case MarkShape.Star:
				DrawStar(draw, at, colour, s);
				break;

			// ⚠ Legacy 2-6 land here too, which is intentional: the migration rewrites them, but a
			// config that has not been migrated yet still draws something sensible rather than
			// silently falling back to a reticle.
			case MarkShape.Icon:
			case (MarkShape)2:
			case (MarkShape)3:
			case (MarkShape)4:
			case (MarkShape)5:
			case (MarkShape)6:
				DrawGlyph(draw, glyph, icons, iconPx, at, colour, s);
				break;

			default:
				DrawReticle(draw, at, colour, s);
				break;
		}
	}

	// ── the picker, shared by every feature that lets you choose a marker ────
	//
	// ⭐ Extracted here the moment a SECOND feature wanted it, rather than copied. One place
	// that knows how a marker looks, and one place that knows how you choose one.

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

	private static string iconFilter = string.Empty;

	/// <summary>
	/// Shape, plus a glyph picker when the shape is Icon.
	///
	/// ⚠ The search is capped at 80 results and SAYS SO when it truncates. A silent cap reads as
	/// "that icon does not exist", which would send somebody looking for a bug that is not there.
	/// </summary>
	public static bool GlyphPicker(string id, ref MarkShape shape, ref int glyph, float width = 150f) {
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
			string filter = iconFilter;
			if (ImGui.InputTextWithHint($"##filter{id}", "search 1382 icons", ref filter, 32))
				iconFilter = filter;

			var matches = iconFilter.Trim().Length == 0
				? Popular.Select(i => (Name: i.ToString(), Char: (int)i))
				: AllIcons().Where(i => i.Name.Contains(iconFilter.Trim(), StringComparison.OrdinalIgnoreCase));

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

	/// <summary>
	/// A Font Awesome glyph with a shadow layer behind it -- the Honorific approach, and deserok's
	/// call after I spent a while tuning bezier curves: *"theres hearts, triangles, hell, all kinds of
	/// random shapes... can't we just do our own outline by making one glyph, and then adding a
	/// shadowlayer to it."*
	///
	/// ⭐⭐ This replaces five hand-rolled outlines with 1382 properly drawn shapes, and it is strictly
	/// less code. The curve-fitting was the wrong instinct: the answer was already shipped in Dalamud.
	///
	/// ## ⚠ The shape rule is AMENDED here, not broken
	///
	/// Every other glyph is an outline because a filled shape becomes a blob over a bright background.
	/// Font Awesome icons are solid. But outlining was the MEANS -- the end is staying readable over
	/// snow and spell effects, and a solid glyph ringed in dark achieves that better than a thin
	/// stroke, which is why map markers in most games look like this.
	///
	/// ⚠ Eight offsets, not four. Four leaves the diagonals bare and the halo visibly square at the
	/// corners; eight closes it for one more pass over two glyphs.
	///
	/// ⚠ Falls back to the reticle while the atlas is still building, rather than drawing nothing. A
	/// marker that blinks out for a few frames after a settings change reads as a bug.
	/// </summary>
	private static void DrawGlyph(
		ImDrawListPtr draw, int glyph, ImFontPtr? icons, float iconPx, Vector2 at, uint colour, float s) {
		if (icons is not { } face || glyph <= 0) {
			DrawReticle(draw, at, colour, s);
			return;
		}

		string text = char.ConvertFromUtf32(glyph);
		ImGui.PushFont(face);
		var size = ImGui.CalcTextSize(text);
		ImGui.PopFont();

		// ⚠ Rounded, for the same reason the tag is: a glyph quad on a half pixel is resampled even
		// when the atlas size is exactly right.
		var origin = new Vector2(
			MathF.Round(at.X - size.X / 2f),
			MathF.Round(at.Y - 18f * s - size.Y / 2f));

		float halo = MathF.Max(1.4f, 2.2f * s);
		for (int i = 0; i < 8; i++) {
			float angle = i * MathF.PI / 4f;
			var offset = new Vector2(MathF.Cos(angle) * halo, MathF.Sin(angle) * halo);
			draw.AddText(face, iconPx, origin + offset, Shadow, text);
		}

		draw.AddText(face, iconPx, origin, colour, text);
	}

	/// <summary>
	/// An outlined diamond with a filled point and a small detached square above it.
	///
	/// ⭐ Deliberately in the visual language of the game's own target reticle -- a marker that looks
	/// like it belongs reads faster than one that looks like a debug overlay.
	///
	/// ⚠⚠ But not a colour a target reticle uses, and that is the point. A friend marker mistakable
	/// for "this is my current target" would be a worse misread than no marker at all. Same family,
	/// different signal.
	/// </summary>
	private static void DrawReticle(ImDrawListPtr draw, Vector2 at, uint colour, float s) {
		float halfWidth = 10f * s, tall = 32f * s, shoulder = 12.5f * s, square = 5f * s, gap = 8f * s;
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
	/// A five-pointed star, the default for the party leader.
	///
	/// ⭐ deserok's idea, and asymmetric in exactly the useful direction: from the leader's client
	/// everyone else is a diamond, while from Bunny's and Q's the leader is a star. They follow him,
	/// so "where is he" is the question they are actually asking -- and a distinct SHAPE answers it
	/// faster than a distinct colour, because shape survives peripheral vision and colour blindness.
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
		Stroke(draw, points, heavy, light, colour);
	}

	/// <summary>The two-pass stroke every closed shape shares. Dark underneath, colour on top.</summary>
	private static void Stroke(ImDrawListPtr draw, Span<Vector2> points, float heavy, float light, uint colour) {
		draw.AddPolyline(ref points[0], points.Length, Shadow, ImDrawFlags.Closed, heavy);
		draw.AddPolyline(ref points[0], points.Length, colour, ImDrawFlags.Closed, light);
	}
}
