using System;

using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace DeserokUtils.Features.EphemeralMarks;

/// <summary>
/// The tag font, rasterised at the size it is actually drawn at.
///
/// ## ⚠⚠ Why the tag was blurry while the shapes were crisp
///
/// The shapes are vector geometry, so they are generated at whatever size is asked for. The tag was
/// <c>AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, ...)</c> -- and a font is a **bitmap
/// atlas rasterised at one fixed size**, so drawing it at any other size is a stretched image, not
/// larger type. Both testers saw it immediately at 2x.
///
/// ⚠ The resolution normalisation makes non-integer ratios the norm rather than the exception: at
/// 1080p the effective scale is <c>0.75 x slider</c>, so the default 2.0 asks for 1.5x -- every other
/// output pixel sampling halfway between two texels. But even an exact 2x is soft under bilinear
/// filtering, which is why deserok saw it too on 1440p. There is no scale factor that makes a
/// resampled bitmap font sharp; the size has to be baked in.
///
/// ⭐ deserok's framing, and it is the correct fix rather than a nicer resample: *"can we select a
/// fontsize close to the scale? Like lets say 1x = 12pt, someone picks 2x, can we just move to 24pt"*.
///
/// ⭐ Uses the game's own **Axis** face, which is what FFXIV sets its own UI in, so the tag reads as
/// part of the game rather than as an overlay.
/// </summary>
internal sealed class MarkFont: IDisposable {
	/// ⚠ Bounds, because the size comes from a slider multiplied by a resolution ratio. The slider
	/// runs 0.4-2.5 and the ratio is 0.75 at 1080p through 1.5 at 4K, so the raw product spans roughly
	/// 5px to 64px -- and a 5px atlas is unreadable while nothing needs more than this ceiling.
	private const int MinPx = 9;
	private const int MaxPx = 72;

	/// <summary>
	/// ⚠⚠ Rebuilds are DEBOUNCED, and that is not a micro-optimisation. Dragging the size slider
	/// walks through dozens of integer sizes, and each one would otherwise kick off a font atlas
	/// build -- the per-frame audit in DeserokUtils.md, in its most expensive possible form. Waiting
	/// for the number to settle means one build per adjustment instead of fifty.
	/// </summary>
	private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

	private IFontHandle? handle;
	private int builtPx;
	private int wantPx;
	private DateTime wantSince = DateTime.MinValue;

	/// <summary>
	/// Ask for a size, and get back the size that should actually be drawn.
	///
	/// ⭐ Returns the BUILT size once a handle exists, not the requested one, so the glyphs are always
	/// drawn 1:1 against their own rasterisation -- asking for 23.4px from a 23px atlas would put the
	/// blur straight back.
	///
	/// ⚠ Returns the requested size while nothing is built yet. The first frames after switching tags
	/// on therefore look exactly like the old behaviour, which is correct: a soft tag beats no tag,
	/// and the atlas build is asynchronous.
	/// </summary>
	public float Prepare(float px) {
		int rounded = Math.Clamp((int)MathF.Round(px), MinPx, MaxPx);
		if (rounded != this.wantPx) {
			this.wantPx = rounded;
			this.wantSince = DateTime.UtcNow;
		}

		if (this.wantPx != this.builtPx && DateTime.UtcNow - this.wantSince >= Settle)
			this.Rebuild();

		return this.handle is { Available: true } ? this.builtPx : px;
	}

	/// <summary>
	/// ⚠ Null while the atlas is still building, or if it failed. Callers fall back to the default
	/// font rather than skipping the tag.
	/// </summary>
	public ILockedImFont? TryLock() {
		if (this.handle is not { Available: true })
			return null;
		return this.handle.TryLock(out _);
	}

	/// <summary>
	/// ⚠ Built on FIRST USE, and only ever when the tag is actually switched on -- it is off by
	/// default, so nobody who never turns it on pays for a font atlas at all.
	/// </summary>
	private void Rebuild() {
		int px = this.wantPx;
		try {
			var built = Plugin.PluginInterface.UiBuilder.FontAtlas
				.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, px));

			// ⚠ Swap first, dispose second. Disposing the old handle before the new one is assigned
			// would leave a frame able to observe a disposed handle.
			var old = this.handle;
			this.handle = built;
			this.builtPx = px;
			old?.Dispose();

			Plugin.Log.Information($"EphemeralMarks: tag font rebuilt at {px}px (Axis).");
		}
		catch (Exception ex) {
			// ⚠ Never let a font build take the overlay down. builtPx stays put, so this retries on
			// the next size change rather than every frame.
			this.builtPx = px;
			Plugin.Log.Error(ex, $"EphemeralMarks: could not build the tag font at {px}px; using the default font.");
		}
	}

	public void Dispose() {
		this.handle?.Dispose();
		this.handle = null;
	}
}
