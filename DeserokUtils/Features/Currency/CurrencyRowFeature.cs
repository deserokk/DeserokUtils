using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeserokUtils.Features.Currency;

internal sealed class CurrencyRowFeature: IDisposable {
	public string TabTitle => "Currency row";

	public string Summary => "Shows your currencies in a row instead of one at a time.";

	private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(500);

	private readonly Dictionary<uint, ISharedImmediateTexture?> icons = [];
	private readonly List<(uint ItemId, uint Icon, string Name, int Count)> held = [];

	private DateTime nextRead = DateTime.MinValue;

	private readonly EphemeralMarks.MarkFont font = new(EphemeralMarks.MarkFace.Axis);

	private uint lastSeenIcon;

	public CurrencyRowFeature() {
		Plugin.PluginInterface.UiBuilder.Draw += this.Draw;

		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "CurrencySetting", this.OnSettings);
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "CurrencySetting", this.OnSettings);
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CurrencySetting", this.OnSettings);

		Plugin.RegisterSub("addons", "list the game windows open right now, into sniff.log",
			(_, rest) => {
				if (rest.Trim().Length > 0) DumpAddon(rest.Trim());
				else ListAddons();
			});
	}

	private static unsafe void ListAddons() {
		var stage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance();
		if (stage is null) return;

		var units = stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
		var open = new List<string>();

		for (var i = 0; i < units.Count; i++) {
			var unit = units.Entries[i].Value;
			if (unit is null || !unit->IsVisible) continue;

			open.Add($"{unit->NameString} ({unit->X},{unit->Y})");
		}

		open.Sort(StringComparer.Ordinal);
		SniffLog.Mark($"OPEN WINDOWS - {open.Count}");
		foreach (var name in open) SniffLog.Write("  " + name);
		Plugin.Chat.Print($"[DeserokUtils] {open.Count} windows written to sniff.log.");
	}

	public void Dispose() {
		this.font.Dispose();
		Plugin.PluginInterface.UiBuilder.Draw -= this.Draw;
		Plugin.AddonLifecycle.UnregisterListener(this.OnSettings);
	}

	private void OnSettings(AddonEvent type, AddonArgs args) => this.ReadSettings();

	private static unsafe void DumpAddon(string name) {
		var addon = Plugin.GameGui.GetAddonByName(name);
		if (addon.Address == nint.Zero) {
			Plugin.Chat.PrintError($"[DeserokUtils] {name} isn't open.");
			return;
		}

		var unit = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon.Address;
		SniffLog.Mark($"ADDON {name} - {unit->AtkValuesCount} values, scale {unit->Scale}");

		for (var i = 0; i < unit->AtkValuesCount; i++) {
			var value = unit->AtkValues[i];
			var shown = value.Type switch {
				FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int => value.Int.ToString(),
				FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt => value.UInt.ToString(),
				FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool => value.Byte.ToString(),
				FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String or
					FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String8 or
					FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ManagedString
					=> value.String.ToString(),
				_ => string.Empty,
			};

			if (shown.Length > 0 && shown != "0") SniffLog.Write($"  [{i,3}] {value.Type} = {shown}");
		}

		var count = unit->UldManager.NodeListCount;
		SniffLog.Write($"  -- {count} nodes");

		for (var i = 0; i < count; i++) {
			var node = unit->UldManager.NodeList[i];
			if (node is null) continue;

			var extra = string.Empty;
			if (node->Type == FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Text) {
				var text = (FFXIVClientStructs.FFXIV.Component.GUI.AtkTextNode*)node;
				extra = $" font {text->FontSize} align {text->AlignmentType} text '{text->NodeText.ToString()}'";
			}
			else if (node->Type == FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Counter) {
				var counter = (FFXIVClientStructs.FFXIV.Component.GUI.AtkCounterNode*)node;
				extra = $" counter '{counter->NodeText.ToString()}' width {counter->CounterWidth} part {counter->NumberWidth}";
			}

			SniffLog.Write($"  node {i,2} id {node->NodeId,4} {node->Type} at ({node->X},{node->Y}) "
			             + $"size {node->Width}x{node->Height} scale {node->ScaleX:0.##} vis {node->IsVisible()}{extra}");
		}

		Plugin.Chat.Print($"[DeserokUtils] {name} written to sniff.log.");
	}

	private const int FirstSlot = 47;
	private const int Slots = 5;
	private const int FirstName = 24;
	private const int FirstIcon = 2;

	internal unsafe void ReadSettings() {
		var addon = Plugin.GameGui.GetAddonByName("CurrencySetting");
		if (addon.Address == nint.Zero) return;

		var unit = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon.Address;
		if (unit->AtkValues is null || unit->AtkValuesCount < FirstSlot + Slots) return;

		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
		var tracked = new List<CurrencyEntry>();

		for (var slot = 0; slot < Slots; slot++) {
			var option = unit->AtkValues[FirstSlot + slot].UInt;

			if (option == 0) continue;

			var nameAt = FirstName + (int)option;
			var iconAt = FirstIcon + (int)option - 1;
			if (nameAt >= unit->AtkValuesCount || iconAt >= unit->AtkValuesCount) continue;

			var name = unit->AtkValues[nameAt].String.ToString();
			var icon = (uint)unit->AtkValues[iconAt].Int;
			if (name.Length == 0 || icon == 0) continue;

			uint itemId = 0;
			foreach (var item in sheet) {
				if (!string.Equals(item.Name.ExtractText(), name, StringComparison.Ordinal)) continue;
				itemId = item.RowId;
				break;
			}

			if (itemId == 0) continue;
			tracked.Add(new CurrencyEntry { ItemId = itemId, Icon = icon, Name = name });
		}

		if (tracked.Count == 0) return;

		Plugin.Config.CurrencyTrackedList = tracked;
		Plugin.Config.Save();
		this.nextRead = DateTime.MinValue;

		if (Plugin.Verbose)
			Plugin.Chat.Print($"[DeserokUtils] currency rotation read: {string.Join(", ", tracked.Select(t => t.Name))}");
	}

	private unsafe void Read() {
		this.held.Clear();

		var showing = ShowingIcon();

		foreach (var entry in Plugin.Config.CurrencyTrackedList) {
			if (Plugin.Config.CurrencyHidden.Contains(entry.Icon)) continue;

			if (entry.Icon == showing) continue;

			this.held.Add((entry.ItemId, entry.Icon, entry.Name, Count(entry.ItemId)));
		}
	}

	private static unsafe uint ShowingIcon() {
		var addon = Plugin.GameGui.GetAddonByName("_Money");
		if (addon.Address == nint.Zero) return 0;

		var money = (FFXIVClientStructs.FFXIV.Client.UI.AddonMoney*)addon.Address;
		return money is null ? 0 : money->IconId;
	}

	private static unsafe int Count(uint itemId) {
		var manager = InventoryManager.Instance();
		return manager is null ? 0 : manager->GetInventoryItemCount(itemId);
	}

	private const float IconBox = 36f;
	private const float NumberHeight = 22f;

	private const float NumberGap = 2f;
	private const float EntryGap = 14f;

	private const float CellWidth = 10f;

	private void Draw() {
		if (!Plugin.Config.CurrencyRowEnabled) return;
		if (Plugin.PluginInterface.UiBuilder.CutsceneActive) return;

		var now = DateTime.UtcNow;
		if (now >= this.nextRead) {
			this.nextRead = now + Every;
			this.Read();
		}

		if (this.held.Count == 0) return;

		var addon = Plugin.GameGui.GetAddonByName("_Money");
		if (addon.Address == nint.Zero || !addon.IsVisible) return;

		unsafe {
			var unit = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon.Address;
			if (unit is null || !unit->IsVisible) return;

			var scale = unit->Scale * Plugin.Config.CurrencyRowScale;
			var icon = IconBox * scale;
			var height = NumberHeight * scale * Plugin.Config.CurrencyRowTextScale;

			var right = (float)addon.X;
			var top = addon.Y + ((36f * scale) - icon) / 2f + Plugin.Config.CurrencyRowOffsetY;

			var px = this.font.Prepare(height);
			using var locked = this.font.TryLock();
			var face = locked?.ImFont;

			var draw = ImGui.GetForegroundDrawList();

			foreach (var (_, iconId, name, count) in this.held) {
				var text = count.ToString("N0");
				var cell = CellWidth * scale;
				var size = new Vector2(text.Length * cell, height);

				var width = size.X + (NumberGap * scale) + icon;
				right -= width + (EntryGap * scale);

				var iconAt = Snap(new Vector2(right + size.X + (NumberGap * scale), top));
				var textAt = Snap(new Vector2(right, top + ((icon - size.Y) / 2f)));

				var shadow = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.9f));
				var white = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));

				for (var c = 0; c < text.Length; c++) {
					var glyph = text[c].ToString();

					float glyphWidth;
					if (face is { } measuring) {
						ImGui.PushFont(measuring);
						glyphWidth = ImGui.CalcTextSize(glyph).X;
						ImGui.PopFont();
					}
					else {
						glyphWidth = ImGui.CalcTextSize(glyph).X * (px / ImGui.GetFontSize());
					}

					var at = Snap(new Vector2(textAt.X + (c * cell) + ((cell - glyphWidth) / 2f), textAt.Y));

					if (face is { } drawn) {
						draw.AddText(drawn, px, at + new Vector2(1f, 1f), shadow, glyph);
						draw.AddText(drawn, px, at, white, glyph);
					}
					else {
						draw.AddText(at + new Vector2(1f, 1f), shadow, glyph);
						draw.AddText(at, white, glyph);
					}
				}

				if (this.Icon(iconId)?.GetWrapOrDefault() is { } texture) {
					var box = MathF.Round(icon);
					draw.AddImage(texture.Handle, iconAt, iconAt + new Vector2(box, box));
				}

				if (Plugin.Config.CurrencyRowTooltips
				    && ImGui.IsMouseHoveringRect(new Vector2(right, top), new Vector2(right + width, top + icon)))
					ImGui.SetTooltip($"{name}: {text}");
			}
		}
	}

	private static Vector2 Snap(Vector2 at) => new(MathF.Round(at.X), MathF.Round(at.Y));

	private ISharedImmediateTexture? Icon(uint iconId) {
		if (this.icons.TryGetValue(iconId, out var cached)) return cached;

		try {
			this.icons[iconId] = cached = Plugin.Textures.GetFromGameIcon(new GameIconLookup(iconId));
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, $"Currency row: icon {iconId} would not load.");
			this.icons[iconId] = cached = null;
		}

		return cached;
	}

	public void DrawTab() {
		var scale = Plugin.Config.CurrencyRowScale;
		ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8f);
		if (ImGui.SliderFloat("Size", ref scale, 0.5f, 2.5f, "%.1f")) Plugin.Config.CurrencyRowScale = scale;
		if (ImGui.IsItemDeactivatedAfterEdit()) Plugin.Config.Save();

		var text = Plugin.Config.CurrencyRowTextScale;
		ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8f);
		if (ImGui.SliderFloat("Number size", ref text, 0.25f, 1.5f, "%.2f")) Plugin.Config.CurrencyRowTextScale = text;
		if (ImGui.IsItemDeactivatedAfterEdit()) Plugin.Config.Save();

		var offset = Plugin.Config.CurrencyRowOffsetY;
		ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8f);
		if (ImGui.SliderFloat("Height", ref offset, -200f, 200f, "%.0f")) Plugin.Config.CurrencyRowOffsetY = offset;
		if (ImGui.IsItemDeactivatedAfterEdit()) Plugin.Config.Save();

		var tooltips = Plugin.Config.CurrencyRowTooltips;
		if (ImGui.Checkbox("Name on hover", ref tooltips)) {
			Plugin.Config.CurrencyRowTooltips = tooltips;
			Plugin.Config.Save();
		}

		ImGui.Spacing();

		if (Plugin.Config.CurrencyTrackedList.Count == 0) {
			ImGui.TextDisabled("Open the game's Currency Settings window once and your rotation appears here.");
			ImGui.TextDisabled("Left-click the gil widget, then the cog in the Currency window.");
			return;
		}

		ImGui.TextDisabled("Your rotation, as the game has it. Untick to leave one out of the row.");

		foreach (var entry in Plugin.Config.CurrencyTrackedList) {
			var shown = !Plugin.Config.CurrencyHidden.Contains(entry.Icon);
			if (!ImGui.Checkbox($"{entry.Name}##cur{entry.Icon}", ref shown)) continue;

			if (shown) Plugin.Config.CurrencyHidden.Remove(entry.Icon);
			else Plugin.Config.CurrencyHidden.Add(entry.Icon);

			Plugin.Config.Save();
			this.nextRead = DateTime.MinValue;
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Whichever one the game's widget is showing is skipped, so it never doubles up.");
	}

	public void DrawDiagnostics() {
		void Row(string label, bool ok, string detail) {
			ImGui.TextUnformatted(ok ? "PASS" : "no  ");
			ImGui.SameLine();
			ImGui.TextDisabled($"{label}  {detail}");
		}

		Row("feature on", Plugin.Config.CurrencyRowEnabled, string.Empty);

		var addon = Plugin.GameGui.GetAddonByName("_Money");
		var visible = addon.Address != nint.Zero && addon.IsVisible;
		Row("money widget on screen", visible, visible ? $"at {addon.X}, {addon.Y}" : "not found");

		Row("rotation read", Plugin.Config.CurrencyTrackedList.Count > 0,
			Plugin.Config.CurrencyTrackedList.Count > 0
				? string.Join(", ", Plugin.Config.CurrencyTrackedList.Select(e => e.Name))
				: "open Currency Settings once");

		Row("drawing", this.held.Count > 0, $"{this.held.Count} shown, widget has icon {ShowingIcon()}");
	}
}
