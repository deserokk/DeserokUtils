using System;
using System.Numerics;

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
	Reticle = 0,
	Star = 1,
	Heart = 2,
	Circle = 3,
	Triangle = 4,
	Square = 5,
	Cross = 6,
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
		MarkShape.Heart => "Heart",
		MarkShape.Circle => "Circle",
		MarkShape.Triangle => "Triangle",
		MarkShape.Square => "Square",
		MarkShape.Cross => "Cross",
		_ => shape.ToString(),
	};

	public static void Draw(ImDrawListPtr draw, MarkShape shape, Vector2 at, uint colour, float s) {
		switch (shape) {
			case MarkShape.Star: DrawStar(draw, at, colour, s); break;
			case MarkShape.Heart: DrawHeart(draw, at, colour, s); break;
			case MarkShape.Circle: DrawRing(draw, at, colour, s); break;
			case MarkShape.Triangle: DrawPolygon(draw, at, colour, s, 3, -MathF.PI / 2f); break;
			case MarkShape.Square: DrawPolygon(draw, at, colour, s, 4, -MathF.PI / 4f); break;
			case MarkShape.Cross: DrawCross(draw, at, colour, s); break;
			default: DrawReticle(draw, at, colour, s); break;
		}
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

	/// <summary>
	/// ⭐ Sampled from the standard heart curve rather than drawn from arcs, so it is one closed
	/// polyline like every other shape here and gets the same mitred corners and the same two-pass
	/// stroke for free.
	///
	/// ⚠ The curve is generated top-down in screen space, so the parametric Y is NEGATED -- the maths
	/// convention puts +Y upward and ImGui puts it downward. Without that the heart is upside down,
	/// which reads as a slightly odd blob rather than as an obvious bug.
	///
	/// ⚠ 28 samples. Fewer shows flats along the lobes at 2x on a 1440p screen; more is invisible.
	/// </summary>
	private static void DrawHeart(ImDrawListPtr draw, Vector2 at, uint colour, float s) {
		const int N = 28;
		float lift = 20f * s;
		// The curve spans about 32 units wide; this brings it to roughly the star's footprint.
		float k = 1.15f * s;
		float heavy = MathF.Max(1.6f, 3.5f * s), light = MathF.Max(1f, 1.8f * s);
		var centre = new Vector2(at.X, at.Y - lift);

		Span<Vector2> points = stackalloc Vector2[N];
		for (int i = 0; i < N; i++) {
			float t = i * MathF.Tau / N;
			float x = 16f * MathF.Pow(MathF.Sin(t), 3f);
			float y = 13f * MathF.Cos(t) - 5f * MathF.Cos(2f * t)
				- 2f * MathF.Cos(3f * t) - MathF.Cos(4f * t);
			points[i] = centre + new Vector2(x * k, -y * k);
		}

		Stroke(draw, points, heavy, light, colour);
	}

	/// <summary>⚠ Not AddCircle: it takes a segment count and the default adapts to radius, so a ring
	/// drawn small then scaled up goes visibly polygonal. Sampling it here keeps it smooth at 2.5x.</summary>
	private static void DrawRing(ImDrawListPtr draw, Vector2 at, uint colour, float s) {
		const int N = 24;
		float radius = 15f * s, lift = 18f * s;
		float heavy = MathF.Max(1.6f, 3.5f * s), light = MathF.Max(1f, 1.8f * s);
		var centre = new Vector2(at.X, at.Y - lift);

		Span<Vector2> points = stackalloc Vector2[N];
		for (int i = 0; i < N; i++) {
			float angle = i * MathF.Tau / N;
			points[i] = centre + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
		}

		Stroke(draw, points, heavy, light, colour);
	}

	/// <summary>A regular polygon -- triangle and square share this, differing only in rotation.</summary>
	private static void DrawPolygon(ImDrawListPtr draw, Vector2 at, uint colour, float s, int sides, float rotation) {
		float radius = 16f * s, lift = 18f * s;
		float heavy = MathF.Max(1.6f, 3.5f * s), light = MathF.Max(1f, 1.8f * s);
		var centre = new Vector2(at.X, at.Y - lift);

		Span<Vector2> points = stackalloc Vector2[8];
		for (int i = 0; i < sides; i++) {
			float angle = rotation + i * MathF.Tau / sides;
			points[i] = centre + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
		}

		draw.AddPolyline(ref points[0], sides, Shadow, ImDrawFlags.Closed, heavy);
		draw.AddPolyline(ref points[0], sides, colour, ImDrawFlags.Closed, light);
	}

	/// <summary>
	/// An X. ⚠ Two strokes rather than a closed outline, so it is the one shape that cannot use
	/// <see cref="Stroke"/> -- a polyline through four corners would draw a bowtie.
	/// </summary>
	private static void DrawCross(ImDrawListPtr draw, Vector2 at, uint colour, float s) {
		float arm = 13f * s, lift = 18f * s;
		float heavy = MathF.Max(1.8f, 4f * s), light = MathF.Max(1.2f, 2.1f * s);
		var centre = new Vector2(at.X, at.Y - lift);

		var a1 = centre + new Vector2(-arm, -arm);
		var a2 = centre + new Vector2(arm, arm);
		var b1 = centre + new Vector2(arm, -arm);
		var b2 = centre + new Vector2(-arm, arm);

		draw.AddLine(a1, a2, Shadow, heavy);
		draw.AddLine(b1, b2, Shadow, heavy);
		draw.AddLine(a1, a2, colour, light);
		draw.AddLine(b1, b2, colour, light);
	}

	/// <summary>The two-pass stroke every closed shape shares. Dark underneath, colour on top.</summary>
	private static void Stroke(ImDrawListPtr draw, Span<Vector2> points, float heavy, float light, uint colour) {
		draw.AddPolyline(ref points[0], points.Length, Shadow, ImDrawFlags.Closed, heavy);
		draw.AddPolyline(ref points[0], points.Length, colour, ImDrawFlags.Closed, light);
	}
}
