using System;

using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;

namespace DeserokUtils.UI;

internal sealed class Fonts: IDisposable {
	private IFontHandle? title;
	private IFontHandle? body;
	private IFontHandle? label;
	private IFontHandle? icons;

	private float built;

	public IFontHandle Title => this.Text(ref this.title, 16f);

	public IFontHandle Body => this.Text(ref this.body, 13f);

	public IFontHandle Label => this.Text(ref this.label, 11f);

	public IFontHandle Icons => this.Icon(ref this.icons, 13f);

	public void Tick() {

		var scale = Chrome.TextScale;
		if (Math.Abs(scale - this.built) < 0.01f)
			return;

		this.built = scale;
		this.Drop();
	}

	private IFontHandle Text(ref IFontHandle? slot, float px) {
		slot ??= Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(
			e => e.OnPreBuild(tk => {
				var face = tk.AddDalamudDefaultFont(px * Chrome.TextScale);

				tk.AddGameGlyphs(
					new GameFontStyle(GameFontFamily.Axis, px * Chrome.TextScale), null, face);
			}));

		return slot;
	}

	private IFontHandle Icon(ref IFontHandle? slot, float px) {
		slot ??= Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(
			e => e.OnPreBuild(tk =>
				tk.AddFontAwesomeIconFont(
					new SafeFontConfig { SizePx = px * Chrome.TextScale })));

		return slot;
	}

	private void Drop() {
		this.title?.Dispose();
		this.body?.Dispose();
		this.label?.Dispose();
		this.icons?.Dispose();
		this.title = null;
		this.body = null;
		this.label = null;
		this.icons = null;
	}

	public void Dispose() => this.Drop();
}
