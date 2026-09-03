using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Client.Game;

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

		/// <summary>Items carrying a dye we think is worth stopping for.</summary>
		public List<(string Item, string Dye)> ExpensiveDyes = new();

		/// <summary>
		/// ⭐ Adding a loose piece to an outfit already in the dresser frees its WHOLE slot, where
		/// forming a new outfit from n pieces only frees n-1 : the outfit itself keeps one. So
		/// additions are the better return per piece, and are worth doing first.
		/// </summary>
		public int SlotsFromAdditions => this.Additions.Sum(a => a.Pieces.Count);

		public int SlotsFromNewOutfits => this.NewOutfits.Sum(o => o.Pieces.Count - 1);

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
			else {
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

		// Pass two: duplicates, before anything else looks at the loose pile.
		//
		// ⚠ Order matters and it is not arbitrary. A helm you own three times might also belong to
		// a set : one copy goes into the outfit and the other two are simply waste. Counting
		// duplicates first and then packing the remainder keeps the two numbers from claiming the
		// same slot twice.
		var byItem = loose.GroupBy(x => x.ItemId).ToList();

		foreach (var group in byItem.Where(g => g.Count() > 1)) {
			result.Duplicates.Add(new Duplicate(
				group.Key, ItemName(group.Key), group.Select(x => x.Index).ToList()));
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
			}
		}

		// Pass four: sets with two or more loose pieces and no packed outfit yet.
		//
		// ⚠⚠ TWO, not a complete set. Partial outfits are legal : one was observed holding four of
		// nine. A single piece is excluded because packing it saves nothing : one slot in, one slot
		// out.
		var grouped = new Dictionary<uint, List<(uint, uint, string, int)>>();

		foreach (var (index, itemId) in unique) {
			if (claimed.Contains(index)) continue;
			if (!membership.TryGetValue(itemId, out var belongsTo)) continue;

			// ⚠ An item can belong to several sets. Taking the first is a simplification, not a
			// solution : a better assignment would maximise total slots saved across all of them.
			// Worth revisiting only if a real dresser shows it mattering.
			var (setItemId, slot) = belongsTo[0];

			if (!grouped.TryGetValue(setItemId, out var list))
				grouped[setItemId] = list = new List<(uint, uint, string, int)>();

			list.Add((index, itemId, ItemName(itemId), slot));
		}

		foreach (var (setItemId, pieces) in grouped) {
			if (pieces.Count < 2) continue;
			result.NewOutfits.Add(new NewOutfit(setItemId, ItemName(setItemId), pieces));
		}

		result.Additions = result.Additions.OrderByDescending(a => a.Pieces.Count).ToList();
		result.NewOutfits = result.NewOutfits.OrderByDescending(o => o.Pieces.Count).ToList();
		result.Duplicates = result.Duplicates.OrderByDescending(d => d.Indices.Count).ToList();

		return result;
	}
}
