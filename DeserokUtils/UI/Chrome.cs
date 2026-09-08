using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace DeserokUtils.UI;

internal static class Chrome {

	public static float Scale => ImGuiHelpers.GlobalScale;

	public static float TextScale
		=> ImGuiHelpers.GlobalScale * Math.Clamp(Plugin.Config?.UiScale ?? 1f, 0.7f, 2f);

	public static float WindowRounding => 18f * Scale;

	public static float RailWidth => 252f * Scale;
	public static float HeaderHeight => 58f * TextScale;
	public static float FooterHeight => 34f * TextScale;
	public static float RowHeight => 38f * TextScale;
	public static float RowGap => 10f * Scale;
	public static float Pad => 18f * Scale;
	public static float RailPad => 12f * Scale;

	public static Vector2 CurrentSize { get; set; }

	private static Fonts fonts = null!;

	public static void Attach(Fonts f) => fonts = f;

	public static float TrackedCaps(Vector2 at, string text, Vector4 colour, float tracking = 0.14f) {
		var draw = ImGui.GetWindowDrawList();
		var packed = Theme.U(colour);
		var gap = ImGui.GetFontSize() * tracking;
		var x = at.X;

		foreach (var ch in text.ToUpperInvariant()) {
			var glyph = ch.ToString();
			draw.AddText(new Vector2(x, at.Y), packed, glyph);
			x += ImGui.CalcTextSize(glyph).X + gap;
		}

		return x - at.X;
	}

	public static void SectionLabel(string text) {
		using (fonts.Label.Push()) {
			var at = ImGui.GetCursorScreenPos();
			var width = TrackedCaps(at, text, Theme.Faint);
			ImGui.Dummy(new Vector2(width, ImGui.GetTextLineHeight()));
		}
	}

	public static void TextAt(Vector2 at, string text, Vector4 colour)
		=> ImGui.GetWindowDrawList().AddText(at, Theme.U(colour), text);

	public static string Fit(string text, float width) {
		if (ImGui.CalcTextSize(text).X <= width)
			return text;

		const string ellipsis = "...";
		var room = width - ImGui.CalcTextSize(ellipsis).X;
		var cut = text.Length;

		while (cut > 0 && ImGui.CalcTextSize(text[..cut]).X > room)
			cut--;

		var space = text.LastIndexOf(' ', Math.Max(0, cut - 1));
		if (space > text.Length / 2)
			cut = space;

		return text[..cut].TrimEnd() + ellipsis;
	}

	public static void Icon(Vector2 at, FontAwesomeIcon icon, Vector4 colour) {
		using (fonts.Icons.Push())
			ImGui.GetWindowDrawList().AddText(at, Theme.U(colour), icon.ToIconString());
	}

	public static void IconCentred(Vector2 min, Vector2 size, FontAwesomeIcon icon, Vector4 colour) {
		var glyph = IconSize(icon);
		Icon(min + ((size - glyph) * 0.5f), icon, colour);
	}

	public static Vector2 IconSize(FontAwesomeIcon icon) {
		using (fonts.Icons.Push())
			return ImGui.CalcTextSize(icon.ToIconString());
	}

	public static bool NavRow(string id, FontAwesomeIcon icon, string label, bool active, float width) {
		var draw = ImGui.GetWindowDrawList();
		var origin = ImGui.GetCursorScreenPos();

		ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0f, 0f, 0f, 0f));
		ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0f, 0f, 0f, 0f));
		var clicked = ImGui.Button($"##nav{id}", new Vector2(width, RowHeight));
		ImGui.PopStyleColor(3);

		var hovered = ImGui.IsItemHovered();
		if (active || hovered) {
			draw.AddRectFilled(
				origin, origin + new Vector2(width, RowHeight),
				Theme.U(active ? Theme.Raised : Theme.Field), 8f * Scale);
		}

		var tint = active || hovered ? Theme.Ink : Theme.Dim;

		var glyph = IconSize(icon);
		Icon(
			new Vector2(origin.X + (12f * TextScale), origin.Y + ((RowHeight - glyph.Y) * 0.5f)),
			icon, active ? Theme.Accent : tint);

		using (fonts.Body.Push()) {
			var size = ImGui.CalcTextSize(label);
			draw.AddText(
				new Vector2(origin.X + (36f * TextScale), origin.Y + ((RowHeight - size.Y) * 0.5f)),
				Theme.U(tint), Fit(label, width - (44f * TextScale)));
		}

		ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + RowHeight + RowGap));
		return clicked;
	}

	public static bool Toggle(string id, ref bool value) {
		var draw = ImGui.GetWindowDrawList();
		var width = 36f * TextScale;
		var height = 20f * TextScale;
		var line = MathF.Max(height, ImGui.GetTextLineHeight());

		var origin = ImGui.GetCursorScreenPos();
		ImGui.InvisibleButton($"##toggle{id}", new Vector2(width, line));

		var clicked = ImGui.IsItemClicked();
		var hovered = ImGui.IsItemHovered();
		if (clicked) value = !value;

		var top = origin.Y + ((line - height) * 0.5f);
		var min = new Vector2(origin.X, top);
		var max = new Vector2(origin.X + width, top + height);

		var track = value
			? (hovered ? Theme.AccentHover : Theme.Accent)
			: (hovered ? Theme.Raised : Theme.SwitchOff);

		draw.AddRectFilled(min, max, Theme.U(track), 999f);
		if (!value)
			draw.AddRect(min, max, Theme.U(Theme.BorderControl), 999f, ImDrawFlags.None, 1f);

		var radius = (height * 0.5f) - (2f * Scale);
		var knobX = value ? max.X - (2f * Scale) - radius : min.X + (2f * Scale) + radius;
		draw.AddCircleFilled(
			new Vector2(knobX, top + (height * 0.5f)), radius,
			Theme.U(value ? Theme.OnAccent : Theme.Dim));

		return clicked;
	}

	public static bool GlyphButton(string id, FontAwesomeIcon icon, float box = 30f) {
		var size = new Vector2(box * Scale, box * Scale);
		var origin = ImGui.GetCursorScreenPos();
		ImGui.InvisibleButton($"##{id}", size);

		var clicked = ImGui.IsItemClicked();
		var hovered = ImGui.IsItemHovered();

		if (hovered) {
			ImGui.GetWindowDrawList().AddRectFilled(
				origin, origin + size, Theme.U(Theme.Field), 8f * Scale);
		}

		var glyph = IconSize(icon);
		Icon(origin + ((size - glyph) * 0.5f), icon, hovered ? Theme.Ink : Theme.Dim);
		return clicked;
	}

	public static float Badge(Vector2 at, string text, Vector4 colour) {
		var draw = ImGui.GetWindowDrawList();

		using (fonts.Label.Push()) {
			var size = ImGui.CalcTextSize(text);
			var padX = 7f * Scale;
			var padY = 3f * Scale;
			var width = size.X + (padX * 2f);
			var height = size.Y + (padY * 2f);

			var min = new Vector2(at.X, at.Y - (height * 0.5f));
			draw.AddRect(
				min, min + new Vector2(width, height), Theme.U(colour), height * 0.5f,
				ImDrawFlags.None, 1f);
			draw.AddText(min + new Vector2(padX, padY), Theme.U(colour), text);
			return width;
		}
	}

	public static void Rule(float inset = 0f) {
		var draw = ImGui.GetWindowDrawList();
		var at = ImGui.GetCursorScreenPos();
		var width = ImGui.GetContentRegionAvail().X;
		draw.AddRectFilled(
			new Vector2(at.X + inset, at.Y), new Vector2(at.X + width - inset, at.Y + 1f),
			Theme.U(Theme.RuleHair));
		ImGui.Dummy(new Vector2(width, 1f));
	}

}
