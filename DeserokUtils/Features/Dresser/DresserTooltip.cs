using System.Collections.Generic;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
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
/// ## ⭐⭐ A node of our own, and no signature hooks
///
/// SimpleTweaks reaches tooltip TEXT through five hand-written signature scans, because it is
/// building a general framework — number arrays, action tooltips, sixty fields. That is the bloat
/// deserok wanted to leave behind, and none of it is needed for one line: this creates its own
/// AtkTextNode and splices it in, driven by Dalamud's AddonLifecycle. Stable API, nothing to
/// re-scan on patch day.
///
/// ⚠ Two other plugins were already doing exactly this in his client when we looked — a probe of
/// a live tooltip found node 32612 (Price Insight's market board block) and one more sitting in the
/// same addon. This is the normal way to add a line here, not a clever one.
///
/// ⚠⚠ THE ROUTE THAT LOOKS EASIER IS THE ONE THAT CRASHES. Writing the description straight into
/// the tooltip's string array is what the framework above does safely, and it is NOT available from
/// the lifecycle event: <c>AddonRequestedUpdateArgs</c> carries <c>StringArrayData**</c>, the whole
/// table, which the docs say plainly. Casting it as one array put an access violation inside the
/// client. See the note on <see cref="AfterUpdate"/>.
/// </summary>
internal sealed unsafe class DresserTooltip {
	/// <summary>
	/// The item description, in the tooltip's string array.
	///
	/// ⚠ 13, taken verbatim from SimpleTweaks' ItemTooltipField rather than counted by hand. Its
	/// neighbours are the item name (0), the glamour name (1) and the category (2) — so an
	/// off-by-one here would overwrite the item's NAME with our note.
	/// </summary>
	/// <summary>
	/// Our text node's id.
	///
	/// ⚠ Distinctive on purpose. The probe found node 32612 (Price Insight) and node 1398013963
	/// already living in this addon, so the id space is genuinely shared with other people's plugins
	/// and a low number would eventually collide with one.
	/// </summary>
	private const uint OurNodeId = 0x44535501;

	/// <summary>The node our line sits beside, and whose position moves to make room.</summary>
	private const uint InsertBesideNode = 2;

	/// <summary>⚠ The "Shop Selling Price" line — ordinary body text, borrowed for its styling.</summary>
	private const uint StyleFromNode = 44;

	/// <summary>
	/// ⚠⚠⚠ THE ICON CHECKMARK WAS TRIED AND ABANDONED. Do not rebuild it without reading this.
	///
	/// deserok asked for the game's own collected-tick — the one in the icon's lower corner on a
	/// barding you own — rather than an icon of ours, which was the right instinct: it is the
	/// affordance people already know.
	///
	/// ⭐ It IS reachable, and finding it was not the problem. Node 19, an Image inside Res node 18,
	/// located by dumping the whole node tree hovering a barding he owns, dumping it again hovering a
	/// piece of gear, and diffing: out of 661 nodes exactly one flipped. It is also not gated on
	/// UIState.IsItemActionUnlocked, so it would have worked for ordinary gear.
	///
	/// ⚠⚠ WHAT KILLED IT: the write lands and does not hold. Measured 2026-09-04 — the node is
	/// found, set visible, and reads back visible — then the next RequestedUpdate four milliseconds
	/// later finds it hidden again. The icon component reapplies its own visibility after us, so
	/// making it stick means writing every frame a tooltip is open, forever, against a component that
	/// disagrees. That is a fight to lose slowly: it works until a patch and then stops quietly.
	///
	/// ⭐ The inline line already answers the question this was decoration for, so the trade was not
	/// close. Recorded rather than deleted because "add the checkmark" is an obvious thing to suggest
	/// again, and the next person deserves the measurement instead of the afternoon.
	/// </summary>

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
	/// <summary>
	/// ⚠⚠ UNVERIFIED. UIForegroundPayload takes a row of the game's own colour table, and these two
	/// numbers were picked without ever seeing them rendered — which is the same class of guess that
	/// has been wrong three times today. /dsu-dresser colours prints the table so they can be chosen
	/// by looking rather than by hoping.
	///
	/// ⭐ The intent: green for owned, amber for a piece that would join a set. Matching the plugin's
	/// own accent pair, where blue reads and amber acts.
	/// </summary>
	/// <summary>
	/// ⭐ Confirmed by looking: 43 renders green and 31 renders the amber that reads beside the
	/// Warning sprite. Both were guesses when written and both happened to land — which is luck, not
	/// method, and the reason /dsu-dresser colours exists.
	/// </summary>
	private const ushort OwnedColour = 43;

	private const ushort WantedColour = 31;

	/// <summary>⚠⚠ NOT YET SEEN. Pick it from /dsu-dresser colours rather than trusting this.</summary>
	private const ushort MissingColour = 17;

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

	/// <summary>
	/// Undo our own height change before the game recalculates the tooltip.
	///
	/// ⚠⚠ WITHOUT THIS THE WINDOW GROWS EVERY FRAME. The post handler adds our node's height to
	/// the window; if that is never taken back off, the next update adds it again on top. Copied in
	/// shape from SimpleTweaks' AdditionalItemInfo, which needs the same pairing for the same reason.
	/// </summary>
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

	/// <summary>
	/// Put our line on the tooltip, once the game has finished building it.
	///
	/// ⚠⚠⚠ A NODE, NOT A STRING ARRAY, and the difference cost a client crash. The first attempt
	/// wrote into the string array Dalamud hands to OnRequestedUpdate — except that parameter is
	/// StringArrayData**, the whole table, and casting it as one array put an access violation inside
	/// StringArrayData.SetValue. Writing string fields is only safe inside the game's own
	/// GenerateItemTooltip, which needs a signature scan to reach.
	///
	/// ⭐⭐ Measured before rebuilding rather than guessed at again: a probe of a real tooltip showed
	/// ItemDetail carrying ZERO AtkValues — so the trick that works on the dresser's own tooltip is
	/// unavailable here — and, in the same dump, two custom text nodes belonging to OTHER plugins
	/// already sitting in the addon. Node 32612 is Price Insight's market board block. That is the
	/// route, confirmed live in deserok's client rather than inferred from source.
	/// </summary>
	private void AfterUpdate(AddonEvent type, AddonArgs args) {
		if (!Plugin.Config.DresserTooltip) return;

		var addon = (AtkUnitBase*)args.Addon.Address;
		if (addon is null || addon->WindowNode is null) return;

		var existing = FindNode(addon);
		if (existing is not null) existing->AtkResNode.ToggleVisibility(false);

		var agent = AgentItemDetail.Instance();
		if (agent is null) return;

		// ⚠⚠ THE HQ FLAG LIVES HERE. The agent reports a high-quality item as id + 1,000,000, and
		// every lookup against a sheet or against the cache would miss it — silently, and looking
		// exactly like "you do not own this". Taken from Seventhxiv/Collections, which normalises
		// every id it handles.
		var itemId = DresserCache.PureItemId(agent->ItemId);
		if (itemId == 0) return;

		var note = Note(itemId);
		if (note.Payloads.Count == 0) return;

		var insert = addon->GetNodeById(InsertBesideNode);
		if (insert is null) return;

		// ⚠ Its style is borrowed rather than chosen: whatever the game does to tooltips, our line
		// does too. Node 44 is the "Shop Selling Price" line — ordinary body text.
		var template = addon->GetTextNodeById(StyleFromNode);
		if (template is null) return;

		var node = existing is not null ? existing : Create(addon, insert, template);
		if (node is null) return;

		node->AtkResNode.ToggleVisibility(true);
		node->SetText(note.EncodeWithNullTerminator());
		node->ResizeNodeForCurrentText();
		node->AtkResNode.SetPositionFloat(17f, addon->WindowNode->AtkResNode.Height - 10f);

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

		// ⚠ Spliced into the sibling chain by hand. UpdateDrawNodeList afterwards is not optional —
		// without it the node exists and is never drawn.
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

	/// <summary>
	/// Take our node back out.
	///
	/// ⚠⚠ A plugin reload does NOT tear down the game's UI. A node left spliced into the addon
	/// outlives us, pointing at a text buffer nobody owns any more.
	/// </summary>
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
	private static SeString Note(uint itemId) {
		var lines = new SeString();

		var cache = DresserCache.Current;
		if (cache is null) return lines;

		// ⭐⭐⭐ SHORT VERDICT, THEN THE DETAIL ON ITS OWN LINE. The first version put everything on
		// one — "You have this — packed in your Thavnairian Attire (Bustier)" — and it ran straight
		// out of the right edge of the tooltip, because the node clips rather than wraps.
		//
		// ⭐⭐ deserok's fix, 2026-09-04, and it is better than making it wrap: *"we shouldn't make it
		// so verbose... don't need to specify which outfit, normally the item itself is labeled, and
		// honestly they're going to just go home and pack it anyways, letting the addon sort it."* The
		// set NAME is decoration — you are holding a Thavnairian Headdress, you can see what set it is
		// for. What you cannot see is whether you already have one.
		//
		// ⭐ So the second line is the market board's shape: the set and how far along it is. Present
		// when it adds something, absent when it does not.
		// ⚠⚠ GEAR ONLY, and the gate matters far more now there is a negative line. Without it,
		// every potion, crafting material and piece of furniture in the game would be told it is not
		// an appearance you own — true, and absurd.
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

			// ⚠ "Not in your dresser", never "you do not own this". The cache knows the dresser and
			// the armoire and nothing else — the piece may well be sitting in the bags of the person
			// reading it, which is exactly the looting case this feature is for.
			lines.Payloads.Add(new TextPayload(
				need.Completes ? " Not in your dresser — completes a set!" : " You need this for an outfit"));
			lines.Payloads.Add(new UIForegroundPayload(0));

			Detail(lines, need.Set, WantedColour);
		}
		else {
			// ⭐⭐⭐ THE PLAIN NEGATIVE, and it was wrong to leave it out. The argument against was
			// that it fires on almost every item in the game and rare lines are the ones that get
			// read — but that reasons about sets, and deserok corrected the frame: *"the goal is 'do
			// we have this appearance'."*
			//
			// Under that question silence is not an answer, it is the ABSENCE of one, and nobody can
			// tell "you do not own this" apart from "the plugin did not run" or "the cache is empty".
			// A tool asked one question should answer it every time, including when the answer is no.
			lines.Payloads.Add(new UIForegroundPayload(MissingColour));
			lines.Payloads.Add(new TextPayload("\u2717 You do not have this appearance"));
			lines.Payloads.Add(new UIForegroundPayload(0));
		}

		// ⚠ Only when it could be wrong. The dresser cannot change with its window shut, so on every
		// ordinary tooltip this stays silent rather than stamping an "as of" nobody can act on.
		if (lines.Payloads.Count > 0 && cache.MaybeStale) {
			lines.Payloads.Add(new NewLinePayload());
			lines.Payloads.Add(new UIForegroundPayload(3));
			lines.Payloads.Add(new TextPayload("  dresser changed since the last scan"));
			lines.Payloads.Add(new UIForegroundPayload(0));
		}

		return lines;
	}

	/// <summary>The set line: name and how many of its slots are filled. ⚠ Indented, never wrapped.</summary>
	private static void Detail(SeString lines, (string Name, int Have, int Total) set, ushort colour) {
		lines.Payloads.Add(new NewLinePayload());
		lines.Payloads.Add(new UIForegroundPayload(colour));
		lines.Payloads.Add(new TextPayload($"  {Fit(set.Name)} ({set.Have}/{set.Total})"));
		lines.Payloads.Add(new UIForegroundPayload(0));
	}

	/// <summary>
	/// How much of a set name fits before the tooltip clips it.
	///
	/// ⚠⚠ THE NODE CLIPS, IT DOES NOT WRAP. Anything past the right edge is simply not drawn, and
	/// it is not drawn SILENTLY — the line looks finished, just wrong. That is worse than an obvious
	/// truncation, which at least says a name was cut.
	///
	/// ⭐⭐ 44, AND THE POINT IS THAT IT NEVER FIRES. The first attempt was 34, reasoned from the
	/// longest market board line that fits beside ours — deserok looked at the rendered box and said
	/// there was room for about ten more, which there is. Bracketed by two real measurements: the
	/// line that clipped was 60, and this does not.
	///
	/// ⭐ At 44 every set name in his dresser passes through untouched — the longest is 39,
	/// "Prestige High Allagan Attire of Healing". So this stopped being a routine truncation and
	/// became insurance against a name nobody has seen yet, which is the right job for it. A cap
	/// that trims ordinary names is a cap set too low.
	///
	/// ⚠ Cut at a word boundary when there is one close by, because "Prestige High Allagan Attire
	/// of…" reads as a name and "Prestige High Allagan Attire of Heal…" reads as a bug.
	/// </summary>
	private static string Fit(string name) {
		const int max = 44;
		if (name.Length <= max) return name;

		var cut = name.LastIndexOf(' ', max - 1);
		if (cut < max - 12) cut = max - 1;

		return name[..cut].TrimEnd() + "…";
	}

	/// <summary>Where the item already is, in the words somebody at a vendor needs.</summary>
	private static (string Where, (string Name, int Have, int Total)? Set)? Owned(
		DresserCache cache, uint itemId) {
		if (cache.LoosePieces.Contains(itemId)) return ("in your glamour dresser", null);

		foreach (var (setItemId, slot) in Sets(itemId)) {
			if (!cache.OutfitSlots.TryGetValue(setItemId, out var filled)) continue;
			if (!filled.Contains(slot)) continue;

			return ("in an outfit",
				(ItemName(setItemId), Progress(cache, setItemId).Count, SlotCount(setItemId)));
		}

		if (CabinetByItem().TryGetValue(itemId, out var row) && cache.Armoire.Contains(row))
			return ("in your Armoire", null);

		// ⭐⭐⭐ THE ARMOURY CHEST, AND IT IS THE WHOLE REASON THIS PARAGRAPH EXISTS. deserok,
		// 2026-09-04: *"looking through the armory chest is... each thing is in its own tab, there's
		// no search, and it's just icons, so you have to mouse over everything."* That is the single
		// worst place in the game to answer "do I already have this", which makes it the most
		// valuable place to answer it for somebody.
		//
		// ⭐⭐ LIVE, not cached, and the distinction is not an oversight. The dresser needs a cache
		// because the game only sends its contents while you are standing at one. The armoury and
		// your equipment are ALWAYS loaded, so reading them fresh is both simpler and strictly more
		// correct — there is no staleness to reason about at all.
		//
		// ⚠ Cheap enough for a tooltip: about 550 slot reads, each a pointer deref and an integer
		// compare, with no allocation and no sheet lookup. The two draw-loop disasters in this
		// project's history were sheet walks doing string extraction, which is a different order of
		// cost entirely.
		if (Worn(itemId)) return ("equipped right now", null);
		if (InArmoury(itemId)) return ("in your armoury chest", null);

		// ⚠ THE BAGS ARE DELIBERATELY NOT SEARCHED. If a piece is in your inventory you are almost
		// certainly looking straight at it, and "you have this in your bags" would be the same
		// useless redundancy as marking an item inside the glamour dresser — which deserok already
		// caught and rejected. The hidden places are the ones worth reporting.
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

			// ⚠ Normalised, because a high-quality item in the armoury carries the flag and would
			// otherwise never match the id the tooltip is asking about.
			if (item is not null && DresserCache.PureItemId(item->ItemId) == itemId) return true;
		}

		return false;
	}

	/// <summary>⚠ Every armoury category. Missing one reads as "you do not own it", silently.</summary>
	private static readonly InventoryType[] Armoury = {
		InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
		InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
		InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
		InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
	};

	/// <summary>
	/// Whether it would fill a gap in an outfit you already have.
	///
	/// ⭐⭐ THIS is the line that turns a vendor list into a decision. "You own the Rebel Set" is not
	/// the useful fact when you are looking at a pair of Rebel Boots — "your Rebel Set is missing
	/// exactly that slot" is.
	/// </summary>
	private static ((string Name, int Have, int Total) Set, bool Completes)? Wanted(
		DresserCache cache, uint itemId) {
		foreach (var (setItemId, slot) in Sets(itemId)) {
			var have = Progress(cache, setItemId);

			// ⚠ Already got that slot covered, packed or loose. Nothing to say.
			if (have.Contains(slot)) continue;

			// ⚠⚠ Silent when you own NOTHING of the set. deserok asked whether a third colour was
			// worth it for "you have neither this nor the outfit" — it is not, and the reason is that
			// it describes almost every item in the game. These lines work because they are rare; one
			// that fires on every vendor tooltip stops being read, and "you do not own this" is the
			// least actionable of the three anyway. Silence already means it.
			if (have.Count == 0) continue;

			var total = SlotCount(setItemId);

			// ⚠ "Completes" only when it genuinely does. Every other case just shows the count —
			// the same care as OutfitsExtended in the packer, and for the same reason: one small
			// untruth and nobody believes the rest of the numbers.
			return ((ItemName(setItemId), have.Count, total), have.Count + 1 == total);
		}

		return null;
	}

	/// <summary>
	/// Which of a set's slots you already have covered, packed OR loose.
	///
	/// ⭐⭐⭐ LOOSE PIECES COUNT, and leaving them out was a real gap. The count used to come only
	/// from a packed outfit's filled slots, so three Rebel pieces sitting loose in the dresser with
	/// no Rebel Set built yet counted as zero — and hovering a fourth said nothing at all. That is
	/// exactly the person this feature is for: someone accidentally collecting a set who has no idea
	/// they are three-fifths of the way through it.
	///
	/// ⚠ A HashSet of slots rather than a count, because a loose piece and a packed piece can be
	/// the same slot, and adding the two totals would claim you own more of the set than you do.
	/// </summary>
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

	/// <summary>
	/// Is this a thing whose APPEARANCE you could own?
	///
	/// ⚠ Equippable and not a soul crystal. A coarse test on purpose: the real rules for what the
	/// glamour dresser accepts are intricate, and being slightly too generous costs a line on a piece
	/// of gear nobody glamours, where being too strict costs silence on gear somebody does.
	/// </summary>
	private static bool IsAppearance(uint itemId) {
		if (Plugin.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId) is not { } item) return false;

		return item.EquipSlotCategory.RowId != 0 && item.ItemUICategory.RowId != 62;
	}

	private static string ItemName(uint itemId)
		=> Plugin.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"#{itemId}";
}
