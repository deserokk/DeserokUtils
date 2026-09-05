using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

using Lumina.Excel.Sheets;

namespace DeserokUtils.Features.Dresser;

/// <summary>
/// Reads the glamour dresser and works out what could be packed away.
///
/// ⭐⭐ EVERYTHING HERE IS A READ. No callbacks, no UI driving, nothing consumed. The scan is
/// arithmetic over two tables : the dresser's contents in memory, and the game's own sheet of which
/// items belong to which outfit. It cannot go wrong, which is why it ships before the packing half.
///
/// ⚠ The one query that is a function call rather than a field read is
/// <c>IsSetSlotUnlocked</c>, and it is a pure question with no side effect.
///
/// ## The vocabulary, because the game overloads both words
///
///  - **set**    : the eleven slots an outfit COULD hold, from MirageStoreSetItem.
///  - **outfit** : a packed dresser entry. ⚠ It does NOT have to be complete : a real one was
///                 observed holding four of nine, with the rest greyed out. That single fact is
///                 what makes this tool worth building, because it means partial packing counts.
///  - Neither is a glamour PLATE, which is a different feature entirely.
/// </summary>
internal sealed unsafe class DresserScan {
	/// <summary>The dresser is 800 entries; see MirageManager._prismBoxItemIds.</summary>
	private const int PrismBoxSize = 800;

	/// <summary>
	/// ⚠ The four ordinary bags only. Never the armoury, never equipped gear — offering to pack
	/// away something somebody is wearing is not a thing to do, and gear filed in the armoury is
	/// filed deliberately.
	/// </summary>
	private static readonly FFXIVClientStructs.FFXIV.Client.Game.InventoryType[] Bags = {
		FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1,
		FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory2,
		FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory3,
		FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4,
	};

	/// <summary>Where a loose piece currently lives. ⭐ The packer needs no distinction — it looks
	/// in the bags first anyway, so a piece already there simply skips the restore.</summary>
	private const uint FromBags = uint.MaxValue;

	/// <summary>MirageStoreSetItem has eleven slot columns, in this order.</summary>
	internal static readonly string[] SlotNames = {
		"Main hand", "Off hand", "Head", "Body", "Hands", "Legs",
		"Feet", "Earrings", "Necklace", "Bracelets", "Ring",
	};

	/// <summary>A loose piece that could join an outfit already in the dresser.</summary>
	internal sealed record Addition(uint OutfitIndex, uint OutfitItemId, string OutfitName,
	                                List<(uint Index, uint ItemId, string Name, int Slot)> Pieces);

	/// <summary>Loose pieces of a set with no packed outfit yet.</summary>
	internal sealed record NewOutfit(uint SetItemId, string SetName,
	                                 List<(uint Index, uint ItemId, string Name, int Slot)> Pieces);

	/// <summary>
	/// A packed outfit and which of its slots are filled.
	///
	/// ⭐ deserok asked for this directly : an outfit holding four of nine is worth knowing about
	/// even when you own none of the missing pieces, because it tells you what to look out for.
	///
	/// ⚠⚠ It also verifies the one assumption in this file I could not check from documentation.
	/// <c>IsSetSlotUnlocked</c> could plausibly mean "this slot holds something" or "this slot is
	/// available to fill", and those are opposites : get it backwards and the tool offers to add
	/// pieces that are already packed. Printing filled-versus-missing against a real outfit settles
	/// it by eye in one run.
	/// </summary>
	internal sealed record PackedOutfit(uint Index, uint ItemId, string Name,
	                                    List<(int Slot, string Item, bool Filled)> Slots);

	/// <summary>The same item sitting in the dresser more than once.</summary>
	internal sealed record Duplicate(uint ItemId, string Name, List<uint> Indices);

	internal sealed class Result {
		public bool Loaded;
		public string? Problem;

		public int Used;
		public int Capacity = PrismBoxSize;

		public List<Addition> Additions = new();
		public List<NewOutfit> NewOutfits = new();
		public List<Duplicate> Duplicates = new();

		/// <summary>Every packed outfit, with what it holds and what it lacks.</summary>
		public List<PackedOutfit> Packed = new();

		/// <summary>Pieces a glamour plate uses. ⚠ Still packable — the game just asks first.</summary>
		public List<string> InUseByPlate = new();

		/// <summary>Dyed pieces left alone because the user asked for that.</summary>
		public int SkippedDyed;

		/// <summary>Loose pieces found in the bags rather than the dresser.</summary>
		public int LooseInBags;

		/// <summary>
		/// Every loose piece in the dresser, by item id, whatever the packing made of it.
		///
		/// ⚠ DELIBERATELY WIDER than the packing lists. Those answer "what is worth doing"; this one
		/// answers "do you own this", and a piece that is unpackable, dyed, or already spoken for is
		/// still a piece you own. Narrowing it to the packable ones would make the tooltip lie by
		/// omission about exactly the items somebody is most likely to be looking at.
		/// </summary>
		public HashSet<uint> LoosePieceIds = new();

		/// <summary>Armoire rows already stored. ⚠ Cabinet row ids, not item ids.</summary>
		public HashSet<uint> ArmoireRows = new();

		/// <summary>
		/// Packed outfits where the Armoire would take EVERY piece.
		///
		/// ⭐⭐⭐ THE ONLY CASE WHERE UNPACKING PAYS, and deserok says it is not rare: *"every piece
		/// of the outfits are eligible, that's kind of the thing — right now I know every vanguard set
		/// is consuming a slot, when they're armoire capable, that's the example I know at a glance.
		/// So there's 6 right there."*
		///
		/// ⚠ One eligible piece inside an outfit is worth nothing — the outfit entry still holds its
		/// slot whether or not that piece leaves. It only pays when the whole thing can go, turning
		/// one dresser slot into zero. Which is why this counts outfits rather than pieces: the
		/// piece-level number would suggest work that does not exist.
		///
		/// ⚠ Reported only. Unpacking is recorded but unproven — see DeserokUtils.md — and this list
		/// is what says whether proving it is worth anybody's evening.
		/// </summary>
		public List<(string Name, int Pieces)> FullyArmoireOutfits = new();

		/// <summary>
		/// Outfits in the dresser holding nothing at all.
		///
		/// ⚠⚠ THESE ARE DAMAGE, and this tool made them. A run on 2026-09-03 pressed "Store as
		/// Glamour" repeatedly on a dialog that had nothing selected, committing empty outfits — and
		/// an empty one is a trap: removing an outfit means restoring an item out of it, and there is
		/// no item in it. See DresserPacker.storePressed for the fix.
		///
		/// ⭐ Reported rather than hidden, because there is something to do about them: put a piece
		/// of that set in, and the entry becomes a real outfit that every later piece can join. The
		/// packing does exactly that on its own once you loot one.
		/// </summary>
		public List<string> EmptyOutfits = new();

		/// <summary>
		/// Loose pieces whose set is already packed AND whose slot in it is already filled.
		///
		/// ⭐ A second copy of something the outfit already holds. Not packable — there is nowhere
		/// for it to go — so it is a sell-or-desynth candidate rather than a job.
		/// </summary>
		public List<string> RedundantWithOutfit = new();

		/// <summary>
		/// Bag slots the packing would free.
		///
		/// ⚠⚠ A SEPARATE figure from dresser slots, and they must not be added together. Packing a
		/// piece that was in the dresser recovers a dresser slot; packing one from the bags frees a
		/// bag slot and may even COST a dresser slot, when every piece of a new outfit came from the
		/// bags — nothing was in the dresser and now one entry is. That is still worth doing (it is
		/// filing loot away rather than reclaiming space) but calling it "recovered" would be a lie.
		/// </summary>
		public int BagSlotsFreed;

		/// <summary>
		/// Outfits that would be created from a single piece.
		///
		/// ⚠ These recover nothing now — one slot in, one slot out. Their value is entirely future:
		/// they turn the next piece of that set into a free slot rather than a fragment.
		/// </summary>
		public int OutfitsStarted;

		/// <summary>
		/// Dresser pieces the Armoire would take instead, for nothing.
		///
		/// ⭐⭐ Strictly better than packing them, and the arithmetic is not close: the Armoire takes
		/// a piece for **zero** dresser slots, where an outfit only ever amortises one slot across
		/// its whole set. It also costs no prism and is reversible. So these are deliberately kept
		/// OUT of the packing queue — packing them would be spending a prism to get a worse result.
		/// </summary>
		public List<string> ArmoireEligible = new();

		/// <summary>
		/// The same pieces, with what a transfer needs: what it is, and which cabinet row takes it.
		///
		/// ⚠ The dresser index is deliberately NOT carried. It is read fresh at the moment of the
		/// restore instead — an index read now and used later is the exact staleness that cost the
		/// packer two days, because removing an entry can shift everything after it.
		/// </summary>
		public List<(uint ItemId, uint CabinetRow, string Name)> ArmoireTransfer = new();

		/// <summary>
		/// Pieces already sitting in the Armoire, of which the dresser holds another copy.
		///
		/// ⚠ Different advice: there is nothing to store, the copy is simply surplus. Worth naming
		/// separately because "put this away" and "you already have this, for free" are not the same
		/// sentence.
		/// </summary>
		public List<string> ArmoireDuplicate = new();

		/// <summary>Items carrying a dye we think is worth stopping for.</summary>
		public List<(string Item, string Dye)> ExpensiveDyes = new();

		/// <summary>
		/// ⭐ Adding a loose piece to an outfit already in the dresser frees its WHOLE slot, where
		/// forming a new outfit from n pieces only frees n-1 : the outfit itself keeps one. So
		/// additions are the better return per piece, and are worth doing first.
		/// </summary>
		public int SlotsFromAdditions
			=> this.Additions.Sum(a => a.Pieces.Count(p => p.Index != uint.MaxValue));

		/// <summary>
		/// ⚠ Only the pieces that were in the dresser count toward recovering dresser slots, minus
		/// the one the outfit itself occupies. An outfit built entirely from the bags nets -1 here,
		/// and clamping at zero would hide that honestly-small cost.
		/// </summary>
		public int SlotsFromNewOutfits
			=> this.NewOutfits.Sum(o => o.Pieces.Count(p => p.Index != uint.MaxValue) - 1);

		public int SlotsFromDuplicates => this.Duplicates.Sum(d => d.Indices.Count - 1);

		public int SlotsRecoverable
			=> this.SlotsFromAdditions + this.SlotsFromNewOutfits + this.SlotsFromDuplicates;

		/// <summary>
		/// One prism per piece stored. ⚠ An estimate : the game says "maximum required", so this is
		/// a ceiling rather than a bill. Better to overstate than to stop halfway.
		/// </summary>
		public int PrismsNeeded
			=> this.Additions.Sum(a => a.Pieces.Count) + this.NewOutfits.Sum(o => o.Pieces.Count);

		/// <summary>
		/// ⚠ The packing half restores a whole set into the bags before storing it, so the free
		/// space needed is the LARGEST single job, not the total.
		/// </summary>
		public int FreeSlotsNeeded {
			get {
				var a = this.Additions.Count == 0 ? 0 : this.Additions.Max(x => x.Pieces.Count);
				var n = this.NewOutfits.Count == 0 ? 0 : this.NewOutfits.Max(x => x.Pieces.Count);
				return Math.Max(a, n);
			}
		}
	}

	/// <summary>
	/// ⭐ Dyes worth warning about, by Stain row id.
	///
	/// deserok, 2026-09-03: only two are ever expensive, and a blanket "you will lose dyes" warning
	/// fires on every run and becomes noise inside a week. Naming the one item carrying a Jet Black
	/// stops somebody BECAUSE it is rare. Same principle as a consent prompt : fire precisely, or
	/// train people to click through.
	///
	/// ⚠ By row id, never by name : a name match breaks the moment somebody runs a French client.
	/// The names are resolved from the sheet for display only.
	/// </summary>
	private static readonly string[] ExpensiveStainNames = { "Jet Black", "Pure White" };

	private static HashSet<uint>? expensiveStains;

	/// <summary>
	/// ⚠⚠ Resolved from the sheet by ENGLISH name, never hardcoded and never matched against the
	/// client's own language.
	///
	/// The first version hardcoded ids 5 and 6 from memory. **6 is Soot Black**, which is cheap and
	/// which deserok owns twenty-five of — so the warning would have fired twenty-five times on its
	/// first run, on the wrong dye, and discredited itself immediately. Jet Black is 102. Found by
	/// dumping every stain in a real dresser rather than by thinking harder.
	///
	/// ⭐ Reading English regardless of client language keeps the match stable for a French or German
	/// player while still costing nothing.
	/// </summary>
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

	/// <summary>
	/// Item ids currently used by a glamour plate.
	///
	/// ⚠⚠ <b>These cannot leave the dresser.</b> The game refuses the restore — and refuses it
	/// *after* reporting the command as sent, so the packer sat waiting for a piece that was never
	/// coming. Measured 2026-09-03: Skyworker's Boots, still in the dresser at index 246 with 67 free
	/// bag slots, three runs in a row. The dresser's own window hints at the concept: *"hide items
	/// registered to gear sets"*.
	///
	/// ⭐ So they are excluded from the scan rather than discovered by stalling. A piece that cannot
	/// be packed is not a failure, it is a fact, and the person should be told which pieces and why.
	///
	/// ⚠ Compared modulo a million: plate entries carry the HQ offset that dresser entries do not.
	/// </summary>
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

	/// <summary>item id → its row in the Cabinet sheet, for items the Armoire accepts.</summary>
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

	/// <summary>item id → the sets it can belong to, and in which slot.</summary>
	private Dictionary<uint, List<(uint SetItemId, int Slot)>>? membership;

	/// <summary>
	/// ⭐ Built by walking MirageStoreSetItem once rather than trusting MirageStoreSetItemLookup,
	/// whose semantics I would have had to infer. Reading the forward table is unambiguous : each
	/// row IS a set, each column IS a slot, so the reverse index is derived rather than assumed.
	/// A few thousand rows, built once.
	/// </summary>
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

		// ⚠⚠ The dresser contents are only in memory once the game has fetched them. Opening the
		// dresser once does it. Without this check the scan would confidently report an empty
		// dresser, which is the worst possible failure : plausible, wrong, and silent.
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
		var membership = this.Membership();
		var plateItems = PlateItems();
		var cabinet = this.Cabinet();

		// ⭐⭐ The armoire, whole, before the dresser passes touch anything.
		//
		// ⚠ It is NOT enumerable from the dresser — an item in the armoire is not in the dresser at
		// all, which is the entire reason the armoire is worth using. So "do you own this" can only
		// be answered for armoire items by walking the Cabinet sheet and asking the game about each
		// row. ~1000 lookups once per scan, against a tooltip that would otherwise have to guess.
		//
		// ⚠⚠ GATED ON IsCabinetLoaded, taken from Seventhxiv/Collections rather than found the hard
		// way. Without it, IsItemInCabinet answers from a buffer the server has not filled, and an
		// empty answer is indistinguishable from "you own nothing" — which would quietly wipe the
		// armoire half of the cache every time somebody scanned away from an inn.
		//
		// ⭐ Their note is worth keeping too: the dresser being loaded is itself good evidence you
		// are at an inn, where the armoire is standing next to it.
		if (UIState.Instance()->Cabinet.IsCabinetLoaded()) {
			foreach (var (_, cabinetRow) in cabinet) {
				if (UIState.Instance()->Cabinet.IsItemInCabinet(cabinetRow))
					result.ArmoireRows.Add(cabinetRow);
			}
		}

		// Pass one: split the dresser into packed outfits and loose pieces.
		var outfits = new List<(uint Index, uint ItemId)>();
		var loose = new List<(uint Index, uint ItemId)>();

		for (var i = 0u; i < PrismBoxSize && i < ids.Length; i++) {
			var itemId = ids[(int)i];
			if (itemId == 0) continue;

			result.Used++;

			// ⭐ An entry is a packed outfit exactly when its item id is a row of
			// MirageStoreSetItem : StoreNewOutfit's parameter is named setItemId, so a set row id
			// IS an item id, and the two questions are the same question.
			if (sets?.GetRowOrDefault(itemId) is not null) {
				outfits.Add((i, itemId));
			}
			else if (cabinet.TryGetValue(itemId, out var cabinetRow)) {
				result.LoosePieceIds.Add(itemId);
				// ⭐⭐ The Armoire takes this for free. Never queue it for packing: an outfit would
				// spend a prism to amortise one slot across a set, where the Armoire takes the whole
				// slot to zero and gives it back on demand.
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

				// ⚠ A piece a glamour plate is using is still packable — the game only asks first.
				// An earlier version excluded these and cost six recoverable slots for nothing.
				if (plateItems.Contains(itemId)) result.InUseByPlate.Add(ItemName(itemId));

				// ⭐ Optional: leave dyed pieces where they are. Packing destroys the dye either way,
				// so this is not protection so much as consent — for somebody who would rather deal
				// with those by hand than have a habitual command decide for them.
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

		// ⭐⭐ Bag contents join the SAME pool as the dresser's loose pieces, rather than being a
		// separate feature. After a dungeon the realistic case is two pieces already loose in the
		// dresser and the drop completing the set — two lists could never see that, and it is the
		// whole reason to look in the bags at all.
		var manager = InventoryManager.Instance();
		if (manager is not null) {
			foreach (var bag in Bags) {
				var page = manager->GetInventoryContainer(bag);
				if (page is null || !page->IsLoaded) continue;

				for (var slot = 0; slot < page->Size; slot++) {
					var item = page->GetInventorySlot(slot);
					if (item is null || item->ItemId == 0) continue;

					// ⭐⭐ THE ARMOIRE TAKES THINGS OUT OF YOUR BAGS TOO, and it is free there as well.
					// This pass only ever looked for outfit pieces, so a piece sitting loose in your
					// inventory that the Armoire would take was invisible — which meant the transfer
					// could only ever act on the dresser, and anything it had ALREADY pulled out was
					// beyond its reach. Found the hard way: a stalled run left forty-seven pieces in
					// the bags and a fresh scan could not see any of them.
					if (cabinet.TryGetValue(item->ItemId, out var bagCabinetRow)
					    && !UIState.Instance()->Cabinet.IsItemInCabinet(bagCabinetRow)) {
						result.ArmoireTransfer.Add(
							(item->ItemId, bagCabinetRow, ItemName(item->ItemId)));
					}

					// Only things that could ever join an outfit.
					if (!membership.ContainsKey(item->ItemId)) continue;

					// ⚠ An outfit itself, sitting in the bags, is not a piece.
					if (sets?.GetRowOrDefault(item->ItemId) is not null) continue;

					loose.Add((FromBags, item->ItemId));
					result.LooseInBags++;
				}
			}
		}

		// Pass two: duplicates, before anything else looks at the loose pile.
		//
		// ⚠ Order matters and it is not arbitrary. A helm you own three times might also belong to
		// a set : one copy goes into the outfit and the other two are simply waste. Counting
		// duplicates first and then packing the remainder keeps the two numbers from claiming the
		// same slot twice.
		var byItem = loose.GroupBy(x => x.ItemId).ToList();

		// ⚠ Duplicates remain a DRESSER idea. Owning one copy in the dresser and one in your bags is
		// not waste — the bag copy is the one you just looted and are about to use or sell.
		foreach (var group in byItem.Where(g => g.Count(x => x.Index != FromBags) > 1)) {
			result.Duplicates.Add(new Duplicate(
				group.Key, ItemName(group.Key),
				group.Where(x => x.Index != FromBags).Select(x => x.Index).ToList()));
		}

		// One representative of each distinct loose item survives into the packing analysis.
		var unique = byItem.Select(g => g.First()).ToList();

		// Pass three: pieces that can join an outfit already sitting in the dresser.
		//
		// ⭐ Done before new outfits because it is the better trade : the piece's whole slot goes,
		// where a new outfit always keeps one for itself.
		var claimed = new HashSet<uint>();

		foreach (var (outfitIndex, outfitItemId) in outfits) {
			var pieces = new List<(uint, uint, string, int)>();

			foreach (var (index, itemId) in unique) {
				if (claimed.Contains(index)) continue;
				if (!membership.TryGetValue(itemId, out var belongsTo)) continue;

				foreach (var (setItemId, slot) in belongsTo) {
					if (setItemId != outfitItemId) continue;

					// Already in there? Then this loose copy is a duplicate of a packed piece
					// rather than an addition.
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

			// What this outfit holds and what it is short of, whether or not we can help.
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

				// ⭐ Every FILLED slot armoire-eligible means the whole entry could go. Empty slots are
				// ignored on purpose: an outfit missing its earrings is still fully movable.
				var filledCount = 0;
				var allEligible = true;

				for (var slot = 0; slot < slotItems.Length; slot++) {
					if (slotItems[slot] == 0) continue;
					if (!MirageManager.MemberFunctionPointers.IsSetSlotUnlocked(
						    mirage, outfitIndex, slot)) continue;

					filledCount++;
					if (!cabinet.ContainsKey(slotItems[slot])) allEligible = false;
				}

				if (allEligible && filledCount > 0)
					result.FullyArmoireOutfits.Add((ItemName(outfitItemId), filledCount));

				// ⚠ An outfit with no filled slot at all. See Result.EmptyOutfits — this tool made
				// these, and they cannot be removed by hand.
				if (slots.Count > 0 && slots.TrueForAll(x => !x.Item3))
					result.EmptyOutfits.Add(ItemName(outfitItemId));
			}
		}

		// Pass four: sets with loose pieces and no packed outfit yet.
		//
		// ⚠ A COMPLETE set is never required. Partial outfits are legal — one was observed holding
		// four of nine — and one piece is enough to start one; see the note below the loop for why.
		// ⚠⚠⚠ A SET THAT ALREADY HAS AN OUTFIT MUST NEVER BE STARTED AGAIN. Pass three only
		// claims a loose piece when the existing outfit's slot for it is EMPTY — which is right — but
		// anything it declined then fell through to here and was queued as a brand new outfit for the
		// very same set. That is where the duplicate Rebel Sets came from: you loot a second Rebel
		// Coat, the outfit already has one, and the tool cheerfully builds a second Rebel Set beside
		// it. deserok, 2026-09-03: *"we need to add a check 'does this outfit exist already?' before
		// creating outfit."* He is right, and it belongs here rather than in the packer, because by
		// the time the packer sees a job the question is already settled.
		var alreadyPacked = new HashSet<uint>(outfits.Select(o => o.ItemId));

		var grouped = new Dictionary<uint, List<(uint, uint, string, int)>>();

		foreach (var (index, itemId) in unique) {
			if (claimed.Contains(index)) continue;
			if (!membership.TryGetValue(itemId, out var belongsTo)) continue;

			// ⚠ An item can belong to several sets, so "already packed" is a reason to look at the
			// NEXT one rather than to give up: a Vanguard glove may belong to a set you have built and
			// to one you have not.
			//
			// ⭐ Beyond that, taking the first remains a simplification rather than a solution — a
			// better assignment would maximise total slots saved across all of them. Worth revisiting
			// only if a real dresser shows it mattering.
			var choice = belongsTo.Find(b => !alreadyPacked.Contains(b.SetItemId));

			if (choice.SetItemId == 0) {
				// Every set it could join is already built, and pass three did not want it — so that
				// slot is filled. The piece is simply a spare.
				result.RedundantWithOutfit.Add(ItemName(itemId));
				continue;
			}

			if (!grouped.TryGetValue(choice.SetItemId, out var list))
				grouped[choice.SetItemId] = list = new List<(uint, uint, string, int)>();

			list.Add((index, itemId, ItemName(itemId), choice.Slot));
		}

		// ⭐⭐⭐ ONE piece is enough to start an outfit, and the threshold used to be two because a
		// single piece saves no slots: one in, one out.
		//
		// deserok, 2026-09-03: *"that mistwake hood should be packed regardless... it should simply
		// start the new outfit with a single item."* He is right, and the slot was never the point.
		// **An existing outfit is a magnet.** Once Mistwake Striking exists, every future Mistwake
		// piece looted joins it for a whole free slot instead of becoming another fragment — which
		// is precisely the accumulation this tool was built to undo. Starting one costs a prism.
		//
		// ⚠ They are counted separately because they genuinely recover nothing today, and folding
		// them into the headline would inflate it.
		foreach (var (setItemId, pieces) in grouped) {
			// ⭐⭐ One piece is enough. An existing outfit is a magnet: once it exists, every later
			// piece of that set joins it for a whole free slot instead of becoming another fragment,
			// which is exactly the accumulation this tool exists to undo.
			//
			// ⚠ It cost nothing but a prism, and the single-piece failure that briefly made this
			// look impossible was the cogwheel row, not the dialog — see DresserPacker.
			//
			// ⭐ RESOLVED, and the dialog was never different: the recording showed the same
			// SetConvert/SetConvertC pair, reached by cogging row FOUR rather than row zero. Single
			// pieces are back in.
			result.NewOutfits.Add(new NewOutfit(setItemId, ItemName(setItemId), pieces));
			if (pieces.Count == 1) result.OutfitsStarted++;
		}

		// Split the saving by where each piece came from, since the two are not interchangeable.
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

		result.Additions = result.Additions.OrderByDescending(a => a.Pieces.Count).ToList();
		result.NewOutfits = result.NewOutfits.OrderByDescending(o => o.Pieces.Count).ToList();
		result.Duplicates = result.Duplicates.OrderByDescending(d => d.Indices.Count).ToList();

		return result;
	}
}
