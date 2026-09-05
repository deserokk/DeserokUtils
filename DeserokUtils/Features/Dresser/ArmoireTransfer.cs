using System;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Dresser;

/// <summary>
/// Moves dresser pieces the Armoire will take into the Armoire, which costs nothing to store.
///
/// ⭐⭐⭐ THE BEST TRADE THE DRESSER OFFERS. Packing amortises one dresser slot across a whole set;
/// the Armoire takes the slot to zero and hands the item back on demand. The scan has reported these
/// since the day it was written — forty-two of them on the run that first found the list — and
/// acting on it by hand means restoring each piece, walking to the Armoire and storing it, one at a
/// time.
///
/// ## ⭐⭐ Where the actual line is
///
/// deserok, 2026-09-05, correcting me twice in a minute and both times usefully. First: *"nothing
/// here is not reversible, you can unpack outfits (though we have not figured out a way to do it on
/// the machine end)"* — I had been calling packing irreversible, and it is not. An outfit restores,
/// an Armoire item withdraws. What we cannot yet do is undo it FROM THE PLUGIN, which is a much
/// smaller claim.
///
/// Then, when that got over-applied to mean nothing is dangerous: *"wait, discarding is the line,
/// that destroys."* ⚠⚠ **That is the boundary and it has not moved.** Everything in this feature
/// and the packer shuffles items between places the game will hand them back from. Discarding does
/// not, and nothing here should ever grow one.
///
/// ⚠ So the risk in this file is wasted time and a confused dresser, not lost items — a reason to
/// be careful rather than frightened, and not a reason to relax about what gets built next to it.
///
/// ## ⚠⚠⚠ TWO PIECES OF FURNITURE, NOT ONE INTERFACE
///
/// deserok, 2026-09-05, and it shapes everything: *"armoire is not the same UI as the glamour chest,
/// it's a different furniture item like 1 yard away, so it involves closing the glam chest,
/// interacting with the armoire then using that interface."*
///
/// So there is no moment when both windows are open. The physical loop is: open the dresser, take
/// pieces out, close it, turn around, open the Armoire, put them in. That is WHY the batch is sized
/// by free bag slots — each round trip costs a walk, so the run should carry as much as it can.
///
/// ⭐ What this code bets on: the loaded FLAGS outlive their windows. PrismBoxLoaded and
/// IsCabinetLoaded describe whether the server has sent the contents, not whether you are looking at
/// them — so opening each once should let the rest run as plain API calls with nothing on screen.
///
/// ⚠⚠ UNPROVEN, and it is the thing to watch on the first run. If StoreCabinetItem needs the
/// Armoire window actually open, the symptom is exact and readable: the restore lands, the store
/// fires, and IsItemInCabinet never turns true. The log says which step stalled, so the answer comes
/// out of one attempt rather than out of reasoning.
///
/// ⭐ And the fallback is already built elsewhere: the Interact feature can operate a furnishing
/// from a standing position, which is exactly what "turn around and open the Armoire" is. Worth
/// reaching for only once the simple version is proven not to work.
///
/// ## ⭐ Loose, not packed
///
/// deserok: *"no outfit packing, things go in loose."* Nothing here touches MirageStoreSetItem, the
/// cogwheel, or the outfit dialog — the whole apparatus that took two days to make behave. A piece
/// comes out of the dresser and goes into the Armoire as itself. That is why this is a small file.
///
/// ## ⚠⚠ What the return value is worth: nothing
///
/// <c>StoreCabinetItem</c> returns a bool, and the packer already learned what that is worth on
/// <c>StoreNewOutfit</c> — which returned true, stored nothing, and cost two aborted runs before
/// anybody checked. Ground truth here is <c>IsItemInCabinet</c>, asked afterwards. Every step waits
/// to be told by the game that it happened.
/// </summary>
internal sealed unsafe class ArmoireTransfer {
	internal enum State { Idle, Restoring, Storing, Done, Failed }

	/// <summary>
	/// ⚠ Kept clear of full. deserok's number: gather (free slots − 3) at a time. The margin is not
	/// superstition — a restore that arrives with nowhere to go is refused, and a run that fills
	/// somebody's bags to the brim leaves them worse off than when they started.
	/// </summary>
	private const int SlotsToLeaveFree = 3;

	/// <summary>
	/// ⚠⚠ A CEILING ON TOP OF THE FREE-SLOT RULE. The first run restored all forty-seven pieces in
	/// a single tick, which should have been impossible under (free - 3) and was not — and until the
	/// log says why, an arithmetic slip must not be able to empty a dresser into somebody's bags.
	/// A wrong batch size then costs one extra round trip instead of a mess to clean up by hand.
	/// </summary>
	private const int MaxBatch = 8;

	/// <summary>⚠ Ticks between one item and the next — roughly half a second at 60fps.</summary>
	private const int PaceTicks = 30;

	/// <summary>⚠ Up to this much more, at random. An even beat is a signature of its own.</summary>
	private const int PaceJitter = 25;

	private readonly Random jitter = new();

	/// <summary>The one piece currently on its way out of the dresser, if any.</summary>
	private (uint ItemId, uint CabinetRow, string Name)? inFlight;

	/// <summary>Ticks between actions. ⚠ Every one of these is a request to the server.</summary>
	private const int Settle = 20;

	/// <summary>How long to wait for one step before giving up on that piece.</summary>
	private const int StepTimeoutTicks = 400;

	/// <summary>⚠ SelectYesno is generic. Answer a bounded number, never in a loop.</summary>
	private const int MaxYesno = 3;

	private readonly List<(uint ItemId, uint CabinetRow, string Name)> queue = new();
	private readonly List<(uint ItemId, uint CabinetRow, string Name)> batch = new();
	private readonly List<string> failed = new();

	private State state = State.Idle;
	private int settle;
	private int waited;
	private int yesno;

	public State Current => this.state;
	public string Status { get; private set; } = string.Empty;
	public int Stored { get; private set; }
	public IReadOnlyList<string> Failed => this.failed;
	public bool Running => this.state is State.Restoring or State.Storing;

	/// <summary>
	/// Begin, or explain why not.
	///
	/// ⚠⚠ Both stores have to be loaded, and they are loaded by LOOKING at them: the dresser only
	/// sends its contents when you open it, and the cabinet the same. In an inn room they stand next
	/// to each other, so "open both once" is one sentence to a player and two impossible reads
	/// otherwise.
	/// </summary>
	public void Start(DresserScan.Result r) {
		this.queue.Clear();
		this.batch.Clear();
		this.failed.Clear();
		this.inFlight = null;
		this.Stored = 0;
		this.yesno = 0;

		var mirage = MirageManager.Instance();
		if (mirage is null || !mirage->PrismBoxLoaded) {
			this.Fail("Open your glamour dresser once first, then try again.");
			return;
		}

		if (!UIState.Instance()->Cabinet.IsCabinetLoaded()) {
			this.Fail("Open your Armoire once first, then try again.");
			return;
		}

		if (r.ArmoireTransfer.Count == 0) {
			this.state = State.Done;
			this.Status = "Nothing in your dresser that the Armoire would take.";
			return;
		}

		if (Room() < 1) {
			this.Fail($"Needs at least {SlotsToLeaveFree + 1} free bag slots; make a little room.");
			return;
		}

		// ⚠⚠⚠ NEVER A PIECE YOU ARE WEARING. deserok, seeing the Armoire's own store window:
		// *"we just want to be careful to not put equipped items in, or armory chest items."*
		//
		// The sharp edge is that StoreCabinetItem takes a CABINET ROW, not a container and slot — it
		// names the item, not the copy — so the game chooses which one to consume and we cannot tell
		// it otherwise. For most Armoire gear that is moot, since you own exactly one. The case that
		// bites is owning two: one worn, one in the dresser. Store could plausibly take the worn one
		// and leave you stripped, still holding the dresser copy.
		//
		// ⭐ So the piece is simply not offered while it is on your body. Cheaper than proving what
		// the game prefers, and it turns an unknown into a non-issue rather than a hope.
		foreach (var piece in r.ArmoireTransfer) {
			if (Equipped(piece.ItemId)) {
				DresserLog.Step($"  {piece.Name}: you are wearing one, leaving it alone");
				continue;
			}

			this.queue.Add(piece);
		}

		if (this.queue.Count == 0) {
			this.state = State.Done;
			this.Status = "Nothing to move — you are wearing the pieces the Armoire would take.";
			return;
		}
		this.state = State.Restoring;
		this.settle = 0;
		this.waited = 0;
		this.Status = $"Moving {this.queue.Count} piece(s) to your Armoire...";

		DresserLog.Step($"=== ARMOIRE START: {this.queue.Count} piece(s) ===");
	}

	public void Stop(string why) {
		if (!this.Running) return;

		this.state = State.Failed;
		this.Status = why;
		DresserLog.Step($"ARMOIRE STOPPED: {why}");
	}

	public void Tick() {
		if (!this.Running) return;
		if (this.settle > 0) { this.settle--; return; }

		// ⚠⚠ NOT aborted on IsCabinetLoaded here, deliberately. The player has to CLOSE one window
		// and open the other to do this at all, so a flag that turns out to be window-scoped rather
		// than session-scoped would kill every run mid-way — and the failure would look like the
		// plugin breaking rather than like an assumption being wrong. The step timeout catches a real
		// loss of the cabinet and names it.
		//
		// ⚠ Answered before anything else, and bounded. A restore or a store can raise one, and an
		// unanswered prompt looks exactly like a step that never completed.
		if (this.yesno < MaxYesno && FireYes()) {
			this.yesno++;
			DresserLog.Trace("  fired: SelectYesno [0]");
			this.settle = Settle;
			return;
		}

		if (++this.waited > StepTimeoutTicks) {
			this.GiveUpOnCurrent("the game did not respond in time");
			return;
		}

		if (this.state == State.Restoring) this.TickRestore();
		else this.TickStore();
	}

	/// <summary>
	/// Pull a batch out of the dresser, up to what the bags can hold.
	///
	/// ⭐ A batch rather than one at a time because each round trip is a restore AND a store, and
	/// deserok's rule sizes it by what is actually free rather than by a constant somebody guessed.
	/// </summary>
	private void TickRestore() {
		// ⚠ One in flight at a time. See Pace().
		if (this.inFlight is { } flying) {
			if (!InBags(flying.ItemId)) return;

			DresserLog.Trace($"  landed: {flying.Name}");
			this.batch.Add(flying);
			this.inFlight = null;
			this.waited = 0;
			this.settle = Pace();
			return;
		}

		var free = DresserPacker.FreeBagSlots();
		var room = Math.Min(Math.Max(0, free - SlotsToLeaveFree), MaxBatch);

		// Gathered as much as this trip can carry, or there is nothing left to gather.
		if (this.batch.Count >= room || this.queue.Count == 0) {
			if (this.batch.Count == 0) { this.Finish(); return; }

			DresserLog.Step($"  storing {this.batch.Count} piece(s)");
			this.state = State.Storing;
			this.waited = 0;
			return;
		}

		var piece = this.queue[0];
		this.queue.RemoveAt(0);

		// ⭐ Already out. A piece the scan found in your bags needs no restore at all — it goes
		// straight to the store step, and costs no bag slot to gather because it already occupies one.
		if (InBags(piece.ItemId)) {
			this.batch.Add(piece);
			return;
		}

		var mirage = MirageManager.Instance();
		if (mirage is null || !mirage->PrismBoxLoaded) {
			this.Stop("lost sight of the dresser contents");
			return;
		}

		// ⚠⚠ The index is read HERE, in the tick that uses it. Removing an entry can move everything
		// after it, so an index remembered from the scan is a different item by the time it is used.
		var ids = mirage->PrismBoxItemIds;
		var index = -1;
		for (var i = 0; i < ids.Length; i++) {
			if (ids[i] != piece.ItemId) continue;
			index = i;
			break;
		}

		if (index < 0) {
			DresserLog.Step($"  {piece.Name}: no longer in the dresser, skipping");
			return;
		}

		if (!MirageManager.MemberFunctionPointers.RestorePrismBoxItem(mirage, (uint)index)) {
			DresserLog.Step($"  {piece.Name}: the game refused to restore it");
			this.failed.Add($"{piece.Name} (could not be taken out of the dresser)");
			return;
		}

		DresserLog.Trace($"  restore: {piece.Name} from index {index} ({free} free)");
		this.inFlight = piece;
		this.waited = 0;
		this.settle = Pace();
	}

	/// <summary>
	/// How long to wait before touching anything again.
	///
	/// ⭐⭐⭐ ONE ITEM AT A TIME, PACED. The first run pulled forty-seven pieces out of the dresser
	/// inside twenty milliseconds, and deserok stopped it for the right reason: *"lets limit to 1
	/// withdrawn item over n milliseconds, this seems super detectible if we allow all items out at
	/// once."* He is right, and it is not only about detection — a burst is also unreadable, unstoppable
	/// and unrecoverable. Forty-seven simultaneous requests cannot be watched, cannot be interrupted
	/// halfway, and if one goes wrong there is no telling which.
	///
	/// ⚠ Jittered, because a perfectly even beat is its own signature. The variation is small and
	/// costs nothing: speed was never a constraint here — the alternative is doing this by hand.
	/// </summary>
	private int Pace() => PaceTicks + this.jitter.Next(PaceJitter);

	/// <summary>
	/// Put the batch into the Armoire, one piece at a time, confirming each.
	///
	/// ⚠⚠ IsItemInCabinet is the only thing believed here. StoreCabinetItem returns a bool and the
	/// packer already learned what a bool is worth from StoreNewOutfit, which returned true and did
	/// nothing sixteen times in a row.
	/// </summary>
	private void TickStore() {
		if (this.batch.Count == 0) {
			this.state = State.Restoring;
			this.waited = 0;
			return;
		}

		var piece = this.batch[0];

		if (UIState.Instance()->Cabinet.IsItemInCabinet(piece.CabinetRow)) {
			DresserLog.Step($"  stored {piece.Name}");
			this.batch.RemoveAt(0);
			this.Stored++;
			this.Status = $"Moved {this.Stored} piece(s) to your Armoire...";
			this.waited = 0;
			this.settle = Settle;
			return;
		}

		// ⚠ Only ask once per settle window; the confirmation above is what decides it worked.
		UIState.Instance()->Cabinet.StoreCabinetItem(piece.CabinetRow);
		DresserLog.Trace($"  store: {piece.Name} (cabinet {piece.CabinetRow})");

		// ⚠ Paced like the restores, and for the same reasons. A store is a request to the server
		// too, and forty-seven of them in a burst is the same signature the restores were.
		this.settle = this.Pace();
	}

	/// <summary>Abandon whatever step stalled, and keep going with the rest.</summary>
	private void GiveUpOnCurrent(string why) {
		// ⚠ The in-flight restore is the thing that stalled, when there is one. Popping from the
		// batch instead would blame a piece that arrived perfectly well.
		if (this.inFlight is { } flying) {
			this.failed.Add($"{flying.Name} ({why})");
			DresserLog.Step($"  SKIPPED {flying.Name}: {why}");
			this.inFlight = null;
			this.waited = 0;
			this.yesno = 0;
			return;
		}

		if (this.batch.Count > 0) {
			var piece = this.batch[0];
			this.batch.RemoveAt(0);
			this.failed.Add($"{piece.Name} ({why})");
			DresserLog.Step($"  SKIPPED {piece.Name}: {why}");
		}

		this.waited = 0;
		this.yesno = 0;

		// ⚠ Pieces already restored are sitting in the bags. Said plainly at the end rather than
		// tidied away silently, because they are the player's to deal with and they can see them.
		if (this.batch.Count == 0 && this.queue.Count == 0 && this.inFlight is null) this.Finish();
	}

	private void Finish() {
		this.state = State.Done;

		this.Status = this.Stored == 0
			? "Nothing was moved to your Armoire."
			: $"Moved {this.Stored} piece(s) to your Armoire — {this.Stored} dresser slot(s) freed.";

		if (this.failed.Count > 0) this.Status += $" {this.failed.Count} could not be moved.";

		Plugin.Chat.Print($"Dresser: {this.Status}");
		DresserLog.Step($"=== ARMOIRE DONE: {this.Status} ===");

		foreach (var entry in this.failed) DresserLog.Step($"  failed: {entry}");
	}

	private void Fail(string why) {
		this.state = State.Failed;
		this.Status = why;
		Plugin.Chat.Print($"Dresser: {why}");
	}

	/// <summary>How many pieces we may pull out right now. ⚠ deserok's rule: free slots minus three.</summary>
	private static int Room() => Math.Max(0, DresserPacker.FreeBagSlots() - SlotsToLeaveFree);

	private static bool AllLanded(List<(uint ItemId, uint CabinetRow, string Name)> pieces) {
		foreach (var piece in pieces) {
			if (!InBags(piece.ItemId)) return false;
		}

		return true;
	}

	/// <summary>
	/// ⚠⚠⚠ THE ARMOURY COUNTS, AND LEAVING IT OUT IS WHAT STALLED THE FIRST RUN. Forty-seven
	/// pieces restored, not one store attempted, every one timing out in turn — because the game
	/// files restored EQUIPMENT into the matching armoury category when there is room, and this
	/// looked only in the bags. So nothing ever "landed" and the wait never ended.
	///
	/// The packer learned this exact thing on Skyworker's Boots and wrote it down; I did not carry it
	/// across to a file that does the same restore. A lesson recorded in one place is not a lesson
	/// applied in another.
	///
	/// ⚠ Safe to accept the armoury copy here, which it would NOT be in general: every id in this
	/// queue came out of the dresser moments ago, so anything found is the piece we just restored
	/// rather than gear that was already filed away. Equipped pieces are excluded before the queue is
	/// built, which is the case that actually needed guarding.
	/// </summary>
	private static bool InBags(uint itemId) {
		var manager = InventoryManager.Instance();
		if (manager is null) return false;

		foreach (var bag in Anywhere) {
			var page = manager->GetInventoryContainer(bag);
			if (page is null || !page->IsLoaded) continue;

			for (var i = 0; i < page->Size; i++) {
				var item = page->GetInventorySlot(i);
				if (item is not null && item->ItemId == itemId) return true;
			}
		}

		return false;
	}

	/// <summary>⚠ On your body right now. See the note in Start.</summary>
	private static bool Equipped(uint itemId) {
		var manager = InventoryManager.Instance();
		if (manager is null) return false;

		var page = manager->GetInventoryContainer(InventoryType.EquippedItems);
		if (page is null || !page->IsLoaded) return false;

		for (var i = 0; i < page->Size; i++) {
			var item = page->GetInventorySlot(i);
			if (item is not null && item->ItemId == itemId) return true;
		}

		return false;
	}

	/// <summary>⚠ Bags AND armoury: the two places a restore can arrive.</summary>
	private static readonly InventoryType[] Anywhere = {
		InventoryType.Inventory1, InventoryType.Inventory2,
		InventoryType.Inventory3, InventoryType.Inventory4,
		InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
		InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
		InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
		InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
	};

	private static bool FireYes() {
		var addon = Plugin.GameGui.GetAddonByName("SelectYesno", 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return false;

		var unit = (AtkUnitBase*)addon.Address;
		var values = stackalloc AtkValue[1];
		values[0].Type = AtkValueType.Int;
		values[0].Int = 0;

		unit->FireCallback(1, values, true);
		return true;
	}
}
