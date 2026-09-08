using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

using DeserokUtils.Input;

namespace DeserokUtils.UI;

internal readonly record struct DomainEntry(
	string Name, FontAwesomeIcon Icon, bool ShowsEverything = false);

internal sealed class OptionsWindow: Window {
	private readonly IReadOnlyList<DomainEntry> domains;

	private readonly IReadOnlyList<DomainEntry> pinned;

	private readonly IReadOnlyList<TabEntry> tweaks;
	private readonly Fonts fonts;
	private readonly HashSet<string> expanded = new();

	private int selected;

	private bool onSettings;
	private string search = string.Empty;

	private bool collapsed;

	private float pendingScale;

	private DomainEntry Current
		=> this.selected < this.domains.Count
			? this.domains[this.selected]
			: this.pinned[this.selected - this.domains.Count];

	private const string SettingsName = "Settings";

	private readonly Action<IReadOnlySet<string>> drawKeybinds;

	public OptionsWindow(
		IReadOnlyList<DomainEntry> domains, IReadOnlyList<DomainEntry> pinned,
		IReadOnlyList<TabEntry> tweaks, Fonts fonts,
		Action<IReadOnlySet<string>> drawKeybinds)
		: base("DeserokUtils##dsu_options",
			ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar
			| ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse) {
		this.domains = domains;
		this.pinned = pinned;
		this.tweaks = tweaks;
		this.fonts = fonts;
		this.drawKeybinds = drawKeybinds;
	}

	private static readonly Vector2[] Sizes = {
		new(980f, 660f),
		new(1120f, 760f),
		new(1260f, 880f),
	};

	public override void PreDraw() {
		this.fonts.Tick();

		var pick = Math.Clamp(Plugin.Config.UiSize, 0, Sizes.Length - 1);
		var full = Sizes[pick] * Chrome.Scale;

		Chrome.CurrentSize = this.collapsed
			? new Vector2(full.X, Chrome.HeaderHeight)
			: full;

		this.Size = Chrome.CurrentSize;
		this.SizeCondition = ImGuiCond.Always;

		Theme.Push();
	}

	public override void PostDraw() => Theme.Pop();

	public override void Draw() {
		using var _ = this.fonts.Body.Push();

		if (this.collapsed) {
			this.DrawCollapsed();
			return;
		}

		this.DrawRail();

		ImGui.SameLine(0f, 0f);

		var seam = ImGui.GetCursorScreenPos();
		ImGui.GetWindowDrawList().AddRectFilled(
			seam, new Vector2(seam.X + 1f, seam.Y + ImGui.GetContentRegionAvail().Y),
			Theme.U(Theme.RuleHair));

		ImGui.SameLine(0f, 1f);

		if (ImGui.BeginChild("dsu_body", new Vector2(0f, -1f), false, ImGuiWindowFlags.NoScrollbar)) {

			var origin = ImGui.GetCursorScreenPos();
			var size = ImGui.GetContentRegionAvail();

			this.DrawHeaderStrip(origin, size.X);
			this.DrawMiddle(origin, size);
			this.DrawFooter(origin, size);
		}

		ImGui.EndChild();
	}

	private void DrawCollapsed() {
		var s = Chrome.Scale;
		var origin = ImGui.GetCursorScreenPos();
		var width = ImGui.GetContentRegionAvail().X;
		var draw = ImGui.GetWindowDrawList();

		draw.AddRectFilled(
			origin, origin + new Vector2(width, Chrome.HeaderHeight),
			Theme.U(Theme.Panel), Chrome.WindowRounding);

		var tile = 22f * s;
		var tileAt = new Vector2(origin.X + (14f * s), origin.Y + ((Chrome.HeaderHeight - tile) * 0.5f));
		draw.AddRectFilled(tileAt, tileAt + new Vector2(tile, tile), Theme.U(Theme.Accent), 6f * s);

		var mark = Chrome.IconSize(FontAwesomeIcon.Bolt);
		Chrome.Icon(tileAt + ((new Vector2(tile, tile) - mark) * 0.5f), FontAwesomeIcon.Bolt, Theme.OnAccent);

		using (this.fonts.Title.Push()) {
			var line = ImGui.GetTextLineHeight();
			Chrome.TrackedCaps(
				new Vector2(tileAt.X + tile + (10f * s), origin.Y + ((Chrome.HeaderHeight - line) * 0.5f)),
				"DeserokUtils", Theme.Ink);
		}

		this.DrawWindowButtons(origin, width);
	}

	private void DrawWindowButtons(Vector2 origin, float width) {
		var s = Chrome.Scale;
		var box = 30f * s;
		var top = origin.Y + ((Chrome.HeaderHeight - box) * 0.5f);

		ImGui.SetCursorScreenPos(new Vector2(origin.X + width - box - (14f * s), top));
		if (Chrome.GlyphButton("close", FontAwesomeIcon.Times))
			this.IsOpen = false;

		ImGui.SetCursorScreenPos(new Vector2(origin.X + width - (box * 2f) - (18f * s), top));
		if (Chrome.GlyphButton(
				"collapse",
				this.collapsed ? FontAwesomeIcon.WindowMaximize : FontAwesomeIcon.WindowMinimize))
			this.collapsed = !this.collapsed;

		if (ImGui.IsItemHovered())
			ImGui.SetTooltip(this.collapsed ? "Open it back up" : "Collapse to the bar");
	}

	private void DrawRail() {
		var s = Chrome.Scale;
		var origin = ImGui.GetCursorScreenPos();
		var height = ImGui.GetContentRegionAvail().Y;

		ImGui.GetWindowDrawList().AddRectFilled(
			origin, new Vector2(origin.X + Chrome.RailWidth, origin.Y + height),
			Theme.U(Theme.Panel), Chrome.WindowRounding, ImDrawFlags.RoundCornersLeft);

		if (!ImGui.BeginChild(
				"dsu_rail", new Vector2(Chrome.RailWidth, -1f), false,
				ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) {
			ImGui.EndChild();
			return;
		}

		var railHeight = ImGui.GetContentRegionAvail().Y;

		ImGui.Dummy(new Vector2(0f, 15f * s));
		ImGui.Indent(Chrome.RailPad);
		this.DrawBrand();
		ImGui.Unindent(Chrome.RailPad);
		ImGui.Dummy(new Vector2(0f, 15f * s));

		Chrome.Rule(Chrome.RailPad);
		ImGui.Dummy(new Vector2(0f, 8f * s));

		ImGui.Indent(Chrome.RailPad);
		var width = ImGui.GetContentRegionAvail().X - Chrome.RailPad;

		for (var i = 0; i < this.domains.Count; i++) {
			var domain = this.domains[i];
			if (Chrome.NavRow(
					$"d{i}", domain.Icon, domain.Name, !this.onSettings && this.selected == i, width)) {
				this.selected = i;
				this.onSettings = false;
				this.search = string.Empty;
			}
		}

		ImGui.Unindent(Chrome.RailPad);

		var settingsTop = railHeight - Chrome.RowHeight - (14f * s);
		var pinnedTop = settingsTop - ((Chrome.RowHeight + (4f * s)) * this.pinned.Count);
		var ruleTop = pinnedTop - (12f * s);

		ImGui.SetCursorPosY(MathF.Max(ImGui.GetCursorPosY(), ruleTop));
		Chrome.Rule(Chrome.RailPad);

		ImGui.Indent(Chrome.RailPad);

		for (var i = 0; i < this.pinned.Count; i++) {
			var index = this.domains.Count + i;
			ImGui.SetCursorPosY(pinnedTop + ((Chrome.RowHeight + (4f * s)) * i));

			if (Chrome.NavRow(
					$"p{i}", this.pinned[i].Icon, this.pinned[i].Name,
					!this.onSettings && this.selected == index, width)) {
				this.selected = index;
				this.onSettings = false;
				this.search = string.Empty;
			}
		}

		ImGui.SetCursorPosY(settingsTop);
		if (Chrome.NavRow("settings", FontAwesomeIcon.Cog, SettingsName, this.onSettings, width)) {
			this.onSettings = true;
			this.search = string.Empty;
		}

		ImGui.Unindent(Chrome.RailPad);
		ImGui.EndChild();
	}

	private void DrawBrand() {
		var s = Chrome.Scale;
		var draw = ImGui.GetWindowDrawList();
		var origin = ImGui.GetCursorScreenPos();
		var tile = 28f * s;

		draw.AddRectFilled(origin, origin + new Vector2(tile, tile), Theme.U(Theme.Accent), 8f * s);

		var glyph = Chrome.IconSize(FontAwesomeIcon.Bolt);
		Chrome.Icon(
			origin + ((new Vector2(tile, tile) - glyph) * 0.5f), FontAwesomeIcon.Bolt, Theme.OnAccent);

		var textX = origin.X + tile + (11f * s);

		using (this.fonts.Title.Push())
			Chrome.TextAt(new Vector2(textX, origin.Y), "DeserokUtils", Theme.Ink);

		using (this.fonts.Label.Push()) {
			Chrome.TrackedCaps(
				new Vector2(textX, origin.Y + (16f * s)),
				$"v{Plugin.PluginInterface.Manifest.AssemblyVersion.ToString(3)}", Theme.Faint, 0.1f);
		}

		ImGui.Dummy(new Vector2(tile, tile));
	}

	private void DrawHeaderStrip(Vector2 origin, float width) {
		var s = Chrome.Scale;
		var draw = ImGui.GetWindowDrawList();

		draw.AddRectFilled(
			origin, new Vector2(origin.X + width, origin.Y + Chrome.HeaderHeight),
			Theme.U(Theme.Panel), Chrome.WindowRounding, ImDrawFlags.RoundCornersTopRight);

		var title = this.onSettings ? SettingsName : this.Current.Name;
		using (this.fonts.Title.Push()) {
			var line = ImGui.GetTextLineHeight();
			Chrome.TrackedCaps(
				new Vector2(origin.X + Chrome.Pad, origin.Y + ((Chrome.HeaderHeight - line) * 0.5f)),
				title, Theme.Ink);
		}

		this.DrawWindowButtons(origin, width);

		draw.AddRectFilled(
			new Vector2(origin.X, origin.Y + Chrome.HeaderHeight),
			new Vector2(origin.X + width, origin.Y + Chrome.HeaderHeight + 1f),
			Theme.U(Theme.RuleHair));
	}

	private void DrawFooter(Vector2 bodyOrigin, Vector2 bodySize) {
		var origin = new Vector2(bodyOrigin.X, bodyOrigin.Y + bodySize.Y - Chrome.FooterHeight);
		var width = bodySize.X;
		var draw = ImGui.GetWindowDrawList();

		draw.AddRectFilled(
			origin, new Vector2(origin.X + width, origin.Y + 1f), Theme.U(Theme.RuleHair));

		var y = origin.Y + ((Chrome.FooterHeight - ImGui.GetTextLineHeight()) * 0.5f);

		var shown = this.Visible().ToList();
		var switched = shown.Count(t => t.Get is not null);
		var on = shown.Count(t => t.Get?.Invoke() == true);

		var reference = !this.onSettings && this.selected >= this.domains.Count;

		var line = this.onSettings
			? "Appearance, diagnostics and keybinds"
			: reference
				? $"Reference · {Plural(shown.Count, "topic")}"
				: switched == 0
					? Plural(shown.Count, "tweak")
					: $"{Plural(shown.Count, "tweak")} · {on} of {switched} on";

		Chrome.TextAt(new Vector2(origin.X + Chrome.Pad, y), line, Theme.Faint);
	}

	private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? string.Empty : "s")}";

	private IEnumerable<TabEntry> Visible() {
		if (this.onSettings)
			return Array.Empty<TabEntry>();

		if (this.search.Length > 0) {
			var needle = this.search.Trim();
			return this.tweaks.Where(t =>
				t.Domains.Length > 0
				&& (t.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
					|| t.Summary.Contains(needle, StringComparison.OrdinalIgnoreCase)
					|| t.Domains.Any(d => d.Contains(needle, StringComparison.OrdinalIgnoreCase))));
		}

		if (this.Current.ShowsEverything) {
			var listed = this.domains
				.Where(d => !d.ShowsEverything)
				.Select(d => d.Name)
				.ToHashSet();

			return this.tweaks.Where(t => t.Domains.Any(listed.Contains));
		}

		var domain = this.Current.Name;
		return this.tweaks.Where(t => t.Domains.Contains(domain));
	}

	private void DrawMiddle(Vector2 bodyOrigin, Vector2 bodySize) {
		var s = Chrome.Scale;
		var top = bodyOrigin.Y + Chrome.HeaderHeight + 1f;
		var height = bodySize.Y - Chrome.HeaderHeight - 1f - Chrome.FooterHeight;

		ImGui.SetCursorScreenPos(new Vector2(bodyOrigin.X + Chrome.Pad, top + Chrome.Pad));

		var began = ImGui.BeginChild(
			"dsu_middle",
			new Vector2(bodySize.X - (Chrome.Pad * 2f), height - (Chrome.Pad * 2f)),
			false, ImGuiWindowFlags.None);

		if (!began) {
			ImGui.EndChild();
			return;
		}

		if (this.onSettings) {
			this.DrawSettings();
			ImGui.EndChild();
			return;
		}

		this.DrawSearch();
		ImGui.Dummy(new Vector2(0f, 6f * s));

		var shown = this.Visible().ToList();
		if (shown.Count == 0) {
			ImGui.Dummy(new Vector2(0f, 20f * s));
			ImGui.PushStyleColor(ImGuiCol.Text, Theme.Faint);
			ImGui.TextWrapped(
				this.search.Length > 0 ? $"Nothing matches \"{this.search}\"." : "Nothing here yet.");
			ImGui.PopStyleColor();
			ImGui.EndChild();
			return;
		}

		string? lastDomain = null;
		foreach (var entry in shown) {

			var head = entry.Domains.FirstOrDefault() ?? "Other";
			if (this.search.Length > 0 && head != lastDomain) {
				lastDomain = head;
				ImGui.Dummy(new Vector2(0f, 6f * s));
				Chrome.SectionLabel(head);
				ImGui.Dummy(new Vector2(0f, 4f * s));
			}

			this.DrawCard(entry, ImGui.GetContentRegionAvail().X);
		}

		ImGui.Dummy(new Vector2(0f, 6f * s));
		ImGui.EndChild();
	}

	private void DrawSearch() {
		var s = Chrome.Scale;
		var origin = ImGui.GetCursorScreenPos();

		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(34f * s, 8f * s));
		ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

		var text = this.search;
		if (ImGui.InputTextWithHint("##dsu_search", "Search tweaks", ref text, 64))
			this.search = text;

		ImGui.PopStyleVar();

		var height = ImGui.GetItemRectSize().Y;
		var glyph = Chrome.IconSize(FontAwesomeIcon.Search);
		Chrome.Icon(
			new Vector2(origin.X + (13f * s), origin.Y + ((height - glyph.Y) * 0.5f)),
			FontAwesomeIcon.Search, Theme.Faint);
	}

	private void DrawCard(TabEntry entry, float width) {
		var s = Chrome.Scale;
		var draw = ImGui.GetWindowDrawList();
		var headerHeight = 56f * Chrome.TextScale;
		var open = this.expanded.Contains(entry.Title);

		draw.ChannelsSplit(2);
		draw.ChannelsSetCurrent(1);

		var origin = ImGui.GetCursorScreenPos();
		var hasSwitch = entry.Get is not null && entry.Set is not null;

		var switchColumn = hasSwitch ? 56f * s : 0f;
		var switchLeft = origin.X + (16f * s);
		var switchY = origin.Y + ((headerHeight - (20f * s)) * 0.5f);

		var switchHovered = false;
		if (hasSwitch) {
			ImGui.SetCursorScreenPos(new Vector2(switchLeft, switchY));

			var value = entry.Get!();
			if (Chrome.Toggle($"sw_{entry.Title}", ref value))
				entry.Set!(value);

			switchHovered = ImGui.IsItemHovered();
		}
		else {

			draw.AddCircleFilled(
				new Vector2(switchLeft + (18f * s), origin.Y + (headerHeight * 0.5f)),
				3.5f * s, Theme.U(Theme.Faint));
		}

		ImGui.SetCursorScreenPos(new Vector2(origin.X + switchColumn, origin.Y));
		ImGui.InvisibleButton(
			$"##card_{entry.Title}", new Vector2(width - switchColumn, headerHeight));

		var hovered = ImGui.IsItemHovered() || switchHovered;

		if (ImGui.IsItemClicked()) {
			if (!this.expanded.Remove(entry.Title))
				this.expanded.Add(entry.Title);

			open = !open;
		}

		var textX = origin.X + (68f * s);
		var textRight = origin.X + width - (36f * s);

		using (this.fonts.Title.Push()) {
			Chrome.TextAt(new Vector2(textX, origin.Y + (11f * s)), entry.Title, Theme.Ink);

			if (entry.Bind?.Invoke() is { } bind) {
				var titleWidth = ImGui.CalcTextSize(entry.Title).X;
				Chrome.Badge(
					new Vector2(
						textX + titleWidth + (10f * s),
						origin.Y + (11f * s) + (ImGui.GetTextLineHeight() * 0.5f)),
					bind.IsBound ? bind.ToString() : "Unbound",
					bind.IsBound ? Theme.Dim : Theme.Faint);
			}
		}

		if (entry.Summary.Length > 0) {
			Chrome.TextAt(
				new Vector2(textX, origin.Y + (32f * s)),
				Chrome.Fit(entry.Summary, textRight - textX), Theme.Dim);
		}

		var expandable = entry.Draw is not null || entry.Bind is not null;
		if (expandable) {
			var chevron = open ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown;
			var chevronSize = Chrome.IconSize(chevron);
			Chrome.Icon(
				new Vector2(
					origin.X + width - (18f * s) - chevronSize.X,
					origin.Y + ((headerHeight - chevronSize.Y) * 0.5f)),
				chevron, hovered ? Theme.Dim : Theme.Faint);
		}

		var bottom = origin.Y + headerHeight;

		if (open && expandable) {
			draw.AddRectFilled(
				new Vector2(origin.X + (16f * s), bottom),
				new Vector2(origin.X + width - (16f * s), bottom + 1f), Theme.U(Theme.RuleHair));

			ImGui.SetCursorScreenPos(new Vector2(origin.X, bottom + (12f * s)));
			ImGui.Indent(16f * s);
			ImGui.PushID(entry.Title);

			ImGui.PushItemWidth(-(16f * s));

			if (entry.Bind?.Invoke() is { } editable && entry.BindName is not null) {
				if (KeybindPicker.Draw(entry.BindName, editable, entry.BindRepeats))
					Plugin.Config.Save();

				ImGui.Dummy(new Vector2(0f, 6f * s));
			}

			entry.Draw?.Invoke();
			ImGui.PopItemWidth();

			ImGui.PopID();
			ImGui.Unindent(16f * s);

			bottom = ImGui.GetCursorScreenPos().Y + (12f * s);
		}

		draw.ChannelsSetCurrent(0);
		var box = new Vector2(origin.X + width, bottom);
		draw.AddRectFilled(origin, box, Theme.U(open ? Theme.Panel : Theme.Field), 12f * s);

		if (hovered || open) {
			draw.AddRect(
				origin, box, Theme.U(open ? Theme.AccentAlpha(0.33f) : Theme.CardBorder), 12f * s,
				ImDrawFlags.None, 1f);
		}

		draw.ChannelsMerge();

		ImGui.SetCursorScreenPos(new Vector2(origin.X, bottom + (10f * s)));
	}

	private void DrawSettings() {
		var s = Chrome.Scale;

		Chrome.SectionLabel("Accent");
		ImGui.Dummy(new Vector2(0f, 6f * s));

		var swatch = 28f * s;
		var current = Plugin.Config.UiAccentRgb;

		for (var i = 0; i < Theme.Presets.Length; i++) {
			if (i > 0) ImGui.SameLine(0f, 10f * s);

			var (name, colour) = Theme.Presets[i];
			var rgb = Theme.ToRgb(colour);
			var origin = ImGui.GetCursorScreenPos();

			ImGui.InvisibleButton($"##accent{i}", new Vector2(swatch, swatch));
			if (ImGui.IsItemClicked()) {
				Plugin.Config.UiAccentRgb = rgb;
				Plugin.Config.Save();
			}

			var draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(origin, origin + new Vector2(swatch, swatch), Theme.U(colour), 8f * s);

			if (rgb == current) {
				Chrome.IconCentred(
					origin, new Vector2(swatch, swatch), FontAwesomeIcon.Check, Theme.OnColour(colour));
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(name);
		}

		ImGui.Dummy(new Vector2(0f, 8f * s));

		var free = new Vector3(
			((current >> 16) & 0xFF) / 255f,
			((current >> 8) & 0xFF) / 255f,
			(current & 0xFF) / 255f);

		ImGui.SetNextItemWidth(200f * s);
		if (ImGui.ColorEdit3("Anything else", ref free, ImGuiColorEditFlags.NoInputs)) {
			Plugin.Config.UiAccentRgb = Theme.ToRgb(new Vector4(free, 1f));
			Plugin.Config.Save();
		}

		ImGui.Dummy(new Vector2(0f, 18f * s));
		Chrome.SectionLabel("Window size");
		ImGui.Dummy(new Vector2(0f, 6f * s));

		var names = new[] { "Compact", "Standard", "Tall" };
		for (var i = 0; i < names.Length; i++) {
			if (i > 0) ImGui.SameLine(0f, 10f * s);
			if (ImGui.RadioButton(names[i], Plugin.Config.UiSize == i)) {
				Plugin.Config.UiSize = i;
				Plugin.Config.Save();
			}
		}

		ImGui.Dummy(new Vector2(0f, 18f * s));
		Chrome.SectionLabel("Text size");
		ImGui.Dummy(new Vector2(0f, 6f * s));

		if (this.pendingScale <= 0f)
			this.pendingScale = Plugin.Config.UiScale;

		ImGui.SetNextItemWidth(220f * s);
		ImGui.SliderFloat("##dsu_text", ref this.pendingScale, 0.8f, 1.6f, "%.2fx");

		if (ImGui.IsItemDeactivatedAfterEdit()) {
			Plugin.Config.UiScale = Math.Clamp(this.pendingScale, 0.8f, 1.6f);
			Plugin.Config.Save();
		}

		ImGui.SameLine(0f, 10f * s);
		if (ImGui.Button("Reset##dsu_text_reset")) {
			this.pendingScale = 1f;
			Plugin.Config.UiScale = 1f;
			Plugin.Config.Save();
		}

		ImGui.TextDisabled("The window grows with the text, so nothing runs out of its row.");

		ImGui.Dummy(new Vector2(0f, 18f * s));
		Chrome.SectionLabel("Diagnostics");
		ImGui.Dummy(new Vector2(0f, 6f * s));

		var verbose = Plugin.Verbose;
		if (Chrome.Toggle("verbose", ref verbose))
			Plugin.Verbose = verbose;

		ImGui.SameLine(0f, 12f * s);
		ImGui.TextUnformatted("Diagnostics to the Debug channel");
		ImGui.SameLine(0f, 8f * s);
		ImGui.TextDisabled("(/dsu debug)");

		if (verbose) {
			var withTools = this.tweaks.Where(t => t.Diagnostics is not null).ToList();

			ImGui.Dummy(new Vector2(0f, 10f * s));

			foreach (var tool in withTools) {
				if (ImGui.CollapsingHeader(tool.Title)) {
					ImGui.Indent(12f * s);
					tool.Diagnostics!();
					ImGui.Unindent(12f * s);
					ImGui.Dummy(new Vector2(0f, 4f * s));
				}
			}

			if (withTools.Count == 0)
				ImGui.TextDisabled("Nothing here has a debug surface yet.");
		}

		ImGui.Dummy(new Vector2(0f, 18f * s));
		Chrome.SectionLabel("Other keys");
		ImGui.Dummy(new Vector2(0f, 6f * s));

		var owned = this.tweaks
			.Where(t => t.BindName is not null)
			.Select(t => t.BindName!)
			.ToHashSet();

		this.drawKeybinds(owned);
	}
}
