using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace DeserokUtils.UI;

internal static class Theme {

	private static Vector4 Hex(uint value, bool hasAlpha = false)
		=> hasAlpha
			? new Vector4(
				((value >> 24) & 0xFF) / 255f, ((value >> 16) & 0xFF) / 255f,
				((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f)
			: new Vector4(
				((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f,
				(value & 0xFF) / 255f, 1f);

	public static readonly Vector4 Ground = Hex(0x000000);

	public static readonly Vector4 Panel = Hex(0x141416);

	public static readonly Vector4 Field = Hex(0x1C1C1E);

	public static readonly Vector4 Raised = Hex(0x2C2C2E);

	public static readonly Vector4 RuleStrong = Hex(0x38383A);

	public static readonly Vector4 RuleHair = Hex(0x2C2C2E);

	public static readonly Vector4 BorderControl = Hex(0x48484A);

	public static readonly Vector4 CardBorder = Hex(0xFFFFFF1F, hasAlpha: true);

	public static readonly Vector4 Ink = Hex(0xF5F5F7);
	public static readonly Vector4 Dim = Hex(0xA8A8AD);
	public static readonly Vector4 Faint = Hex(0x7C7C82);

	public static readonly Vector4 Positive = Hex(0x4EA36B);
	public static readonly Vector4 Negative = Hex(0xD6584A);

	public static readonly Vector4 SwitchOff = Hex(0x39393D);

	public static Vector4 Accent
		=> Plugin.Config is null ? Presets[0].Colour : Hex(Plugin.Config.UiAccentRgb);

	public static Vector4 AccentHover => Mix(Accent, Ink, 0.14f);

	public static Vector4 AccentPressed => Mix(Accent, Ground, 0.18f);

	public static Vector4 AccentAlpha(float a) => Accent with { W = a };

	public static Vector4 OnColour(Vector4 c) {
		var luminance = (0.2126f * c.X) + (0.7152f * c.Y) + (0.0722f * c.Z);
		return luminance > 0.55f ? Hex(0x000000) : Hex(0xFFFFFF);
	}

	public static Vector4 OnAccent => OnColour(Accent);

	public static readonly (string Name, Vector4 Colour)[] Presets = {
		("Blue", Hex(0x3D87EB)),
		("Indigo", Hex(0x6366F1)),
		("Lilac", Hex(0x9B6DFF)),
		("Rose", Hex(0xE0567F)),
		("Amber", Hex(0xE0A53C)),
		("Emerald", Hex(0x4EA36B)),
		("Teal", Hex(0x2BB3B3)),
	};

	public static uint U(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

	public static Vector4 Fade(Vector4 c, float alpha) => c with { W = c.W * alpha };

	public static Vector4 Mix(Vector4 a, Vector4 b, float t)
		=> new(
			a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t),
			a.Z + ((b.Z - a.Z) * t), a.W + ((b.W - a.W) * t));

	public static uint ToRgb(Vector4 c)
		=> ((uint)Math.Round(Math.Clamp(c.X, 0f, 1f) * 255f) << 16)
			| ((uint)Math.Round(Math.Clamp(c.Y, 0f, 1f) * 255f) << 8)
			| (uint)Math.Round(Math.Clamp(c.Z, 0f, 1f) * 255f);

	private static int colours;
	private static int vars;

	public static void Push() {
		colours = 0;
		vars = 0;

		var accent = Accent;

		Colour(ImGuiCol.WindowBg, Ground);
		Colour(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0f));
		Colour(ImGuiCol.PopupBg, Panel);
		Colour(ImGuiCol.Border, BorderControl);
		Colour(ImGuiCol.BorderShadow, new Vector4(0f, 0f, 0f, 0f));

		Colour(ImGuiCol.Text, Ink);
		Colour(ImGuiCol.TextDisabled, Faint);

		Colour(ImGuiCol.TitleBg, Panel);
		Colour(ImGuiCol.TitleBgActive, Panel);
		Colour(ImGuiCol.TitleBgCollapsed, Panel);

		Colour(ImGuiCol.FrameBg, Field);
		Colour(ImGuiCol.FrameBgHovered, Raised);
		Colour(ImGuiCol.FrameBgActive, Raised);

		Colour(ImGuiCol.Button, Field);
		Colour(ImGuiCol.ButtonHovered, Raised);
		Colour(ImGuiCol.ButtonActive, Raised);

		Colour(ImGuiCol.Header, Raised);
		Colour(ImGuiCol.HeaderHovered, Raised);
		Colour(ImGuiCol.HeaderActive, AccentAlpha(0.33f));

		Colour(ImGuiCol.CheckMark, accent);
		Colour(ImGuiCol.SliderGrab, accent);
		Colour(ImGuiCol.SliderGrabActive, AccentPressed);

		Colour(ImGuiCol.ScrollbarBg, new Vector4(0f, 0f, 0f, 0f));
		Colour(ImGuiCol.ScrollbarGrab, RuleStrong);
		Colour(ImGuiCol.ScrollbarGrabHovered, BorderControl);
		Colour(ImGuiCol.ScrollbarGrabActive, accent);

		Colour(ImGuiCol.Separator, RuleHair);
		Colour(ImGuiCol.SeparatorHovered, RuleStrong);
		Colour(ImGuiCol.SeparatorActive, accent);

		Colour(ImGuiCol.Tab, Field);
		Colour(ImGuiCol.TabHovered, Raised);
		Colour(ImGuiCol.TabActive, Raised);

		Colour(ImGuiCol.TableHeaderBg, Field);
		Colour(ImGuiCol.TableBorderStrong, RuleStrong);
		Colour(ImGuiCol.TableBorderLight, RuleHair);
		Colour(ImGuiCol.TableRowBg, new Vector4(0f, 0f, 0f, 0f));
		Colour(ImGuiCol.TableRowBgAlt, Fade(Field, 0.5f));

		Colour(ImGuiCol.TextSelectedBg, AccentAlpha(0.35f));
		Colour(ImGuiCol.NavHighlight, AccentAlpha(0.7f));

		Colour(ImGuiCol.ResizeGrip, new Vector4(0f, 0f, 0f, 0f));
		Colour(ImGuiCol.ResizeGripHovered, new Vector4(0f, 0f, 0f, 0f));
		Colour(ImGuiCol.ResizeGripActive, new Vector4(0f, 0f, 0f, 0f));

		Var(ImGuiStyleVar.WindowRounding, Chrome.WindowRounding);
		Var(ImGuiStyleVar.WindowBorderSize, 0f);
		Var(ImGuiStyleVar.WindowPadding, Vector2.Zero);
		Var(ImGuiStyleVar.WindowMinSize, Chrome.CurrentSize);
		Var(ImGuiStyleVar.ChildRounding, 12f * Chrome.Scale);
		Var(ImGuiStyleVar.ChildBorderSize, 0f);
		Var(ImGuiStyleVar.PopupRounding, 10f * Chrome.Scale);
		Var(ImGuiStyleVar.FrameRounding, 10f * Chrome.Scale);
		Var(ImGuiStyleVar.FrameBorderSize, 0f);
		Var(ImGuiStyleVar.FramePadding, new Vector2(10f, 6f) * Chrome.Scale);
		Var(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 8f) * Chrome.Scale);
		Var(ImGuiStyleVar.ScrollbarSize, 10f * Chrome.Scale);
		Var(ImGuiStyleVar.ScrollbarRounding, 999f);
		Var(ImGuiStyleVar.GrabRounding, 999f);
		Var(ImGuiStyleVar.GrabMinSize, 14f * Chrome.Scale);
		Var(ImGuiStyleVar.TabRounding, 8f * Chrome.Scale);
		Var(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f, 0.5f));
	}

	public static void Pop() {
		if (vars > 0) ImGui.PopStyleVar(vars);
		if (colours > 0) ImGui.PopStyleColor(colours);
		vars = 0;
		colours = 0;
	}

	private static void Colour(ImGuiCol which, Vector4 value) {
		ImGui.PushStyleColor(which, value);
		colours++;
	}

	private static void Var(ImGuiStyleVar which, float value) {
		ImGui.PushStyleVar(which, value);
		vars++;
	}

	private static void Var(ImGuiStyleVar which, Vector2 value) {
		ImGui.PushStyleVar(which, value);
		vars++;
	}
}
