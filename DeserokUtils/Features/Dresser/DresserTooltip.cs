using System.Collections.Generic;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Excel.Sheets;

namespace DeserokUtils.Features.Dresser;

internal sealed unsafe class DresserTooltip {

	private const uint OurNodeId = 0x44535501;

	private const uint InsertBesideNode = 2;

	private const uint StyleFromNode = 44;

	private const ushort OwnedColour = 43;

	private const ushort WantedColour = 31;

	public void Listen() {
		Plugin.AddonLifecycle.RegisterListener(
			AddonEvent.PreRequestedUpdate, "ItemDetail", this.BeforeUpdate);
		Plugin.AddonLifecycle.RegisterListener(
			AddonEvent.PostRequestedUpdate, "ItemDetail", this.AfterUpdate);
	}

	public void Dispose() {
		Plugin.AddonLifecycle.UnregisterListener(
			AddonEvent.PreRequestedUpdate, "ItemDetail", this.BeforeUpdate);
		Plugin.AddonLifecycle.UnregisterListener(
			AddonEvent.PostRequestedUpdate, "ItemDetail", this.AfterUpdate);

		RemoveNode();
	}

	private void BeforeUpdate(AddonEvent type, AddonArgs args) {
		var addon = (AtkUnitBase*)args.Addon.Address;
		if (addon is null || addon->WindowNode is null) return;

		var node = FindNode(addon);
		if (node is null || !node->AtkResNode.IsVisible()) return;

		var insert = addon->GetNodeById(InsertBesideNode);
		if (insert is null) return;

		addon->WindowNode->AtkResNode.SetHeight(
			(ushort)(addon->WindowNode->AtkResNode.Height - node->AtkResNode.Height));

		var inner = addon->WindowNode->Component->UldManager.SearchNodeById(InsertBesideNode);
		if (inner is not null) inner->SetHeight(addon->WindowNode->AtkResNode.Height);

		insert->SetPositionFloat(insert->X, insert->Y - node->AtkResNode.Height);
	}

	private void AfterUpdate(AddonEvent type, AddonArgs args) {
		if (!Plugin.Config.DresserTooltip) return;

		var addon = (AtkUnitBase*)args.Addon.Address;
		if (addon is null || addon->WindowNode is null) return;

		var existing = FindNode(addon);
		if (existing is not null) existing->AtkResNode.ToggleVisibility(false);

		var agent = AgentItemDetail.Instance();
		if (agent is null) return;

		var itemId = DresserCache.PureItemId(agent->ItemId);
		if (itemId == 0) return;

		var note = Note(itemId);
		if (note.Payloads.Count == 0) return;

		var insert = addon->GetNodeById(InsertBesideNode);
		if (insert is null) return;

		var template = addon->GetTextNodeById(StyleFromNode);
		if (template is null) return;

		var node = existing is not null ? existing : Create(addon, insert, template);
		if (node is null) return;

		node->AtkResNode.ToggleVisibility(true);
		node->SetText(note.EncodeWithNullTerminator());
		node->ResizeNodeForCurrentText();

		node->AtkResNode.SetPositionFloat(17f, insert->Y);

		addon->WindowNode->AtkResNode.SetHeight(
			(ushort)(addon->WindowNode->AtkResNode.Height + node->AtkResNode.Height));

		var inner = addon->WindowNode->Component->UldManager.SearchNodeById(InsertBesideNode);
		if (inner is not null) inner->SetHeight(addon->WindowNode->AtkResNode.Height);

		insert->SetPositionFloat(insert->X, insert->Y + node->AtkResNode.Height);
	}

	private static AtkTextNode* Create(AtkUnitBase* addon, AtkResNode* insert, AtkTextNode* template) {
		var node = IMemorySpace.GetUISpace()->Create<AtkTextNode>();
		if (node is null) return null;

		node->AtkResNode.Type = NodeType.Text;
		node->AtkResNode.NodeId = OurNodeId;
		node->AtkResNode.NodeFlags = NodeFlags.AnchorLeft | NodeFlags.AnchorTop;
		node->AtkResNode.DrawFlags = 0;
		node->AtkResNode.SetWidth(50);
		node->AtkResNode.SetHeight(20);

		node->AtkResNode.Color = template->AtkResNode.Color;
		node->TextColor = template->TextColor;
		node->EdgeColor = template->EdgeColor;

		node->LineSpacing = 18;
		node->AlignmentFontType = 0x00;
		node->FontSize = 12;
		node->TextFlags = template->TextFlags | TextFlags.MultiLine | TextFlags.AutoAdjustNodeSize;

		var prev = insert->PrevSiblingNode;
		node->AtkResNode.ParentNode = insert->ParentNode;
		insert->PrevSiblingNode = (AtkResNode*)node;
		if (prev is not null) prev->NextSiblingNode = (AtkResNode*)node;
		node->AtkResNode.PrevSiblingNode = prev;
		node->AtkResNode.NextSiblingNode = insert;

		addon->UldManager.UpdateDrawNodeList();
		return node;
	}

	private static AtkTextNode* FindNode(AtkUnitBase* addon) {
		if (addon is null) return null;

		for (var i = 0; i < addon->UldManager.NodeListCount; i++) {
			var node = addon->UldManager.NodeList[i];
			if (node is null || node->NodeId != OurNodeId || node->Type != NodeType.Text) continue;

			return (AtkTextNode*)node;
		}

		return null;
	}

	private static void RemoveNode() {
		var addon = Plugin.GameGui.GetAddonByName("ItemDetail", 1);
		if (addon.Address == nint.Zero) return;

		var unit = (AtkUnitBase*)addon.Address;
		var node = FindNode(unit);
		if (node is null) return;

		if (node->AtkResNode.PrevSiblingNode is not null)
			node->AtkResNode.PrevSiblingNode->NextSiblingNode = node->AtkResNode.NextSiblingNode;

		if (node->AtkResNode.NextSiblingNode is not null)
			node->AtkResNode.NextSiblingNode->PrevSiblingNode = node->AtkResNode.PrevSiblingNode;

		unit->UldManager.UpdateDrawNodeList();
		node->AtkResNode.Destroy(true);
	}

	private static SeString Note(uint itemId) {
		var lines = new SeString();

		var cache = DresserCache.Current;
		if (cache is null) return lines;

		if (!IsAppearance(itemId)) return lines;

		var owned = Owned(cache, itemId);
		var wanted = Wanted(cache, itemId);

		if (owned is { } have) {
			lines.Payloads.Add(new UIForegroundPayload(OwnedColour));
			lines.Payloads.Add(new TextPayload($"\u2713 You have this appearance — {have.Where}"));
			lines.Payloads.Add(new UIForegroundPayload(0));

			if (have.Set is { } set) Detail(lines, set, OwnedColour);
		}
		else if (wanted is { } need) {
			lines.Payloads.Add(new IconPayload(BitmapFontIcon.Warning));
			lines.Payloads.Add(new UIForegroundPayload(WantedColour));

			lines.Payloads.Add(new TextPayload(
				need.Completes ? " Not in your dresser — completes a set!" : " You need this for an outfit"));
			lines.Payloads.Add(new UIForegroundPayload(0));

			Detail(lines, need.Set, WantedColour);
		}
		else {

			lines.Payloads.Add(new TextPayload(
				$"{SeIconChar.Cross.ToIconString()} You do not have this appearance"));
		}

		if (lines.Payloads.Count > 0 && cache.MaybeStale) {
			lines.Payloads.Add(new NewLinePayload());
			lines.Payloads.Add(new UIForegroundPayload(3));
			lines.Payloads.Add(new TextPayload("  dresser changed since the last scan"));
			lines.Payloads.Add(new UIForegroundPayload(0));
		}

		return lines;
	}

	private static void Detail(SeString lines, (string Name, int Have, int Total) set, ushort colour) {
		lines.Payloads.Add(new NewLinePayload());
		lines.Payloads.Add(new UIForegroundPayload(colour));
		lines.Payloads.Add(new TextPayload($"  {Fit(set.Name)} ({set.Have}/{set.Total})"));
		lines.Payloads.Add(new UIForegroundPayload(0));
	}

	private static string Fit(string name) {

		const int max = 44;
		if (name.Length <= max) return name;

		var cut = name.LastIndexOf(' ', max - 1);
		if (cut < max - 12) cut = max - 1;

		return name[..cut].TrimEnd() + "…";
	}

	private static (string Where, (string Name, int Have, int Total)? Set)? Owned(
		DresserCache cache, uint itemId) {

		if (cache.LoosePieces.Contains(itemId)) return ("Glamour Dresser", null);

		foreach (var (setItemId, slot) in Sets(itemId)) {
			if (!cache.OutfitSlots.TryGetValue(setItemId, out var filled)) continue;
			if (!filled.Contains(slot)) continue;

			return ("an outfit",
				(ItemName(setItemId), Progress(cache, setItemId).Count, SlotCount(setItemId)));
		}

		if (CabinetByItem().TryGetValue(itemId, out var row) && cache.Armoire.Contains(row))
			return ("Armoire", null);

		if (Worn(itemId)) return ("Equipped", null);
		if (InArmoury(itemId)) return ("Armoury Chest", null);

		return null;
	}

	private static bool Worn(uint itemId) => Holds(InventoryType.EquippedItems, itemId);

	private static bool InArmoury(uint itemId) {
		foreach (var bag in Armoury) {
			if (Holds(bag, itemId)) return true;
		}

		return false;
	}

	private static bool Holds(InventoryType type, uint itemId) {
		var manager = InventoryManager.Instance();
		if (manager is null) return false;

		var page = manager->GetInventoryContainer(type);
		if (page is null || !page->IsLoaded) return false;

		for (var i = 0; i < page->Size; i++) {
			var item = page->GetInventorySlot(i);

			if (item is not null && DresserCache.PureItemId(item->ItemId) == itemId) return true;
		}

		return false;
	}

	private static readonly InventoryType[] Armoury = {
		InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
		InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
		InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
		InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
	};

	private static ((string Name, int Have, int Total) Set, bool Completes)? Wanted(
		DresserCache cache, uint itemId) {
		foreach (var (setItemId, slot) in Sets(itemId)) {
			var have = Progress(cache, setItemId);

			if (have.Contains(slot)) continue;

			if (have.Count == 0) continue;

			var total = SlotCount(setItemId);

			return ((ItemName(setItemId), have.Count, total), have.Count + 1 == total);
		}

		return null;
	}

	private static HashSet<int> Progress(DresserCache cache, uint setItemId) {
		var slots = new HashSet<int>();

		if (cache.OutfitSlots.TryGetValue(setItemId, out var filled)) {
			foreach (var slot in filled) slots.Add(slot);
		}

		if (Plugin.Data.GetExcelSheet<MirageStoreSetItem>()?.GetRowOrDefault(setItemId)
			is not { } row) return slots;

		var columns = Columns(row);
		for (var slot = 0; slot < columns.Length; slot++) {
			var piece = columns[slot];
			if (piece == 0 || slots.Contains(slot)) continue;

			if (cache.LoosePieces.Contains(piece)) slots.Add(slot);
		}

		return slots;
	}

	private static Dictionary<uint, List<(uint SetItemId, int Slot)>>? setsByPiece;
	private static Dictionary<uint, uint>? cabinetByItem;
	private static Dictionary<uint, int>? slotCounts;

	private static List<(uint SetItemId, int Slot)> Sets(uint itemId)
		=> SetsByPiece().TryGetValue(itemId, out var sets)
			? sets
			: new List<(uint, int)>();

	private static Dictionary<uint, List<(uint SetItemId, int Slot)>> SetsByPiece() {
		if (setsByPiece is not null) return setsByPiece;

		var map = new Dictionary<uint, List<(uint, int)>>();
		var sheet = Plugin.Data.GetExcelSheet<MirageStoreSetItem>();

		if (sheet is not null) {
			foreach (var row in sheet) {
				var columns = Columns(row);
				for (var slot = 0; slot < columns.Length; slot++) {
					var piece = columns[slot];
					if (piece == 0) continue;

					if (!map.TryGetValue(piece, out var list))
						map[piece] = list = new List<(uint, int)>();

					list.Add((row.RowId, slot));
				}
			}
		}

		setsByPiece = map;
		return map;
	}

	private static Dictionary<uint, uint> CabinetByItem() {
		if (cabinetByItem is not null) return cabinetByItem;

		var map = new Dictionary<uint, uint>();
		var sheet = Plugin.Data.GetExcelSheet<Cabinet>();

		if (sheet is not null) {
			foreach (var row in sheet) {
				if (row.Item.RowId != 0) map[row.Item.RowId] = row.RowId;
			}
		}

		cabinetByItem = map;
		return map;
	}

	private static int SlotCount(uint setItemId) {
		slotCounts ??= new Dictionary<uint, int>();
		if (slotCounts.TryGetValue(setItemId, out var known)) return known;

		var count = 0;
		if (Plugin.Data.GetExcelSheet<MirageStoreSetItem>()?.GetRowOrDefault(setItemId) is { } row) {
			foreach (var id in Columns(row)) {
				if (id != 0) count++;
			}
		}

		slotCounts[setItemId] = count;
		return count;
	}

	private static uint[] Columns(MirageStoreSetItem row) => new[] {
		row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId,
		row.Hands.RowId, row.Legs.RowId, row.Feet.RowId, row.Earrings.RowId,
		row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId,
	};

	private static bool IsAppearance(uint itemId) {
		if (Plugin.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId) is not { } item) return false;

		return item.EquipSlotCategory.RowId != 0 && item.ItemUICategory.RowId != 62;
	}

	private static string ItemName(uint itemId)
		=> Plugin.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"#{itemId}";
}
