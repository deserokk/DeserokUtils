using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace DeserokUtils.UI;

internal static class Accent {

	public static readonly Vector4 Blue = new(0.24f, 0.53f, 0.92f, 1f);

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
