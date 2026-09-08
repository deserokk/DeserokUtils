using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Dresser;

internal static unsafe class DresserList {

	private const int FirstEntry = 9;

	private const int Stride = 6;

	private const int MaxEntries = 400;

	public static int UnambiguousRowForIcon(uint icon) {
		if (icon == 0) return -1;

		var found = -1;
		var duplicate = -1;

		foreach (var (row, rowIcon) in Rows()) {
			if (rowIcon != icon) continue;

			if (found >= 0) {
				duplicate = row;
				break;
			}

			found = row;
		}

		if (duplicate < 0)
			return found;

		if (IconOwners(icon) <= 1) {
			DresserLog.Step(
				$"  icon {icon} is on rows {found} and {duplicate}, and only one item uses it "
				+ "— they are copies, taking the first");
			return found;
		}

		DresserLog.Step(
			$"  icon {icon} is on rows {found} and {duplicate}, and {IconOwners(icon)} different "
			+ "items share it — refusing to guess");
		return -1;
	}

	private static int IconOwners(uint icon) {
		owners ??= Build();
		return owners.TryGetValue(icon, out var n) ? n : 0;
	}

	private static Dictionary<uint, int>? owners;

	private static Dictionary<uint, int> Build() {
		var map = new Dictionary<uint, int>();
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
		if (sheet is null) return map;

		foreach (var item in sheet) {
			if (item.EquipSlotCategory.RowId == 0 || item.Icon == 0) continue;
			map[item.Icon] = map.TryGetValue(item.Icon, out var n) ? n + 1 : 1;
		}

		Plugin.Log.Information($"DresserList: {map.Count} icon(s) across the equippable items.");
		return map;
	}

	public static int RowForIcon(uint icon) {
		if (icon == 0) return -1;

		var rows = Rows();
		var found = -1;

		foreach (var (row, rowIcon) in rows) {
			if (rowIcon != icon) continue;

			if (found >= 0) {

				DresserLog.Trace($"  list: icon {icon} appears on rows {found} and {row}; taking {found}");
				break;
			}

			found = row;
		}

		return found;
	}

	public static List<(int Row, uint Icon, int Bag, int Slot)> Entries() {
		var rows = new List<(int, uint, int, int)>();

		var addon = Plugin.GameGui.GetAddonByName("MiragePrismPrismBoxCrystallize", 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return rows;

		var unit = (AtkUnitBase*)addon.Address;
		var count = unit->AtkValuesCount;

		for (var i = 0; i < MaxEntries; i++) {
			var at = FirstEntry + (i * Stride);
			if (at + 3 >= count) break;

			var icon = Number(unit->AtkValues[at + 1]);
			if (icon == 0) continue;

			rows.Add((
				(int)Number(unit->AtkValues[at]),
				icon,
				(int)Number(unit->AtkValues[at + 2]),
				(int)Number(unit->AtkValues[at + 3])));
		}

		return rows;
	}

	public static int RowForBagSlot(int bag, int slot) {
		foreach (var entry in Entries()) {
			if (entry.Bag == bag && entry.Slot == slot) return entry.Row;
		}

		return -1;
	}

	public static List<(int Row, uint Icon)> Rows() {
		var rows = new List<(int, uint)>();

		var addon = Plugin.GameGui.GetAddonByName("MiragePrismPrismBoxCrystallize", 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return rows;

		var unit = (AtkUnitBase*)addon.Address;
		var count = unit->AtkValuesCount;

		for (var i = 0; i < MaxEntries; i++) {
			var at = FirstEntry + (i * Stride);
			if (at + 1 >= count) break;

			var icon = Number(unit->AtkValues[at + 1]);

			if (icon == 0) continue;

			rows.Add(((int)Number(unit->AtkValues[at]), icon));
		}

		return rows;
	}

	public static void DumpRows() {
		var addon = Plugin.GameGui.GetAddonByName("MiragePrismPrismBoxCrystallize", 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) {
			Plugin.Chat.PrintError("[Dresser] the glamour-ready list is not open.");
			return;
		}

		var unit = (AtkUnitBase*)addon.Address;
		var count = unit->AtkValuesCount;
		var items = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();

		DresserLog.Step($"=== LIST DUMP: {count} value(s), stride {Stride} from {FirstEntry} ===");
		Plugin.Chat.Print($"[Dresser] dumping the list to dresser.log ({count} values).");

		for (var i = 0; i < MaxEntries; i++) {
			var at = FirstEntry + (i * Stride);
			if (at + Stride - 1 >= count) break;

			var icon = Number(unit->AtkValues[at + 1]);
			if (icon == 0) continue;

			var row = Number(unit->AtkValues[at]);
			var line = $"  row {row,3} icon {icon,6}";

			for (var f = 2; f < Stride; f++) {
				var value = Number(unit->AtkValues[at + f]);
				line += $" | v{f}={value}";

				if (value == 0 || items?.GetRowOrDefault(value) is not { } candidate) continue;

				var name = candidate.Name.ExtractText();
				if (name.Length == 0) continue;

				line += candidate.Icon == icon
					? $" <== ITEM ID? \"{name}\" icon {candidate.Icon} MATCHES"
					: $" (\"{name}\", icon {candidate.Icon})";
			}

			DresserLog.Step(line);
		}

		DresserLog.Step("  --- every value, index: type = value ---");
		for (var i = 0; i < count; i++) {
			var v = unit->AtkValues[i];
			var text = v.Type switch {
				AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString
					=> "\"" + v.String.ToString() + "\"",
				AtkValueType.UInt => v.UInt.ToString(),
				AtkValueType.Int => v.Int.ToString(),
				AtkValueType.Bool => v.Byte.ToString(),
				_ => "-",
			};

			if (text is "-" or "0")
				continue;

			DresserLog.Step($"    {i,4}: {v.Type,-14} = {text}");
		}

		DresserLog.Step("  --- what is in the bags, for comparison ---");

		var manager = InventoryManager.Instance();
		if (manager is not null) {
			var bags = new[] {
				InventoryType.Inventory1, InventoryType.Inventory2,
				InventoryType.Inventory3, InventoryType.Inventory4,
			};

			for (var b = 0; b < bags.Length; b++) {
				var page = manager->GetInventoryContainer(bags[b]);
				if (page is null || !page->IsLoaded) continue;

				for (var slot = 0; slot < page->Size; slot++) {
					var entry = page->GetInventorySlot(slot);
					if (entry is null || entry->ItemId == 0) continue;

					var id = entry->ItemId > 1_000_000 ? entry->ItemId - 1_000_000 : entry->ItemId;
					if (items?.GetRowOrDefault(id) is not { } it) continue;
					if (it.EquipSlotCategory.RowId == 0) continue;

					DresserLog.Step(
						$"    bag {b + 1} slot {slot,3}  icon {it.Icon,6}  id {id,6}  "
						+ $"\"{it.Name.ExtractText()}\"");
				}
			}
		}

		DresserLog.Step("=== LIST DUMP END ===");
	}

	private static uint Number(AtkValue v) => v.Type switch {
		AtkValueType.UInt => v.UInt,
		AtkValueType.Int => v.Int < 0 ? 0u : (uint)v.Int,
		_ => 0u,
	};
}
