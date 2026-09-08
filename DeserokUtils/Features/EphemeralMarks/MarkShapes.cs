using System;
using System.Linq;
using System.Numerics;

using Dalamud.Interface;

using Dalamud.Bindings.ImGui;

namespace DeserokUtils.Features.EphemeralMarks;

public enum MarkShape {

	Reticle = 0,

	Star = 1,

	Icon = 7,
}

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

	private static readonly FontAwesomeIcon[] Popular = {
		FontAwesomeIcon.Heart, FontAwesomeIcon.Star, FontAwesomeIcon.Crown, FontAwesomeIcon.Skull,
		FontAwesomeIcon.Paw, FontAwesomeIcon.Cat, FontAwesomeIcon.Ghost, FontAwesomeIcon.Snowflake,
		FontAwesomeIcon.Gem, FontAwesomeIcon.Bolt, FontAwesomeIcon.Fire, FontAwesomeIcon.Moon,
		FontAwesomeIcon.Sun, FontAwesomeIcon.Leaf, FontAwesomeIcon.Fish, FontAwesomeIcon.Dragon,
		FontAwesomeIcon.Crosshairs, FontAwesomeIcon.LocationArrow, FontAwesomeIcon.Bullseye,
		FontAwesomeIcon.Anchor, FontAwesomeIcon.Bell, FontAwesomeIcon.Cookie,
	};

	private static (string Name, int Char)[]? allIcons;

	private static (string Name, int Char)[] AllIcons() =>
		allIcons ??= Enum.GetValues<FontAwesomeIcon>()
			.Select(i => (Name: i.ToString(), Char: (int)i))
			.Where(i => i.Char > 0)
			.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();

	private static string iconFilter = string.Empty;

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

	private static void DrawReticle(ImDrawListPtr draw, Vector2 at, uint colour, float s) {
		float halfWidth = 10f * s, tall = 32f * s, shoulder = 12.5f * s, square = 5f * s, gap = 8f * s;
		float heavy = MathF.Max(1.5f, 3f * s), light = MathF.Max(1f, 1.6f * s);

		var left = new Vector2(at.X - halfWidth, at.Y - shoulder);
		var right = new Vector2(at.X + halfWidth, at.Y - shoulder);
		var top = new Vector2(at.X, at.Y - tall);

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

	private static void DrawStar(ImDrawListPtr draw, Vector2 at, uint colour, float s) {
		float outer = 19f * s, inner = 7.9f * s, lift = 18f * s;
		float heavy = MathF.Max(1.6f, 3.5f * s), light = MathF.Max(1f, 1.8f * s);
		var centre = new Vector2(at.X, at.Y - lift);

		Span<Vector2> points = stackalloc Vector2[10];
		for (int i = 0; i < 10; i++) {

			float angle = -MathF.PI / 2f + i * MathF.PI / 5f;
			float radius = (i % 2 == 0) ? outer : inner;
			points[i] = centre + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
		}

		Stroke(draw, points, heavy, light, colour);
	}

	private static void Stroke(ImDrawListPtr draw, Span<Vector2> points, float heavy, float light, uint colour) {
		draw.AddPolyline(ref points[0], points.Length, Shadow, ImDrawFlags.Closed, heavy);
		draw.AddPolyline(ref points[0], points.Length, colour, ImDrawFlags.Closed, light);
	}
}
