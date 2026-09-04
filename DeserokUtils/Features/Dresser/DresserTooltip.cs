using System.Collections.Generic;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Memory;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Excel.Sheets;

namespace DeserokUtils.Features.Dresser;

/// <summary>
/// Says whether you already own a piece, on the game's own item tooltip.
///
/// ⭐⭐⭐ THE WHOLE VALUE IS WHERE IT APPEARS. Q and Bunny, 2026-09-04: mark what you have, and what
/// would complete a set. The scan has always known the answer — what it lacked was a way to say it
/// anywhere except at the dresser, which is the one place you can already see it. A vendor list, the
/// market board, a Need/Greed roll: those are where "do I own this" is a decision rather than a
/// lookup, and every one of them raises this same tooltip.
///
/// ## ⭐⭐ No signature hooks, and that is the whole trick
///
/// SimpleTweaks reaches this data through five hand-written signature scans, because it is building
/// a general tooltip framework — number arrays, action tooltips, sixty fields. We want one line, so
/// none of that is needed: Dalamud's AddonLifecycle raises PreRequestedUpdate on <c>ItemDetail</c>
/// and hands over the same StringArrayData. Stable API, nothing to re-scan on patch day, and the
/// bloat deserok wanted to leave behind is exactly the part we skip.
///
/// ⚠ One thing is taken verbatim rather than derived: <see cref="ItemDescriptionField"/>.
///
/// ## ⚠ What it deliberately does not do
///
/// No checkmark on the icon. That IS a real mechanism — <c>UIState.IsItemActionUnlocked</c>, hooked,
/// returning 1 or 2, which is how SimpleTweaks marks fully-collected card packs. But the game only
/// asks that question about items which HAVE an ItemAction: mounts, minions, Triple Triad cards.
/// Ordinary gear has none, so there is most likely nothing to answer and the hook would never fire.
/// Worth one test before anybody writes it, and not worth guessing at.
/// </summary>
internal sealed unsafe class DresserTooltip {
	/// <summary>
	/// The item description, in the tooltip's string array.
	///
	/// ⚠ 13, taken verbatim from SimpleTweaks' ItemTooltipField rather than counted by hand. Its
	/// neighbours are the item name (0), the glamour name (1) and the category (2) — so an
	/// off-by-one here would overwrite the item's NAME with our note.
	/// </summary>
	private const int ItemDescriptionField = 13;

	/// <summary>
	/// ⚠⚠ FONTAWESOME CANNOT GO HERE, and it is worth knowing why rather than trying. FontAwesome
	/// is a font Dalamud loads for ImGui — our own windows. This tooltip is a GAME addon, drawn by
	/// the game, rendering an SeString in the game's own font, which has never heard of it. Asking
	/// for it produces a missing-glyph box, not a red exclamation mark.
	///
	/// ⭐⭐ What the game has instead is better for this anyway: BitmapFontIcon, a set of real
	/// sprites the client already draws inline in chat and tooltips — the crossworld icon, the
	/// grand company crests, quest markers. <c>Attention</c> is the yellow exclamation bubble, which
	/// is precisely the "!" deserok asked for, and it arrives already coloured and at the right
	/// baseline rather than as text pretending to be an icon.
	///
	/// ⚠ Colour is separate and applies only to the TEXT: UIForegroundPayload takes a row of the
	/// game's own colour table, so these are the client's palette rather than ours. That is the point
	/// — a line that matches the tooltip it is sitting in reads as part of the game.
	///
	/// ⭐ The two chosen, from the real enum rather than a guess at it: <c>GreenDot</c> for owned and
	/// <c>Warning</c> — the yellow exclamation — for a piece that fits a set you are building. Both
	/// are one word to change. Near neighbours if either reads wrong in place: GoldStar, BlueStar,
	/// OrangeDiamond, ExclamationRectangle.
	/// </summary>
	private const ushort OwnedColour = 43;

	private const ushort WantedColour = 31;

	/// <summary>
	/// Ours, so a second refresh does not append the line twice.
	///
	/// ⚠ The tooltip refreshes more than once per hover. SimpleTweaks uses an invisible link payload
	/// as its marker; a zero-width space is the same idea without registering a link handler for
	/// something nobody will ever click.
	/// </summary>
	private const string Marker = "​";

	public void Listen() {
		Plugin.AddonLifecycle.RegisterListener(
			AddonEvent.PreRequestedUpdate, "ItemDetail", this.OnItemTooltip);
	}

	public void Dispose() {
		Plugin.AddonLifecycle.UnregisterListener(
			AddonEvent.PreRequestedUpdate, "ItemDetail", this.OnItemTooltip);
	}

	private void OnItemTooltip(AddonEvent type, AddonArgs args) {
		if (!Plugin.Config.DresserTooltip) return;
		if (args is not AddonRequestedUpdateArgs update) return;

		var agent = AgentItemDetail.Instance();
		if (agent is null) return;

		// ⚠⚠ THE HQ FLAG LIVES HERE. The agent reports a high-quality item as id + 1,000,000, and
		// every lookup against a sheet or against the cache would miss it — silently, and looking
		// exactly like "you do not own this". Taken from Seventhxiv/Collections, which normalises
		// every id it handles.
		var itemId = DresserCache.PureItemId(agent->ItemId);
		if (itemId == 0) return;

		var note = Note(itemId);
		if (note.Count == 0) return;

		var strings = (StringArrayData*)update.StringArrayData;
		if (strings is null || strings->AtkArrayData.Size <= ItemDescriptionField) return;

		var text = strings->StringArray[ItemDescriptionField];
		if (text.Value is null) return;

		var description = MemoryHelper.ReadSeStringNullTerminated((nint)text.Value);
		if (description.TextValue.Contains(Marker)) return;

		if (description.TextValue.Trim().Length > 0) {
			description.Payloads.Add(new NewLinePayload());
			description.Payloads.Add(new NewLinePayload());
		}

		description.Payloads.Add(new TextPayload(Marker));
		foreach (var payload in note) description.Payloads.Add(payload);

		strings->SetValue(ItemDescriptionField, description.EncodeWithNullTerminator(), false);
	}

	/// <summary>
	/// What to say about this item, or nothing at all.
	///
	/// ⚠⚠ Nothing at all is the common case and it has to stay CHEAP. This runs on every tooltip in
	/// the game, including the hundreds skimmed past in a vendor list — the same hot path that has
	/// already cost this project a framerate twice. Dictionary and hash-set lookups only; the two
	/// sheet walks it would otherwise need are memoised in <see cref="SetsByPiece"/> and
	/// <see cref="CabinetByItem"/>.
	///
	/// ⚠ Two lines at the very most. A tooltip is not a report — the Dresser tab is.
	/// </summary>
	private static List<Payload> Note(uint itemId) {
		var lines = new List<Payload>();

		var cache = DresserCache.Current;
		if (cache is null) return lines;

		var where = Owned(cache, itemId);
		if (where is not null) {
			// ⭐ A real sprite, not a text glyph. See the note on Icons below.
			lines.Add(new IconPayload(BitmapFontIcon.GreenDot));
			lines.Add(new UIForegroundPayload(OwnedColour));
			lines.Add(new TextPayload($" {where}"));
			lines.Add(new UIForegroundPayload(0));
		}

		if (Wanted(cache, itemId) is { } wanted) {
			if (lines.Count > 0) lines.Add(new NewLinePayload());
			lines.Add(new IconPayload(BitmapFontIcon.Warning));
			lines.Add(new UIForegroundPayload(WantedColour));
			lines.Add(new TextPayload($" {wanted}"));
			lines.Add(new UIForegroundPayload(0));
		}

		// ⚠ Only when it could be wrong. The dresser cannot change with its window shut, so on every
		// ordinary tooltip this stays silent rather than stamping an "as of" nobody can act on.
		if (lines.Count > 0 && cache.MaybeStale) {
			lines.Add(new NewLinePayload());
			lines.Add(new UIForegroundPayload(3));
			lines.Add(new TextPayload("   (your dresser has changed since the last scan)"));
			lines.Add(new UIForegroundPayload(0));
		}

		return lines;
	}

	/// <summary>Where the item already is, in the words somebody at a vendor needs.</summary>
	private static string? Owned(DresserCache cache, uint itemId) {
		if (cache.LoosePieces.Contains(itemId)) return "In your glamour dresser";

		foreach (var (setItemId, slot) in Sets(itemId)) {
			if (cache.OutfitSlots.TryGetValue(setItemId, out var filled) && filled.Contains(slot))
				return $"In your {ItemName(setItemId)} outfit";
		}

		if (CabinetByItem().TryGetValue(itemId, out var row) && cache.Armoire.Contains(row))
			return "In your Armoire";

		return null;
	}

	/// <summary>
	/// Whether it would fill a gap in an outfit you already have.
	///
	/// ⭐⭐ THIS is the line that turns a vendor list into a decision. "You own the Rebel Set" is not
	/// the useful fact when you are looking at a pair of Rebel Boots — "your Rebel Set is missing
	/// exactly that slot" is.
	/// </summary>
	private static string? Wanted(DresserCache cache, uint itemId) {
		foreach (var (setItemId, slot) in Sets(itemId)) {
			if (!cache.OutfitSlots.TryGetValue(setItemId, out var filled)) continue;
			if (filled.Contains(slot)) continue;

			var total = SlotCount(setItemId);

			// ⚠ "Completes" only when it genuinely does. Every other case says how far along you
			// are — the same care as OutfitsExtended in the packer, and for the same reason: one
			// small untruth and nobody believes the rest of the numbers.
			return filled.Count + 1 == total
				? $"Completes your {ItemName(setItemId)}"
				: $"Fits your {ItemName(setItemId)} — {filled.Count} of {total}";
		}

		return null;
	}

	// ── Memoised sheet reads ─────────────────────────────────────────────────────────────

	private static Dictionary<uint, List<(uint SetItemId, int Slot)>>? setsByPiece;
	private static Dictionary<uint, uint>? cabinetByItem;
	private static Dictionary<uint, int>? slotCounts;

	private static List<(uint SetItemId, int Slot)> Sets(uint itemId)
		=> SetsByPiece().TryGetValue(itemId, out var sets)
			? sets
			: new List<(uint, int)>();

	/// <summary>
	/// item id → the sets it belongs to, and in which column.
	///
	/// ⚠ Built once. DresserScan has an identical map, and duplicating it is deliberate: that one
	/// lives on a scan instance that exists for the length of a scan, and reaching into it from a
	/// tooltip would keep a scan alive for the session to borrow a dictionary.
	/// </summary>
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

	private static string ItemName(uint itemId)
		=> Plugin.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"#{itemId}";
}
