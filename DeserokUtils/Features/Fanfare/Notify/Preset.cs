using System;
using System.Numerics;

namespace DeserokUtils.Features.Fanfare.Notify;

public enum PresetStyle {
	XboxOne,
}

internal sealed class Preset {
	internal required string Name { get; init; }
	internal required PresetStyle Style { get; init; }

	internal float Width { get; init; } = 300f;
	internal float Height { get; init; } = 45f;
	internal float Rounding { get; init; } = 6f;
	internal float IconRounding { get; init; }

	internal float StartWidthFraction { get; init; } = 0.166f;

	internal float ColumnGap { get; init; } = 10f;

	internal Vector4 CardTop { get; init; }
	internal Vector4 CardBottom { get; init; }
	internal Vector4 IconTile { get; init; }

	internal Vector4 IconTileRare { get; init; }
	internal Vector4 TextPrimary { get; init; }
	internal Vector4 TextSecondary { get; init; }

	internal float Transition { get; init; } = 0.15f;

	internal float DisplayTime { get; init; } = 7f;

	internal float MinimumDisplayTime => this.Transition * 13f;

	internal static readonly Preset XboxOne = new() {
		Name = "Xbox One",
		Style = PresetStyle.XboxOne,
		Width = 300f,
		Height = 45f,
		Rounding = 6f,
		IconRounding = 0f,
		StartWidthFraction = 0.166f,
		ColumnGap = 10f,

		CardTop = new Vector4(0.13f, 0.13f, 0.14f, 0.96f),
		CardBottom = new Vector4(0.07f, 0.07f, 0.08f, 0.96f),
		IconTile = new Vector4(0.72f, 0.58f, 0.35f, 1f),
		IconTileRare = new Vector4(0.55f, 0.78f, 0.98f, 1f),
		TextPrimary = new Vector4(1f, 1f, 1f, 1f),
		TextSecondary = new Vector4(0.82f, 0.82f, 0.84f, 1f),

		Transition = 0.15f,
		DisplayTime = 7f,
	};

	internal static Preset ForStyle(PresetStyle style) => style switch {
		PresetStyle.XboxOne => XboxOne,
		_ => XboxOne,
	};
}
