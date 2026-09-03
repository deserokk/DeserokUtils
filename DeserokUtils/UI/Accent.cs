using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace DeserokUtils.UI;

/// <summary>
/// The plugin's accent colours, and a button that uses them.
///
/// ⭐ From the design pass noted 2026-08-24: *"one accent colour — default ImGui is uniformly loud;
/// a single blue against greyscale reads instantly."* This is that, made concrete, and it exists
/// centrally so the second thing that wants a coloured button does not invent its own shade.
///
/// ⭐⭐ Two, not one, and the second earns its place by meaning something rather than by decorating:
///
///  - **Blue** is *look at this* — a scan, a preview, anything that only reads.
///  - **Amber** is *this does something* — spends a resource, moves your things, cannot be undone
///    by clicking again.
///
/// ⚠ That distinction is worth keeping honest. The moment amber is used for an ordinary button it
/// stops carrying any warning, and the next genuinely consequential button has nothing left to say.
/// </summary>
internal static class Accent {
	/// <summary>Reads. ⭐ Chosen against the game's warm brown-gold windows, where blue is the
	/// strongest contrast available — orange would sink into them.</summary>
	public static readonly Vector4 Blue = new(0.24f, 0.53f, 0.92f, 1f);

	/// <summary>Acts. Spends something, or changes something you would have to undo.</summary>
	public static readonly Vector4 Amber = new(0.90f, 0.55f, 0.16f, 1f);

	public static bool Button(string label, Vector4 colour, Vector2 size = default) {
		var hovered = Lighten(colour, 0.12f);
		var active = Lighten(colour, -0.10f);

		ImGui.PushStyleColor(ImGuiCol.Button, colour);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
		ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
		ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
		ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 5f));

		var clicked = ImGui.Button(label, size);

		ImGui.PopStyleVar(2);
		ImGui.PopStyleColor(4);
		return clicked;
	}

	private static Vector4 Lighten(Vector4 c, float amount)
		=> new(
			System.Math.Clamp(c.X + amount, 0f, 1f),
			System.Math.Clamp(c.Y + amount, 0f, 1f),
			System.Math.Clamp(c.Z + amount, 0f, 1f),
			c.W);
}
