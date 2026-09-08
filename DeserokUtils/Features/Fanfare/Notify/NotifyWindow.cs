using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DeserokUtils.Features.Fanfare.Notify;

internal sealed class NotifyWindow: Window {
	private const ImGuiWindowFlags OverlayFlags =
		ImGuiWindowFlags.NoDecoration |
		ImGuiWindowFlags.NoInputs |
		ImGuiWindowFlags.NoSavedSettings |
		ImGuiWindowFlags.NoFocusOnAppearing |
		ImGuiWindowFlags.NoBringToFrontOnFocus |
		ImGuiWindowFlags.NoBackground |
		ImGuiWindowFlags.NoMove;

	private const float RarityFontFactor = 0.85f;

	private const float RarityDim = 0.62f;

	private Notification? current;
	private Preset preset = Preset.XboxOne;
	private float displayTime = 7f;
	private float transition = 0.15f;

	private bool frozen;

	internal NotifyWindow(): base("##FanfareNotification", OverlayFlags) {
		this.RespectCloseHotkey = false;
		this.DisableWindowSounds = true;
		this.IsOpen = false;
	}

	internal bool IsPlaying => this.current is not null;

	internal void Play(Notification notification) {
		this.frozen = false;
		this.Begin(notification);
	}

	internal void Hold(Notification notification) {
		this.Begin(notification);
		this.frozen = true;
	}

	private void Begin(Notification notification) {
		this.current = notification;

		notification.StartedAt = DateTime.UtcNow;

		this.preset = Preset.ForStyle(Plugin.Config.Fanfare.Style);
		this.transition = Plugin.Config.Fanfare.TransitionOverride > 0f
			? Plugin.Config.Fanfare.TransitionOverride
			: this.preset.Transition;

		var wanted = Plugin.Config.Fanfare.DisplayTimeOverride > 0f
			? Plugin.Config.Fanfare.DisplayTimeOverride
			: this.preset.DisplayTime;

		var extraSlides = Math.Max(0, notification.SlideCount - 2);
		wanted *= 1f + (extraSlides * 0.5f);

		this.displayTime = Math.Max(wanted, this.transition * 13f);

		this.IsOpen = true;
	}

	internal void Stop() {
		this.current = null;
		this.frozen = false;
		this.IsOpen = false;
	}

	public override void PreDraw() {
		var viewport = ImGui.GetMainViewport();
		this.Position = viewport.Pos;
		this.Size = viewport.Size;
		this.PositionCondition = ImGuiCond.Always;
		this.SizeCondition = ImGuiCond.Always;
	}

	public override void Draw() {
		if (this.current is null)
			return;

		if (this.frozen) {

			this.transition = Plugin.Config.Fanfare.TransitionOverride > 0f
				? Plugin.Config.Fanfare.TransitionOverride
				: this.preset.Transition;
			this.DrawXboxOne(this.current, this.transition * 11f);
			return;
		}

		var t = this.current.Elapsed;
		if (t >= this.displayTime) {
			this.Stop();
			return;
		}

		this.DrawXboxOne(this.current, t);
	}

	private void DrawXboxOne(Notification notification, float t) {
		var T = this.transition;
		var D = this.displayTime;
		var scale = Math.Max(0.5f, Plugin.Config.Fanfare.Scale);

		var measuredUnder = HashCode.Combine(
			Plugin.Config.Fanfare.MaxWidth, Plugin.Config.Fanfare.ShowDescription,
			Plugin.Config.Fanfare.ShowPoints, Plugin.Config.Fanfare.ShowRarity,
			Plugin.Config.Fanfare.ShowReward);
		if (notification.MeasuredWidth is null || notification.MeasuredUnder != measuredUnder) {
			BuildLines(notification);
			notification.MeasuredWidth = this.MeasureCardWidth(notification);
			this.TruncateLines(notification, notification.MeasuredWidth.Value);
			notification.MeasuredUnder = measuredUnder;
		}

		var fullWidth = notification.MeasuredWidth.Value * scale;
		var height = this.preset.Height * scale;
		var rounding = this.preset.Rounding * scale;
		var gap = this.preset.ColumnGap * scale;

		var startWidth = fullWidth * this.preset.StartWidthFraction;
		var expand = Easing.CubicBezier(0f, 0.5f, 1f, 1f, Easing.Phase(t, T * 7f, T));
		var retract = Easing.CubicBezier(0.75f, 0f, 1f, 1f, Easing.Phase(t, D - (T * 11.5f), T * 2f));
		var width = Easing.Lerp(
			Easing.Lerp(startWidth, fullWidth, expand),
			startWidth,
			retract);

		var collapse = Easing.CubicBezier(0.75f, 0f, 1f, 1f, Easing.Phase(t, D - (T * 7f), T * 2f));
		var cardHeight = height * (1f - collapse);
		if (cardHeight <= 1f)
			return;

		var centre = this.AnchorPoint(fullWidth, height);
		var cardMin = new Vector2(centre.X - (width / 2f), centre.Y - (cardHeight / 2f));
		var cardMax = new Vector2(cardMin.X + width, cardMin.Y + cardHeight);

		var draw = ImGui.GetWindowDrawList();

		var cardAlpha = Easing.CubicBezier(0f, 0.5f, 1f, 1f, Easing.Phase(t, T * 5f, T));
		DrawVerticalGradient(draw, cardMin, cardMax, this.preset.CardTop, this.preset.CardBottom, cardAlpha, rounding);

		draw.PushClipRect(cardMin, cardMax, true);

		this.DrawShine(draw, cardMin, cardMax, Easing.Phase(t, (D / 7f) + (T * 2.5f), T * 10f));
		this.DrawShine(draw, cardMin, cardMax, Easing.Phase(t, (D / 2f) + (T * 2.5f), T * 10f));

		var tileIn = Easing.EaseInOut(Easing.Phase(t, 0f, T));
		var tileOut = Easing.CubicBezier(1f, 0f, 1f, 1f, Easing.Phase(t, D - (T * 8f), T * 2f));
		var tileScale = tileIn * (1f - tileOut);

		if (tileScale > 0.01f) {
			var tileSize = height * tileScale;
			var tileCentre = new Vector2(cardMin.X + (height / 2f), (cardMin.Y + cardMax.Y) / 2f);
			var tileMin = new Vector2(tileCentre.X - (tileSize / 2f), tileCentre.Y - (tileSize / 2f));
			var tileMax = new Vector2(tileCentre.X + (tileSize / 2f), tileCentre.Y + (tileSize / 2f));

			var tileColour = notification.IsRare ? this.preset.IconTileRare : this.preset.IconTile;
			draw.AddRectFilled(tileMin, tileMax, Colour(tileColour, 1f), this.preset.IconRounding * scale);

			var iconAlpha = Easing.Phase(t, T * 5f, T) * (1f - Easing.Phase(t, D - (T * 10.5f), T));
			if (notification.Icon is not null && iconAlpha > 0.01f) {
				var wrap = notification.Icon.GetWrapOrEmpty();
				var inset = tileSize * 0.08f;
				draw.AddImage(
					wrap.Handle,
					new Vector2(tileMin.X + inset, tileMin.Y + inset),
					new Vector2(tileMax.X - inset, tileMax.Y - inset),
					Vector2.Zero,
					Vector2.One,
					Colour(Vector4.One, iconAlpha));
			}
		}

		this.DrawText(draw, notification, t, cardMin, cardMax, height, gap, scale);

		draw.PopClipRect();
	}

	private static void BuildLines(Notification notification) {
		notification.UnlockLine = notification.UnlockMessage;

		notification.TitleLine = Plugin.Config.Fanfare.ShowPoints && notification.Points > 0
			? $"{notification.Points} - {notification.Title}"
			: notification.Title;

		notification.DescriptionLine = Flatten(notification.Description);

		if (!Plugin.Config.Fanfare.ShowRarity)
			notification.RarityLine = string.Empty;
		else if (notification.PercentOwned is float percent)
			notification.RarityLine = $"{percent:0.#}% of tracked players have this";
		else if (notification.TooNew)
			notification.RarityLine = "New achievement, no reliable tracking yet";
		else
			notification.RarityLine = string.Empty;

		notification.RewardLabelLine = notification.RewardLabel;
		notification.RewardNameLine = notification.RewardName;
	}

	private static string Flatten(string text) {
		if (string.IsNullOrEmpty(text))
			return string.Empty;

		return text
			.Replace("\r\n", Separator)
			.Replace('\r', ' ')
			.Replace("\n", Separator)
			.Trim();
	}

	private const string Ellipsis = "...";

	private const string Separator = " - ";

	private void TruncateLines(Notification notification, float cardWidth) {
		var baseline = ImGui.GetFontSize();
		if (baseline <= 0f)
			return;

		var height = this.preset.Height;
		var available = cardWidth - height - this.preset.ColumnGap - (height * 0.16f);
		if (available <= 0f)
			return;

		var titleSize = height * 0.30f;
		var smallSize = height * 0.24f;

		notification.UnlockLine = Fit(notification.UnlockLine, smallSize);
		notification.TitleLine = Fit(notification.TitleLine, titleSize);
		notification.DescriptionLine = Fit(notification.DescriptionLine, smallSize);
		notification.RarityLine = Fit(notification.RarityLine, smallSize * RarityFontFactor);
		notification.RewardLabelLine = Fit(notification.RewardLabelLine, smallSize);
		notification.RewardNameLine = Fit(notification.RewardNameLine, titleSize);

		string Fit(string text, float fontSize) {
			if (string.IsNullOrEmpty(text))
				return text;

			var scale = fontSize / baseline;
			if (ImGui.CalcTextSize(text).X * scale <= available)
				return text;

			if (text.Length <= 1)
				return Ellipsis;

			var cut = Math.Clamp(
				(int)(text.Length * (available / (ImGui.CalcTextSize(text).X * scale))) - 1,
				1, text.Length - 1);

			while (cut > 1 && ImGui.CalcTextSize(text[..cut] + Ellipsis).X * scale > available)
				cut--;

			return text[..cut].TrimEnd() + Ellipsis;
		}
	}

	private float MeasureCardWidth(Notification notification) {
		var baseline = ImGui.GetFontSize();
		if (baseline <= 0f)
			return this.preset.Width;

		var height = this.preset.Height;
		var titleSize = height * 0.30f;
		var smallSize = height * 0.24f;

		float WidthOf(string text, float fontSize)
			=> string.IsNullOrEmpty(text) ? 0f : ImGui.CalcTextSize(text).X * (fontSize / baseline);

		var widest = Math.Max(
			WidthOf(notification.UnlockLine, smallSize),
			WidthOf(notification.TitleLine, titleSize));

		if (Plugin.Config.Fanfare.ShowDescription) {
			widest = Math.Max(widest, WidthOf(notification.DescriptionLine, smallSize));
			widest = Math.Max(widest, WidthOf(notification.RarityLine, smallSize * RarityFontFactor));
			widest = Math.Max(widest, WidthOf(notification.RewardLabelLine, smallSize));
			widest = Math.Max(widest, WidthOf(notification.RewardNameLine, titleSize));
		}

		var required = height + this.preset.ColumnGap + widest + (this.preset.Height * 0.16f);

		return Math.Clamp(required, this.preset.Width, Math.Max(this.preset.Width, Plugin.Config.Fanfare.MaxWidth));
	}

	private void DrawText(
		ImDrawListPtr draw,
		Notification notification,
		float t,
		Vector2 cardMin,
		Vector2 cardMax,
		float height,
		float gap,
		float scale) {

		var textAlpha = Easing.Phase(t, this.transition * 8.5f, this.transition * 2f);
		if (textAlpha <= 0.01f)
			return;

		var left = cardMin.X + height + gap;
		var right = cardMax.X - (6f * scale);
		if (right <= left)
			return;

		var centreY = (cardMin.Y + cardMax.Y) / 2f;
		var titleSize = height * 0.30f;
		var smallSize = height * 0.24f;

		var slides = BuildSlides(notification, height, titleSize, smallSize);

		var steps = slides.Count - 1;
		var progress = 0f;
		for (var i = 0; i < steps; i++) {

			var at = (this.displayTime * (i + 1) / (steps + 1)) - (this.transition * 2f);
			progress += Easing.CubicBezier(0.5f, 0f, 1f, 1f,
				Easing.Phase(t, at, this.transition * 2f));
		}

		var current = Math.Min((int)progress, steps);
		var frac = Math.Clamp(progress - current, 0f, 1f);

		var fade = 1f - Easing.Phase(t, this.displayTime - (this.transition * 12.5f), this.transition * 2f);

		DrawSlide(draw, slides[current], left, centreY, -(height * frac),
			textAlpha * (1f - frac) * (current == steps ? fade : 1f));

		if (frac <= 0.001f || current + 1 >= slides.Count)
			return;

		DrawSlide(draw, slides[current + 1], left, centreY, height - (height * frac),
			textAlpha * frac * (current + 1 == steps ? fade : 1f));
	}

	private readonly record struct Row(string Text, float Y, float Size, Vector4 Colour, float Alpha);

	private List<Row[]> BuildSlides(Notification n, float height, float titleSize, float smallSize) {

		var slides = new List<Row[]>(3);
		slides.Add(new Row[] {
			new(n.UnlockLine, -(height * 0.26f), smallSize, this.preset.TextSecondary, 1f),
			new(n.TitleLine, height * 0.02f, titleSize, this.preset.TextPrimary, 1f),
		});

		if (n.HasDescriptionSlide && !string.IsNullOrWhiteSpace(n.DescriptionLine)) {
			slides.Add(string.IsNullOrEmpty(n.RarityLine)

				? new Row[] {
					new(n.DescriptionLine, -(smallSize * 0.6f), smallSize, this.preset.TextSecondary, 1f),
				}
				: new Row[] {
					new(n.DescriptionLine, -(height * 0.24f), smallSize, this.preset.TextSecondary, 1f),
					new(n.RarityLine, height * 0.06f, smallSize * RarityFontFactor,
						this.preset.TextSecondary, RarityDim),
				});
		}

		if (n.HasRewardSlide && !string.IsNullOrWhiteSpace(n.RewardNameLine)) {
			slides.Add(new Row[] {
				new(n.RewardLabelLine, -(height * 0.26f), smallSize, this.preset.TextSecondary, 1f),
				new(n.RewardNameLine, height * 0.02f, titleSize, this.preset.TextPrimary, 1f),
			});
		}

		return slides;
	}

	private static void DrawSlide(
		ImDrawListPtr draw, Row[] rows, float left, float centreY, float travel, float alpha) {

		foreach (var row in rows)
			DrawLine(draw, row.Text, new Vector2(left, centreY + row.Y + travel),
				row.Size, row.Colour, alpha * row.Alpha);
	}

	private static void DrawLine(ImDrawListPtr draw, string text, Vector2 pos, float size, Vector4 colour, float alpha) {
		if (alpha <= 0.01f || string.IsNullOrEmpty(text))
			return;
		draw.AddText(ImGui.GetFont(), size, pos, Colour(colour, alpha), text);
	}

	private void DrawShine(ImDrawListPtr draw, Vector2 cardMin, Vector2 cardMax, float phase) {
		if (phase <= 0f || phase >= 1f)
			return;

		var width = cardMax.X - cardMin.X;
		var height = cardMax.Y - cardMin.Y;
		var x = cardMin.X + Easing.Lerp(-1.5f * width, 1.0f * width, phase);
		var alpha = phase < 0.5f
			? Easing.Lerp(0f, 0.25f, phase * 2f)
			: Easing.Lerp(0.25f, 0f, (phase - 0.5f) * 2f);
		if (alpha <= 0.005f)
			return;

		const int Slices = 9;
		var band = width * 0.30f;
		var skew = height * 0.6f;
		var sliceWidth = band / Slices;

		for (var i = 0; i < Slices; i++) {

			var distance = Math.Abs(i - ((Slices - 1) / 2f)) / ((Slices - 1) / 2f);
			var sliceAlpha = alpha * (1f - distance) * 0.5f;
			if (sliceAlpha <= 0.002f)
				continue;

			var sx = x + (i * sliceWidth);
			draw.AddQuadFilled(
				new Vector2(sx + skew, cardMin.Y),
				new Vector2(sx + skew + sliceWidth, cardMin.Y),
				new Vector2(sx + sliceWidth, cardMax.Y),
				new Vector2(sx, cardMax.Y),
				Colour(Vector4.One, sliceAlpha));
		}
	}

	private static void DrawVerticalGradient(
		ImDrawListPtr draw, Vector2 min, Vector2 max, Vector4 top, Vector4 bottom, float alpha, float rounding) {

		if (alpha <= 0.01f)
			return;

		draw.AddRectFilled(min, max, Colour(top, alpha), rounding);

		var inset = Math.Min(rounding, (max.Y - min.Y) / 2f);
		if (max.X - min.X <= inset * 2f)
			return;

		var a = Colour(top, alpha);
		var b = Colour(bottom, alpha);
		draw.AddRectFilledMultiColor(
			new Vector2(min.X + inset, min.Y),
			new Vector2(max.X - inset, max.Y),
			a, a, b, b);
	}

	private Vector2 AnchorPoint(float width, float height) {
		const float EdgeInset = 32f;

		var viewport = ImGui.GetMainViewport();
		var pos = viewport.Pos;
		var size = viewport.Size;

		var x = Plugin.Config.Fanfare.Anchor switch {
			NotifyAnchor.TopLeft or NotifyAnchor.BottomLeft
				=> pos.X + EdgeInset + (width / 2f),
			NotifyAnchor.TopRight or NotifyAnchor.BottomRight
				=> pos.X + size.X - EdgeInset - (width / 2f),
			_ => pos.X + (size.X / 2f),
		};

		var y = Plugin.Config.Fanfare.Anchor switch {
			NotifyAnchor.TopLeft or NotifyAnchor.TopCentre or NotifyAnchor.TopRight
				=> pos.Y + EdgeInset + (height / 2f),
			_ => pos.Y + size.Y - EdgeInset - (height / 2f),
		};

		return new Vector2(x + Plugin.Config.Fanfare.OffsetX, y + Plugin.Config.Fanfare.OffsetY);
	}

	private static uint Colour(Vector4 colour, float alpha)
		=> ImGui.ColorConvertFloat4ToU32(colour with { W = colour.W * Math.Clamp(alpha, 0f, 1f) });
}
