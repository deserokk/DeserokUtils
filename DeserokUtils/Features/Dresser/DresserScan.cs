using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

using Lumina.Excel.Sheets;

namespace DeserokUtils.Features.Dresser;

internal sealed unsafe class DresserScan {

	private const int PrismBoxSize = 800;

	private static readonly FFXIVClientStructs.FFXIV.Client.Game.InventoryType[] Bags = {
		FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1,
		FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory2,
		FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory3,
		FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4,
	};

	private const uint FromBags = uint.MaxValue;

	internal static readonly string[] SlotNames = {
		"Main hand", "Off hand", "Head", "Body", "Hands", "Legs",
		"Feet", "Earrings", "Necklace", "Bracelets", "Ring",
	};

	internal sealed record Addition(uint OutfitIndex, uint OutfitItemId, string OutfitName,
	                                List<(uint Index, uint ItemId, string Name, int Slot)> Pieces);

	internal sealed record NewOutfit(uint SetItemId, string SetName,
	                                 List<(uint Index, uint ItemId, string Name, int Slot)> Pieces);

	internal sealed record PackedOutfit(uint Index, uint ItemId, string Name,
	                                    List<(int Slot, string Item, bool Filled)> Slots);

	internal sealed record Duplicate(uint ItemId, string Name, List<uint> Indices);

	internal sealed class Result {
		public bool Loaded;
		public string? Problem;

		public int Used;
		public int Capacity = PrismBoxSize;

		public List<Addition> Additions = new();
		public List<NewOutfit> NewOutfits = new();
		public List<Duplicate> Duplicates = new();

		public List<PackedOutfit> Packed = new();

		public List<string> InUseByPlate = new();

		public int SkippedDyed;

		public int LooseInBags;

		public HashSet<uint> LoosePieceIds = new();

		public HashSet<uint> ArmoireRows = new();

		public List<(string Name, int Pieces)> FullyArmoireOutfits = new();

		public List<(uint Index, uint SetItemId, string Name, ushort Mask, int Pieces,
			List<(uint ItemId, uint CabinetRow, string Name)> Contents)> Dissolvable = new();

		public List<string> EmptyOutfits = new();

		public List<string> RedundantWithOutfit = new();

		public int BagSlotsFreed;

		public int OutfitsStarted;

		public List<string> ArmoireEligible = new();

		public List<(uint ItemId, uint CabinetRow, string Name)> ArmoireTransfer = new();

		public List<(uint ItemId, string Name)> StoreLoose = new();

		public List<string> ArmoireDuplicate = new();

		public List<(string Item, string Dye)> ExpensiveDyes = new();

		public int SlotsFromAdditions
			=> this.Additions.Sum(a => a.Pieces.Count(p => p.Index != uint.MaxValue));

		public int SlotsFromNewOutfits
			=> this.NewOutfits.Sum(o => o.Pieces.Count(p => p.Index != uint.MaxValue) - 1);

		public int SlotsFromDuplicates => this.Duplicates.Sum(d => d.Indices.Count - 1);

		public int SlotsRecoverable
			=> this.SlotsFromAdditions + this.SlotsFromNewOutfits + this.SlotsFromDuplicates;

		public int PrismsNeeded
			=> this.Additions.Sum(a => a.Pieces.Count) + this.NewOutfits.Sum(o => o.Pieces.Count);

		public int FreeSlotsNeeded {
			get {
				var a = this.Additions.Count == 0
					? 0
					: this.Additions.Max(x => x.Pieces.Count(p => p.Index != uint.MaxValue));

				var n = this.NewOutfits.Count == 0
					? 0
					: this.NewOutfits.Max(x => x.Pieces.Count(p => p.Index != uint.MaxValue));

				return Math.Max(a, n);
			}
		}
	}

	private static readonly string[] ExpensiveStainNames = { "Jet Black", "Pure White" };

	private static HashSet<uint>? expensiveStains;

	private static HashSet<uint> ExpensiveStains() {
		if (expensiveStains is not null) return expensiveStains;

		var found = new HashSet<uint>();
		var sheet = Plugin.Data.GetExcelSheet<Stain>(Dalamud.Game.ClientLanguage.English);

		if (sheet is not null) {
			foreach (var row in sheet) {
				var name = row.Name.ExtractText();
				if (ExpensiveStainNames.Contains(name, StringComparer.OrdinalIgnoreCase))
					found.Add(row.RowId);
			}
		}

		expensiveStains = found;
		return found;
	}

	private static HashSet<uint> PlateItems() {
		var used = new HashSet<uint>();

		var mirage = MirageManager.Instance();
		if (mirage is null) return used;

		var plates = mirage->GlamourPlates;
		for (var p = 0; p < plates.Length; p++) {
			var ids = plates[p].ItemIds;
			for (var i = 0; i < ids.Length; i++) {
				if (ids[i] == 0) continue;
				used.Add(ids[i] % 1000000);
			}
		}

		return used;
	}

	private Dictionary<uint, uint>? cabinet;

	private Dictionary<uint, uint> Cabinet() {
		if (this.cabinet is not null) return this.cabinet;

		var map = new Dictionary<uint, uint>();
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>();

		if (sheet is not null) {
			foreach (var row in sheet) {
				var itemId = row.Item.RowId;
				if (itemId != 0) map[itemId] = row.RowId;
			}
		}

		this.cabinet = map;
		return map;
	}

	private Dictionary<uint, List<(uint SetItemId, int Slot)>>? membership;

	private Dictionary<uint, List<(uint SetItemId, int Slot)>> Membership() {
		if (this.membership is not null) return this.membership;

		var map = new Dictionary<uint, List<(uint, int)>>();
		var sheet = Plugin.Data.GetExcelSheet<MirageStoreSetItem>();

		if (sheet is not null) {
			foreach (var row in sheet) {
				var slots = new[] {
					row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId,
					row.Hands.RowId, row.Legs.RowId, row.Feet.RowId, row.Earrings.RowId,
					row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId,
				};

				for (var slot = 0; slot < slots.Length; slot++) {
					var itemId = slots[slot];
					if (itemId == 0) continue;

					if (!map.TryGetValue(itemId, out var list))
						map[itemId] = list = new List<(uint, int)>();

					list.Add((row.RowId, slot));
				}
			}
		}

		this.membership = map;
		return map;
	}

	private static string ItemName(uint itemId) {
		var item = Plugin.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
		return item?.Name.ExtractText() ?? $"#{itemId}";
	}

	private static string StainName(uint stainId) {
		var stain = Plugin.Data.GetExcelSheet<Stain>()?.GetRowOrDefault(stainId);
		return stain?.Name.ExtractText() ?? $"dye #{stainId}";
	}

	public Result Scan() {
		var result = new Result();

		var mirage = MirageManager.Instance();
		if (mirage is null) {
			result.Problem = "Could not read the glamour dresser.";
			return result;
		}

		if (!mirage->PrismBoxLoaded) {
			result.Problem =
				"Open your glamour dresser once first, then run this again. "
				+ "The game only sends its contents when you look at it.";
			return result;
		}

		result.Loaded = true;

		var ids = mirage->PrismBoxItemIds;
		var stain0 = mirage->PrismBoxStain0Ids;
		var stain1 = mirage->PrismBoxStain1Ids;

		var sets = Plugin.Data.GetExcelSheet<MirageStoreSetItem>();
		var items = Plugin.Data.GetExcelSheet<Item>();
		var membership = this.Membership();
		var plateItems = PlateItems();
		var cabinet = this.Cabinet();

		if (UIState.Instance()->Cabinet.IsCabinetLoaded()) {
			foreach (var (_, cabinetRow) in cabinet) {
				if (UIState.Instance()->Cabinet.IsItemInCabinet(cabinetRow))
					result.ArmoireRows.Add(cabinetRow);
			}
		}

		var outfits = new List<(uint Index, uint ItemId)>();
		var loose = new List<(uint Index, uint ItemId)>();

		for (var i = 0u; i < PrismBoxSize && i < ids.Length; i++) {
			var itemId = ids[(int)i];
			if (itemId == 0) continue;

			result.Used++;

			if (sets?.GetRowOrDefault(itemId) is not null) {
				outfits.Add((i, itemId));
			}
			else if (cabinet.TryGetValue(itemId, out var cabinetRow)) {
				result.LoosePieceIds.Add(itemId);

				if (UIState.Instance()->Cabinet.IsItemInCabinet(cabinetRow))
					result.ArmoireDuplicate.Add(ItemName(itemId));
				else
				{
					result.ArmoireEligible.Add(ItemName(itemId));
					result.ArmoireTransfer.Add((itemId, cabinetRow, ItemName(itemId)));
				}
			}
			else {
				result.LoosePieceIds.Add(itemId);

				if (plateItems.Contains(itemId)) result.InUseByPlate.Add(ItemName(itemId));

				if (Plugin.Config.DresserSkipDyed
				    && (stain0[(int)i] != 0 || stain1[(int)i] != 0)) {
					result.SkippedDyed++;
				}
				else {
					loose.Add((i, itemId));
				}
			}

			var expensive = ExpensiveStains();
			foreach (var stain in new uint[] { stain0[(int)i], stain1[(int)i] }) {
				if (stain != 0 && expensive.Contains(stain))
					result.ExpensiveDyes.Add((ItemName(itemId), StainName(stain)));
			}
		}

		var manager = InventoryManager.Instance();
		if (manager is not null) {
			foreach (var bag in Bags) {
				var page = manager->GetInventoryContainer(bag);
				if (page is null || !page->IsLoaded) continue;

				for (var slot = 0; slot < page->Size; slot++) {
					var item = page->GetInventorySlot(slot);
					if (item is null || item->ItemId == 0) continue;

					if (cabinet.TryGetValue(item->ItemId, out var bagCabinetRow)
					    && !UIState.Instance()->Cabinet.IsItemInCabinet(bagCabinetRow)) {
						result.ArmoireTransfer.Add(
							(item->ItemId, bagCabinetRow, ItemName(item->ItemId)));
					}

					if (!membership.ContainsKey(item->ItemId)) continue;

					if (sets?.GetRowOrDefault(item->ItemId) is not null) continue;

					loose.Add((FromBags, item->ItemId));
					result.LooseInBags++;
				}
			}
		}

		var byItem = loose.GroupBy(x => x.ItemId).ToList();

		foreach (var group in byItem.Where(g => g.Count(x => x.Index != FromBags) > 1)) {
			result.Duplicates.Add(new Duplicate(
				group.Key, ItemName(group.Key),
				group.Where(x => x.Index != FromBags).Select(x => x.Index).ToList()));
		}

		var unique = byItem.Select(g => g.First()).ToList();

		var claimed = new HashSet<uint>();

		foreach (var (outfitIndex, outfitItemId) in outfits) {
			var pieces = new List<(uint, uint, string, int)>();

			foreach (var (index, itemId) in unique) {
				if (claimed.Contains(index)) continue;
				if (!membership.TryGetValue(itemId, out var belongsTo)) continue;

				foreach (var (setItemId, slot) in belongsTo) {
					if (setItemId != outfitItemId) continue;

					if (MirageManager.MemberFunctionPointers.IsSetSlotUnlocked(
						    mirage, outfitIndex, slot))
						continue;

					pieces.Add((index, itemId, ItemName(itemId), slot));
					claimed.Add(index);
					break;
				}
			}

			if (pieces.Count > 0)
				result.Additions.Add(new Addition(
					outfitIndex, outfitItemId, ItemName(outfitItemId), pieces));

			if (sets?.GetRowOrDefault(outfitItemId) is { } setRow) {
				var slotItems = new[] {
					setRow.MainHand.RowId, setRow.OffHand.RowId, setRow.Head.RowId, setRow.Body.RowId,
					setRow.Hands.RowId, setRow.Legs.RowId, setRow.Feet.RowId, setRow.Earrings.RowId,
					setRow.Necklace.RowId, setRow.Bracelets.RowId, setRow.Ring.RowId,
				};

				var slots = new List<(int, string, bool)>();
				for (var slot = 0; slot < slotItems.Length; slot++) {
					if (slotItems[slot] == 0) continue;
					slots.Add((slot, ItemName(slotItems[slot]),
						MirageManager.MemberFunctionPointers.IsSetSlotUnlocked(
							mirage, outfitIndex, slot)));
				}

				result.Packed.Add(new PackedOutfit(
					outfitIndex, outfitItemId, ItemName(outfitItemId), slots));

				var filledCount = 0;
				var allEligible = true;

				for (var slot = 0; slot < slotItems.Length; slot++) {
					if (slotItems[slot] == 0) continue;
					if (!MirageManager.MemberFunctionPointers.IsSetSlotUnlocked(
						    mirage, outfitIndex, slot)) continue;

					filledCount++;
					if (!cabinet.ContainsKey(slotItems[slot])) allEligible = false;
				}

				if (allEligible && filledCount > 0) {
					result.FullyArmoireOutfits.Add((ItemName(outfitItemId), filledCount));

					ushort mask = 0;

					var contents = new List<(uint, uint, string)>();

					for (var slot = 0; slot < slotItems.Length; slot++) {
						if (slotItems[slot] == 0) continue;
						if (!MirageManager.MemberFunctionPointers.IsSetSlotUnlocked(
							    mirage, outfitIndex, slot)) continue;

						mask |= (ushort)(1 << slot);

						if (cabinet.TryGetValue(slotItems[slot], out var pieceRow))
							contents.Add((slotItems[slot], pieceRow, ItemName(slotItems[slot])));
					}

					result.Dissolvable.Add(
						(outfitIndex, outfitItemId, ItemName(outfitItemId), mask, filledCount,
							contents));
				}

				if (slots.Count > 0 && slots.TrueForAll(x => !x.Item3))
					result.EmptyOutfits.Add(ItemName(outfitItemId));
			}
		}

		var alreadyPacked = new HashSet<uint>(outfits.Select(o => o.ItemId));

		var grouped = new Dictionary<uint, List<(uint, uint, string, int)>>();

		foreach (var (index, itemId) in unique) {
			if (claimed.Contains(index)) continue;
			if (!membership.TryGetValue(itemId, out var belongsTo)) continue;

			var choice = belongsTo.Find(b => !alreadyPacked.Contains(b.SetItemId));

			if (choice.SetItemId == 0) {

				result.RedundantWithOutfit.Add(ItemName(itemId));
				continue;
			}

			if (!grouped.TryGetValue(choice.SetItemId, out var list))
				grouped[choice.SetItemId] = list = new List<(uint, uint, string, int)>();

			list.Add((index, itemId, ItemName(itemId), choice.Slot));
		}

		foreach (var (setItemId, pieces) in grouped) {
			result.NewOutfits.Add(new NewOutfit(setItemId, ItemName(setItemId), pieces));
			if (pieces.Count == 1) result.OutfitsStarted++;
		}

		foreach (var a in result.Additions) {
			foreach (var p in a.Pieces) {
				if (p.Index == FromBags) result.BagSlotsFreed++;
			}
		}

		foreach (var o in result.NewOutfits) {
			foreach (var p in o.Pieces) {
				if (p.Index == FromBags) result.BagSlotsFreed++;
			}
		}

		var owned = new HashSet<uint>(result.LoosePieceIds);

		foreach (var outfit in result.Packed) {
			foreach (var (slot, _, filled) in outfit.Slots) {
				if (!filled) continue;
				if (sets?.GetRowOrDefault(outfit.ItemId) is not { } row) continue;

				var columns = new[] {
					row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId,
					row.Hands.RowId, row.Legs.RowId, row.Feet.RowId, row.Earrings.RowId,
					row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId,
				};

				if (slot < columns.Length && columns[slot] != 0) owned.Add(columns[slot]);
			}
		}

		foreach (var (itemId, cabinetRow) in cabinet) {
			if (result.ArmoireRows.Contains(cabinetRow)) owned.Add(itemId);
		}

		foreach (var a in result.Additions) {
			foreach (var piece in a.Pieces) owned.Add(piece.ItemId);
		}

		foreach (var o in result.NewOutfits) {
			foreach (var piece in o.Pieces) owned.Add(piece.ItemId);
		}

		var manager2 = InventoryManager.Instance();
		if (manager2 is not null) {
			var seen = new HashSet<uint>();

			foreach (var bag in Bags) {
				var page = manager2->GetInventoryContainer(bag);
				if (page is null || !page->IsLoaded) continue;

				for (var slot = 0; slot < page->Size; slot++) {
					var item = page->GetInventorySlot(slot);
					if (item is null || item->ItemId == 0) continue;

					var itemId = item->ItemId > 1_000_000 ? item->ItemId - 1_000_000 : item->ItemId;
					if (owned.Contains(itemId) || !seen.Add(itemId)) continue;

					if (items?.GetRowOrDefault(itemId) is not { } row2) continue;
					if (row2.EquipSlotCategory.RowId == 0 || row2.ItemUICategory.RowId == 62) continue;

					result.StoreLoose.Add((itemId, ItemName(itemId)));
				}
			}
		}

		result.Additions = result.Additions.OrderByDescending(a => a.Pieces.Count).ToList();
		result.NewOutfits = result.NewOutfits.OrderByDescending(o => o.Pieces.Count).ToList();
		result.Duplicates = result.Duplicates.OrderByDescending(d => d.Indices.Count).ToList();

		return result;
	}
}
