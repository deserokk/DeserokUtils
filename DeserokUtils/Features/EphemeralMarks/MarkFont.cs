using System;

using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace DeserokUtils.Features.EphemeralMarks;

internal enum MarkFace {

	Axis,

	Icons,
}

internal sealed class MarkFont(MarkFace face): IDisposable {

	private const int MinPx = 9;
	private const int MaxPx = 72;

	private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

	private IFontHandle? handle;
	private int builtPx;
	private int wantPx;
	private DateTime wantSince = DateTime.MinValue;

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

	public ILockedImFont? TryLock() {
		if (this.handle is not { Available: true })
			return null;
		return this.handle.TryLock(out _);
	}

	private void Rebuild() {
		int px = this.wantPx;
		try {
			var atlas = Plugin.PluginInterface.UiBuilder.FontAtlas;

			var built = face == MarkFace.Icons
				? atlas.NewDelegateFontHandle(e => e.OnPreBuild(
					tk => tk.AddFontAwesomeIconFont(new SafeFontConfig { SizePx = px })))
				: atlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, px));

			var old = this.handle;
			this.handle = built;
			this.builtPx = px;
			old?.Dispose();

			Plugin.Log.Information($"EphemeralMarks: {face} font rebuilt at {px}px.");
		}
		catch (Exception ex) {

			this.builtPx = px;
			Plugin.Log.Error(ex, $"EphemeralMarks: could not build the {face} font at {px}px.");
		}
	}

	public void Dispose() {
		this.handle?.Dispose();
		this.handle = null;
	}
}
