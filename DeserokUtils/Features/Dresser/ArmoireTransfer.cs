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

		this.queue.AddRange(r.ArmoireTransfer);
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

		if (!UIState.Instance()->Cabinet.IsCabinetLoaded()) {
			this.Stop("lost sight of the Armoire");
			return;
		}

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
		if (this.batch.Count > 0 && AllLanded(this.batch)) {
			this.state = State.Storing;
			this.waited = 0;
			return;
		}

		if (this.batch.Count > 0) {
			// Still arriving. ⚠ Never re-issue: a restore is a request, and asking twice for two
			// copies of something is how the packer once pulled a piece it did not mean to.
			return;
		}

		if (this.queue.Count == 0) {
			this.Finish();
			return;
		}

		var room = Room();
		if (room < 1) {
			this.Stop($"your bags are too full to continue; {this.Stored} moved so far");
			return;
		}

		var mirage = MirageManager.Instance();
		var ids = mirage->PrismBoxItemIds;

		while (this.batch.Count < room && this.queue.Count > 0) {
			var piece = this.queue[0];
			this.queue.RemoveAt(0);

			// ⚠⚠ The index is read HERE, not remembered from the scan. Removing an entry can move
			// everything after it, so an index is only ever valid in the tick that read it.
			var index = -1;
			for (var i = 0; i < ids.Length; i++) {
				if (ids[i] != piece.ItemId) continue;
				index = i;
				break;
			}

			if (index < 0) {
				DresserLog.Step($"  {piece.Name}: no longer in the dresser, skipping");
				continue;
			}

			if (!MirageManager.MemberFunctionPointers.RestorePrismBoxItem(mirage, (uint)index)) {
				DresserLog.Step($"  {piece.Name}: the game refused to restore it");
				this.failed.Add($"{piece.Name} (could not be taken out of the dresser)");
				continue;
			}

			DresserLog.Trace($"  restore: {piece.Name} from index {index}");
			this.batch.Add(piece);
		}

		this.waited = 0;
		this.settle = Settle;
	}

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
		this.settle = Settle;
	}

	/// <summary>Abandon whatever step stalled, and keep going with the rest.</summary>
	private void GiveUpOnCurrent(string why) {
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
		if (this.batch.Count == 0 && this.queue.Count == 0) this.Finish();
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
	/// ⚠ The bags only, unlike the packer's search. A restored piece the game filed into the armoury
	/// chest cannot be stored from there, so finding it would be worse than not finding it — it
	/// would look landed and then never store.
	/// </summary>
	private static bool InBags(uint itemId) {
		var manager = InventoryManager.Instance();
		if (manager is null) return false;

		foreach (var bag in Bags) {
			var page = manager->GetInventoryContainer(bag);
			if (page is null || !page->IsLoaded) continue;

			for (var i = 0; i < page->Size; i++) {
				var item = page->GetInventorySlot(i);
				if (item is not null && item->ItemId == itemId) return true;
			}
		}

		return false;
	}

	private static readonly InventoryType[] Bags = {
		InventoryType.Inventory1, InventoryType.Inventory2,
		InventoryType.Inventory3, InventoryType.Inventory4,
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
