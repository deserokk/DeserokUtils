using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Excel.Sheets;

namespace DeserokUtils.Features.Dresser;

internal sealed unsafe class DresserPacker {

	internal static readonly bool Enabled = true;

	private const int StepTimeoutTicks = 600;

	private const int SettleTicks = 20;

	internal enum State { Idle, Waiting, Restoring, Storing, Confirming, Loose, Duplicates, Done, Failed }

	private sealed record Job(
		uint SetItemId, string Name, List<uint> ItemIds, uint? ExistingIndex, int FromDresser,
		IReadOnlyList<int> Slots) {

		public int ExpectedDelta => (this.ExistingIndex is null ? 1 : 0) - this.FromDresser;

		public int MaxGain => this.ExistingIndex is null ? 1 : 0;
	}

	private readonly List<Job> queue = new();
	private int jobIndex;

	private State state = State.Idle;
	private int waited;
	private int settle;

	private readonly List<uint> pending = new();

	private readonly List<uint> storing = new();

	private readonly List<string> skipped = new();

	private readonly List<uint> duplicates = new();

	private int duplicatesPulled;

	private bool loggedAddons;

	private bool restoreIssued;
	private int restoreAttempts;
	private int restoreWait;

	private const int RestoreRetryTicks = 120;

	private const int MaxRestoreAttempts = 3;

	private int cogTarget = -1;

	private bool cogDone;

	private int cogAttempts;

	private const int MaxCogAttempts = 3;

	private bool cancelling;

	private int cancelAttempts;

	private int verifyWaits;

	private int placed;

	private const int VerifyWaitTicks = 60;

	private int tickSlot;

	private bool storePressed;

	private bool confirmPressed;

	private int yesnoAnswered;

	private const int MaxYesno = 3;

	private int menusAnswered;

	private int usedAtJobStart;

	private bool probedList;

	private bool probedMenu;

	private const int SetSlots = 11;

	private static IReadOnlyList<int> SetSlotIndices(uint setItemId) {
		var all = new List<int>();
		for (var i = 0; i < SetSlots; i++) all.Add(i);

		if (Plugin.Data.GetExcelSheet<MirageStoreSetItem>()?.GetRowOrDefault(setItemId)
			is not { } row) return all;

		var columns = new[] {
			row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId,
			row.Hands.RowId, row.Legs.RowId, row.Feet.RowId, row.Earrings.RowId,
			row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId,
		};

		var used = new List<int>();
		for (var i = 0; i < columns.Length; i++) {
			if (columns[i] != 0) used.Add(i);
		}

		return used.Count == 0 ? all : used;
	}

	private const int TickSettle = 4;

	public State Current => this.state;
	public string Status { get; private set; } = string.Empty;
	public int OutfitsPacked { get; private set; }

	public int OutfitsCreated { get; private set; }

	public int OutfitsExtended { get; private set; }
	public int SlotsFreed { get; private set; }

	private int usedAtStart;
	private int predicted;

	private int pass;

	private const int MaxPasses = 5;

	private int usedAtRunStart;

	private int packedThisPass;

	private Stage stage = Stage.Outfits;

	private enum Stage { Outfits, Loose }

	internal Action<DresserScan.Result>? Rescanned;

	public string? Verified { get; private set; }

	public IReadOnlyList<string> Skipped => this.skipped;

	public bool Running
		=> this.state is State.Waiting or State.Restoring or State.Storing
			or State.Confirming or State.Loose or State.Duplicates;

	public int LooseStored { get; private set; }

	public void Start(DresserScan.Result r) {

		if (!Enabled) {
			this.pass = 0;
			this.state = State.Failed;
			this.Status = "Packing is turned off in this build.";
			return;
		}

		this.queue.Clear();
		this.jobIndex = 0;

		var fresh = this.pass == 0;

		if (fresh) {
			this.stage = Stage.Outfits;
			this.OutfitsPacked = 0;
			this.OutfitsCreated = 0;
			this.OutfitsExtended = 0;
			this.SlotsFreed = 0;
			this.duplicatesPulled = 0;
		}

		this.skipped.Clear();
		this.duplicates.Clear();

		foreach (var d in r.Duplicates)
			for (var i = 1; i < d.Indices.Count; i++) this.duplicates.Add(d.ItemId);

		this.loose.Clear();

		if (this.stage == Stage.Loose)
			this.loose.AddRange(r.StoreLoose);
		this.storingLoose = null;
		if (fresh) this.LooseStored = 0;
		this.Verified = null;
		this.usedAtStart = r.Used;

		if (this.pass == 0) {
			this.pass = 1;
			this.usedAtRunStart = r.Used;
		}

		this.packedThisPass = 0;

		this.predicted = r.SlotsFromAdditions + r.SlotsFromNewOutfits + r.SlotsFromDuplicates;

		if (this.stage == Stage.Outfits)
		foreach (var a in r.Additions)
			this.queue.Add(new Job(a.OutfitItemId, a.OutfitName,
				a.Pieces.Select(p => p.ItemId).ToList(), a.OutfitIndex,
				a.Pieces.Count(p => p.Index != uint.MaxValue), SetSlotIndices(a.OutfitItemId)));

		if (this.stage == Stage.Outfits)
		foreach (var o in r.NewOutfits)
			this.queue.Add(new Job(o.SetItemId, o.SetName,
				o.Pieces.Select(p => p.ItemId).ToList(), null,
				o.Pieces.Count(p => p.Index != uint.MaxValue), SetSlotIndices(o.SetItemId)));

		var needed = this.queue.Count == 0 ? 0 : this.queue.Max(j => j.FromDresser);
		var free = FreeBagSlots();

		if (needed > free) {
			this.queue.Clear();
			this.pass = 0;
			this.state = State.Failed;
			this.Status = $"Needs {needed} free bag slot(s) for the biggest outfit; you have {free}. "
			            + "Make room and try again.";
			DresserLog.Step($"REFUSED: needs {needed} free slots, has {free}");
			Plugin.Chat.Print($"Dresser: {this.Status}");
			return;
		}

		if (this.stage == Stage.Outfits && this.queue.Count == 0 && r.StoreLoose.Count > 0) {
			this.stage = Stage.Loose;
			this.loose.AddRange(r.StoreLoose);
			DresserLog.Step("  no outfits to build; going straight to loose pieces");
		}

		if (this.queue.Count == 0 && this.loose.Count > 0) {
			this.state = State.Loose;
			this.waited = 0;
			this.settle = 0;
			this.Status = $"Storing {this.loose.Count} loose piece(s)...";
			DresserLog.Step($"=== PACK START: {this.loose.Count} loose piece(s), no outfits ===");
			return;
		}

		if (this.queue.Count == 0) {

			this.pass = 0;
			this.state = State.Done;
			this.Status = "Nothing to pack.";
			return;
		}

		this.state = State.Waiting;
		this.waited = 0;
		this.settle = 0;
		this.pending.Clear();
		this.storing.Clear();
		this.restoreIssued = false;
		this.loggedAddons = false;
		this.probedList = false;
		this.probedMenu = false;
		this.usedAtJobStart = UsedEntries();
		this.Status = $"Packing {this.queue.Count} outfit(s)...";

		DresserLog.Step($"=== PACK START: {this.queue.Count} job(s), dresser at {r.Used}, predicted {this.predicted} ===");
		foreach (var j in this.queue)
			DresserLog.Step($"  queued {(j.ExistingIndex is null ? "new" : "add-to")} {j.Name} ({j.ItemIds.Count} piece(s)) set={j.SetItemId}");
	}

	public void Stop(string why) {
		if (!this.Running) return;

		this.pass = 0;
		this.state = State.Failed;
		this.Status = why;
		DresserLog.Step($"STOPPED: {why}");
		Plugin.Chat.Print($"Dresser: stopped -- {why}");
	}

	public void Tick() {
		if (!this.Running) return;

		if (!DresserOpen()) {
			this.Stop("the glamour dresser closed");
			return;
		}

		if (this.settle > 0) { this.settle--; return; }

		if (this.state is not (State.Waiting or State.Restoring) && ++this.waited > StepTimeoutTicks) {

			if (this.state == State.Restoring && this.pending.Count > 0) this.DumpMissing(this.pending[0]);

			if (this.state == State.Duplicates) {
				DresserLog.Step("  duplicates: gave up waiting, leaving the rest");
				this.duplicates.Clear();
				this.waited = 0;
				return;
			}

			if (this.state == State.Loose) {
				var name = this.storingLoose?.Name ?? "a loose piece";
				DresserLog.Step($"  SKIPPED {name}: the game did not respond in time");
				this.skipped.Add($"{name} (the game did not respond in time)");
				this.storingLoose = null;
				this.looseYesno = 0;
				this.waited = 0;
				return;
			}

			this.SkipJob("the game did not respond in time");
			return;
		}

		var mirage = MirageManager.Instance();
		if (mirage is null || !mirage->PrismBoxLoaded) {
			this.Stop("lost sight of the dresser contents");
			return;
		}

		switch (this.state) {
			case State.Waiting: this.TickWaiting(); break;
			case State.Restoring: this.TickRestore(mirage); break;
			case State.Storing: this.TickStore(mirage); break;
			case State.Confirming: this.TickConfirming(); break;
			case State.Loose: this.TickLoose(mirage); break;
			case State.Duplicates: this.TickDuplicates(mirage); break;
		}
	}

	private void TickRestore(MirageManager* mirage) {
		if (this.pending.Count == 0) {
			this.state = State.Storing;
			this.waited = 0;
			return;
		}

		var want = this.pending[0];

		if (FindInBags(want, out var landedIn, out _)) {
			DresserLog.Trace($"  landed: {ItemName(want)} ({want}) in {landedIn}");
			this.pending.RemoveAt(0);
			this.restoreIssued = false;
			this.restoreAttempts = 0;
			this.restoreWait = 0;
			this.waited = 0;
			this.settle = SettleTicks;
			return;
		}

		if (this.restoreIssued && TryFire("SelectYesno", 0)) {
			DresserLog.Trace("  fired: SelectYesno [0] (yes, restore it anyway)");
			this.settle = SettleTicks;
			return;
		}

		if (this.restoreIssued) {
			if (++this.restoreWait < RestoreRetryTicks) return;

			this.restoreWait = 0;
			this.restoreIssued = false;

			if (++this.restoreAttempts >= MaxRestoreAttempts) {
				this.DumpMissing(want);
				this.SkipJob($"could not get {ItemName(want)} out of the dresser");
				return;
			}

			DresserLog.Trace($"  retrying restore of {ItemName(want)} (attempt {this.restoreAttempts + 1})");
			return;
		}

		var ids = mirage->PrismBoxItemIds;
		var index = -1;
		for (var i = 0; i < ids.Length; i++) {
			if (ids[i] != want) continue;
			index = i;
			break;
		}

		if (index < 0) {

			this.SkipJob($"could not find {ItemName(want)} in the dresser or your bags");
			return;
		}

		DresserLog.Trace($"  restore: {ItemName(want)} ({want}) from dresser index {index}");

		if (!MirageManager.MemberFunctionPointers.RestorePrismBoxItem(mirage, (uint)index)) {
			this.SkipJob($"the game refused to restore {ItemName(want)} -- inventory may be full");
			return;
		}

		this.restoreIssued = true;
		this.settle = SettleTicks;
	}

	private void TickStore(MirageManager* mirage) {
		var job = this.queue[this.jobIndex];

		if (job.ExistingIndex is null) {
			var ids = mirage->PrismBoxItemIds;
			for (var i = 0; i < ids.Length; i++) {
				if (ids[i] != job.SetItemId) continue;

				this.SkipJob($"{job.Name} already exists in your dresser");
				return;
			}
		}

		var sets = Plugin.Data.GetExcelSheet<MirageStoreSetItem>();
		if (sets?.GetRowOrDefault(job.SetItemId) is not { } row) {
			this.SkipJob($"lost the definition of {job.Name}");
			return;
		}

		var slotItems = new[] {
			row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId,
			row.Hands.RowId, row.Legs.RowId, row.Feet.RowId, row.Earrings.RowId,
			row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId,
		};

		var containers = stackalloc InventoryType[11];
		var slots = stackalloc ushort[11];
		for (var i = 0; i < 11; i++) { containers[i] = 0; slots[i] = 0; }

		var placed = 0;
		for (var slot = 0; slot < 11; slot++) {
			var itemId = slotItems[slot];
			if (itemId == 0 || !job.ItemIds.Contains(itemId)) continue;

			if (!FindInBags(itemId, out var container, out var bagSlot)) {
				this.SkipJob($"{ItemName(itemId)} is not in your bags");
				return;
			}

			containers[slot] = container;
			slots[slot] = bagSlot;
			placed++;
		}

		if (placed == 0) {
			this.SkipJob($"nothing left to store for {job.Name}");
			return;
		}

		var ok = job.ExistingIndex is { } existing
			? MirageManager.MemberFunctionPointers.StoreExistingOutfit(mirage, existing, containers, slots)
			: MirageManager.MemberFunctionPointers.StoreNewOutfit(mirage, job.SetItemId, containers, slots);

		this.placedSlots.Clear();
		for (var slot = 0; slot < 11; slot++) {
			if (containers[slot] != 0 || slots[slot] != 0) this.placedSlots.Add(slot);
		}

		DresserLog.Step($"  store {(job.ExistingIndex is null ? "new" : "existing@" + job.ExistingIndex)} "
			+ $"{job.Name} set={job.SetItemId} placed={placed} -> {ok}");
		for (var slot = 0; slot < 11; slot++) {
			if (containers[slot] == 0 && slots[slot] == 0) continue;
			DresserLog.Step($"      slot {slot,2} {DresserScan.SlotNames[slot],-10} container={containers[slot]} bagSlot={slots[slot]}");
		}

		if (!ok) {

			this.SkipJob($"the game refused to store {job.Name}");
			return;
		}

		this.placed = placed;

		this.storing.Clear();
		this.storing.AddRange(job.ItemIds);

		this.countBeforeStore.Clear();
		foreach (var itemId in job.ItemIds)
			this.countBeforeStore[itemId] = CountInBags(itemId);

		this.state = State.Confirming;
		this.loggedAddons = false;
		this.cogTarget = -1;
		this.cogAttempts = 0;
		this.cogDone = false;
		this.cancelling = false;
		this.cancelAttempts = 0;
		this.verifyWaits = 0;
		this.tickSlot = 0;
		this.storePressed = false;
		this.confirmPressed = false;
		this.yesnoAnswered = 0;
		this.menusAnswered = 0;
		this.settle = SettleTicks;
		this.waited = 0;
	}

	private void TickLoose(MirageManager* mirage) {

		if (this.storingLoose is { } sent) {
			if (FindInBags(sent.ItemId, out _, out _)) {

				if (this.looseYesno == 0) DresserProbe.Text("SelectYesno");

				if (this.looseYesno < MaxYesno && TryFire("SelectYesno", 0)) {
					this.looseYesno++;
					DresserLog.Trace($"  fired: SelectYesno [0] (store {sent.Name})");
					this.settle = SettleTicks;
				}

				return;
			}

			DresserLog.Step($"  stored {sent.Name} in the dresser");
			this.LooseStored++;

			this.packedThisPass++;
			this.storingLoose = null;
			this.waited = 0;
			this.settle = SettleTicks;
			return;
		}

		if (this.loose.Count == 0) {
			this.state = State.Duplicates;
			this.waited = 0;
			return;
		}

		var piece = this.loose[0];
		this.loose.RemoveAt(0);

		if (!Search(Bags, piece.ItemId, out var container, out var bagSlot)) {
			DresserLog.Step($"  {piece.Name}: not in the bags any more");
			this.skipped.Add($"{piece.Name} (not in your bags any more)");
			return;
		}

		var bag = System.Array.IndexOf(Bags, container);
		var listRow = bag < 0 ? -1 : DresserList.RowForBagSlot(bag, bagSlot);

		if (listRow < 0) {
			DresserLog.Step(
				$"  {piece.Name}: nothing in the glamour-ready list is at {container} slot {bagSlot}");
			this.skipped.Add($"{piece.Name} (the game is not offering it as glamour-ready)");
			return;
		}

		DresserLog.Step($"  storing {piece.Name} from row {listRow} ({container} slot {bagSlot})");
		TryFireCrystallize(listRow, 0);

		this.storingLoose = piece;
		this.looseYesno = 0;
		this.waited = 0;
		this.settle = SettleTicks;
	}

	private readonly List<(uint ItemId, string Name)> loose = new();
	private (uint ItemId, string Name)? storingLoose;
	private int looseYesno;

	private static bool TryFireCrystallize(int row, int action) {
		var addon = Plugin.GameGui.GetAddonByName("MiragePrismPrismBoxCrystallize", 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return false;

		var unit = (AtkUnitBase*)addon.Address;

		var values = stackalloc AtkValue[3];
		for (var i = 0; i < 3; i++) values[i].Type = AtkValueType.UInt;
		values[0].UInt = 14;
		values[1].UInt = (uint)row;
		values[2].UInt = (uint)action;

		unit->FireCallback(3, values, true);
		return true;
	}

	private void TickDuplicates(MirageManager* mirage) {
		if (this.duplicates.Count == 0) {
			this.state = State.Done;
			this.Finish();
			return;
		}

		if (FreeBagSlots() < 2) {
			DresserLog.Step("  duplicates: out of bag space, leaving the rest");
			this.duplicates.Clear();
			return;
		}

		var want = this.duplicates[0];

		if (this.restoreIssued && TryFire("SelectYesno", 0)) {
			DresserLog.Trace("  fired: SelectYesno [0] (yes, restore the duplicate)");
			this.settle = SettleTicks;
			return;
		}

		if (this.restoreIssued) {
			if (++this.restoreWait < RestoreRetryTicks) return;

			this.restoreWait = 0;
			this.restoreIssued = false;

			if (++this.restoreAttempts >= MaxRestoreAttempts) {
				DresserLog.Step($"  duplicate {ItemName(want)} would not come out; skipping");
				this.duplicates.RemoveAt(0);
				this.restoreAttempts = 0;
			}

			return;
		}

		var ids = mirage->PrismBoxItemIds;
		var index = -1;
		for (var i = 0; i < ids.Length; i++) {
			if (ids[i] != want) continue;
			index = i;
			break;
		}

		if (index < 0) {

			this.duplicates.RemoveAt(0);
			this.restoreAttempts = 0;
			return;
		}

		DresserLog.Trace($"  duplicate out: {ItemName(want)} from dresser index {index}");

		if (MirageManager.MemberFunctionPointers.RestorePrismBoxItem(mirage, (uint)index)) {
			this.restoreIssued = true;
			this.duplicatesPulled++;
			this.duplicates.RemoveAt(0);
			this.restoreAttempts = 0;
		}
		else {
			DresserLog.Step($"  duplicate {ItemName(want)} refused; skipping");
			this.duplicates.RemoveAt(0);
		}

		this.settle = SettleTicks;
	}

	private void Finish() {
		var after = new DresserScan().Scan();

		if (after.Problem is not null || !after.Loaded) {
			this.pass = 0;
			this.Status = $"Packed {this.OutfitsPacked} outfit(s). Could not re-check the dresser.";
			Plugin.Chat.Print($"Dresser: {this.Status}");
			return;
		}

		this.Rescanned?.Invoke(after);

		var outfitWork = after.Additions.Count + after.NewOutfits.Count;
		var looseWork = after.StoreLoose.Count;
		var stillToDo = this.stage == Stage.Outfits ? outfitWork : looseWork;

		if (stillToDo > 0 && this.packedThisPass > 0 && this.pass < MaxPasses) {
			this.pass++;
			DresserLog.Step($"=== PASS {this.pass} ({this.stage}): {stillToDo} job(s) still to do "
			              + $"after {this.packedThisPass} ===");

			this.Status = $"Packed {this.OutfitsPacked} so far; going again...";
			this.Start(after);
			return;
		}

		if (this.stage == Stage.Outfits && this.pass < MaxPasses) {
			this.stage = Stage.Loose;
			this.pass++;

			DresserLog.Step(
				$"=== OUTFITS DONE after {this.OutfitsPacked}; {looseWork} loose piece(s) to consider ===");

			this.Status = $"Packed {this.OutfitsPacked} outfit(s); storing loose pieces...";
			this.Start(after);
			return;
		}

		var actual = this.usedAtRunStart - after.Used;
		this.pass = 0;
		this.stage = Stage.Outfits;

		var parts = new List<string>();
		if (this.OutfitsCreated > 0) parts.Add(Plural(this.OutfitsCreated, "new outfit"));
		if (this.OutfitsExtended > 0) parts.Add($"{Plural(this.OutfitsExtended, "outfit")} extended");
		if (this.LooseStored > 0)
			parts.Add($"{Plural(this.LooseStored, "loose piece")} stored");

		if (this.duplicatesPulled > 0)
			parts.Add($"{Plural(this.duplicatesPulled, "duplicate")} back in your bags");

		this.Status = $"All done — {Plural(actual, "dresser slot")} recovered "
		            + $"({this.usedAtRunStart} → {after.Used})";

		if (parts.Count > 0) this.Status += ": " + string.Join(", ", parts);

		if (this.skipped.Count > 0)
			this.Status += $". {this.skipped.Count} could not be packed — see the Dresser tab";

		this.Status += ".";

		Plugin.Chat.Print($"Dresser: {this.Status}");

		foreach (var entry in this.skipped) DresserLog.Step($"  skipped: {entry}");

		this.Verified = this.Status;
		DresserLog.Step($"=== PACK DONE: {this.Status} ===");

		if (Plugin.Verbose) DresserLog.Write(after);
	}

	private void TickConfirming() {
		if (this.PiecesGone()) {
			var done = this.queue[this.jobIndex];
			DresserLog.Trace($"  confirmed: {done.Name} left the bags");

			this.OutfitsPacked++;
			this.packedThisPass++;
			if (done.ExistingIndex is null) this.OutfitsCreated++;
			else this.OutfitsExtended++;

			this.SlotsFreed += done.ExistingIndex is null ? this.placed - 1 : this.placed;

			this.NextJob(done.MaxGain);
			return;
		}

		var dialogUp = AddonVisible("MiragePrismPrismSetConvert");

		if (this.cancelling) {
			if (!dialogUp) {
				this.cancelling = false;
				this.SkipJob("the game opened the wrong outfit");
				return;
			}

			if (++this.cancelAttempts > 5) {
				this.Stop("could not close a dialog the game opened for the wrong outfit");
				return;
			}

			CancelDialog();
			DresserLog.Trace("  fired: MiragePrismPrismSetConvert [-2] (cancel)");
			this.settle = SettleTicks;
			return;
		}

		if (dialogUp && !this.cogDone) {
			var shown = SetConvertSetId();

			if (shown == 0 && ++this.verifyWaits < VerifyWaitTicks) return;

			this.cogDone = true;
			DresserProbe.Values("MiragePrismPrismSetConvert");
			DresserProbe.Text("MiragePrismPrismSetConvert");

			var job = this.queue[this.jobIndex];

			if (shown != job.SetItemId) {
				DresserLog.Step($"  WRONG SET: the dialog is for {ItemName(shown)} ({shown}), "
					+ $"wanted {job.Name} ({job.SetItemId}) -- cancelling, nothing committed");
				this.cancelling = true;
				this.cancelAttempts = 0;
				return;
			}

			DresserLog.Step($"  dialog confirmed as {job.Name} ({shown}) from row {this.cogTarget}");
		}

		if (this.yesnoAnswered < MaxYesno && TryFire("SelectYesno", 0)) {
			this.yesnoAnswered++;
			DresserLog.Trace("  fired: SelectYesno [0] (yes)");
			this.settle = SettleTicks;
			return;
		}

		if (!this.confirmPressed && TryFire("MiragePrismPrismSetConvertC", 0)) {
			this.confirmPressed = true;
			DresserLog.Trace("  fired: MiragePrismPrismSetConvertC [0] (confirm)");
			this.settle = SettleTicks;
			return;
		}

		if (AddonVisible("ContextIconMenu")) {

			if (!this.probedMenu) {
				this.probedMenu = true;
				DresserProbe.Values("ContextIconMenu");
			}

			DresserProbe.Text("ContextIconMenu");
			TryFireMenu("ContextIconMenu");
			this.menusAnswered++;
			DresserLog.Trace("  fired: ContextIconMenu [0,0,0,0] (take the first candidate)");
			this.settle = TickSettle;
			return;
		}

		if (!this.storePressed && dialogUp && this.tickSlot < SetSlots) {
			TryFire2("MiragePrismPrismSetConvert", 13, this.tickSlot);
			DresserLog.Trace(
				$"  fired: MiragePrismPrismSetConvert [13,{this.tickSlot}] "
				+ $"({DresserScan.SlotNames[this.tickSlot]})");
			this.tickSlot++;
			this.settle = TickSettle;
			return;
		}

		if (!this.storePressed && dialogUp && TryFire("MiragePrismPrismSetConvert", 14)) {
			this.storePressed = true;
			DresserLog.Step($"  commit {this.queue[this.jobIndex].Name}: "
				+ $"{this.menusAnswered} slot(s) picked from row {this.cogTarget}, "
				+ $"filled columns [{string.Join(", ", this.placedSlots)}]");
			this.settle = SettleTicks;
			return;
		}

		if (!this.cogDone) {
			if (this.cogTarget < 0) {
				if (!this.probedList) {
					this.probedList = true;
					DresserProbe.Values("MiragePrismPrismBoxCrystallize");
					DresserProbe.Text("MiragePrismPrismBoxCrystallize");
				}

				this.cogTarget = this.ResolveCogRow();

				if (this.cogTarget < 0) {
					this.SkipJob(this.WhyNotOffered());
					return;
				}
			}

			if (this.cogAttempts < MaxCogAttempts) {
				TryFireCogRow("MiragePrismPrismBoxCrystallize", this.cogTarget);
				this.cogAttempts++;
				DresserLog.Trace(
					$"  fired: MiragePrismPrismBoxCrystallize [14,{this.cogTarget},1] (cogwheel)");
				this.settle = SettleTicks;
				return;
			}
		}

		if (!this.loggedAddons) {
			this.loggedAddons = true;
			DresserLog.Trace("  stuck; no known dialog is open. Currently visible:");
			foreach (var name in VisibleAddonNames()) DresserLog.Trace($"        {name}");
		}
	}

	private int ResolveCogRow() {
		var job = this.queue[this.jobIndex];
		var items = Plugin.Data.GetExcelSheet<Item>();

		foreach (var itemId in job.ItemIds) {
			if (items?.GetRowOrDefault(itemId) is not { } item) continue;

			var row = DresserList.RowForIcon(item.Icon);
			if (row < 0) continue;

			DresserLog.Trace($"  list: {ItemName(itemId)} (icon {item.Icon}) is row {row}");
			return row;
		}

		DresserLog.Step($"  none of {job.Name}'s pieces are in the glamour-ready list "
			+ $"({DresserList.Rows().Count} row(s) showing)");
		return -1;
	}

	private string WhyNotOffered() {
		var job = this.queue[this.jobIndex];

		foreach (var itemId in job.ItemIds) {
			if (!Search(Armoury, itemId, out var where, out _)) continue;

			return $"{ItemName(itemId)} went to your {where} rather than your bags -- "
			     + "move it into your bags and run this again";
		}

		return $"the game is not offering {job.Name}'s pieces as glamour-ready "
		     + "(gear set registration hides them)";
	}

	private static uint SetConvertSetId() {
		var addon = Plugin.GameGui.GetAddonByName("MiragePrismPrismSetConvert", 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return 0;

		var unit = (AtkUnitBase*)addon.Address;
		if (unit->AtkValuesCount <= 4) return 0;

		var v = unit->AtkValues[4];
		return v.Type switch {
			AtkValueType.UInt => v.UInt,
			AtkValueType.Int => v.Int < 0 ? 0u : (uint)v.Int,
			_ => 0u,
		};
	}

	private static void CancelDialog() {
		var addon = Plugin.GameGui.GetAddonByName("MiragePrismPrismSetConvert", 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return;

		var unit = (AtkUnitBase*)addon.Address;
		var values = stackalloc AtkValue[1];
		values[0].Type = AtkValueType.Int;
		values[0].Int = -2;
		unit->FireCallback(1, values, false);
	}

	private static int UsedEntries() {
		var mirage = MirageManager.Instance();
		if (mirage is null || !mirage->PrismBoxLoaded) return -1;

		var ids = mirage->PrismBoxItemIds;
		var used = 0;
		for (var i = 0; i < ids.Length; i++) {
			if (ids[i] != 0) used++;
		}

		return used;
	}

	private bool MadeCollateral(int maxGain) {
		var now = UsedEntries();
		if (this.usedAtJobStart < 0 || now < 0) return false;

		var delta = now - this.usedAtJobStart;
		if (delta <= maxGain) return false;

		var name = this.jobIndex < this.queue.Count ? this.queue[this.jobIndex].Name : "?";
		this.pass = 0;
		this.state = State.Failed;
		this.Status = $"Stopped after {this.OutfitsPacked} outfit(s): the dresser gained "
		            + $"{Plural(delta - maxGain, "entry")} nobody asked for while packing {name}. "
		            + "Nothing else was touched.";

		DresserLog.Step($"ABORTED: {name} moved the dresser by {delta}, at most {maxGain} was "
		              + $"legitimate ({this.usedAtJobStart} -> {now})");
		Plugin.Chat.Print($"Dresser: {this.Status}");
		return true;
	}

	private void SkipJob(string why) {

		if (this.jobIndex >= this.queue.Count) {
			DresserLog.Step($"  BUG: SkipJob with no job to skip ({why}) in state {this.state}");
			this.waited = 0;
			return;
		}

		var name = this.queue[this.jobIndex].Name;

		DresserLog.Step($"SKIPPED {name}: {why}");
		this.skipped.Add($"{name} ({why})");

		this.pending.Clear();
		this.storing.Clear();
		this.restoreIssued = false;
		this.restoreAttempts = 0;
		this.restoreWait = 0;

		this.NextJob(
			this.jobIndex < this.queue.Count ? this.queue[this.jobIndex].MaxGain : 0);
	}

	private void NextJob(int maxGain = 0) {
		if (this.MadeCollateral(maxGain)) return;

		this.jobIndex++;
		this.cogTarget = -1;
		this.cogAttempts = 0;
		this.cogDone = false;
		this.cancelling = false;
		this.cancelAttempts = 0;
		this.verifyWaits = 0;
		this.tickSlot = 0;
		this.storePressed = false;
		this.confirmPressed = false;
		this.yesnoAnswered = 0;
		this.menusAnswered = 0;
		this.waited = 0;
		this.loggedAddons = false;
		this.usedAtJobStart = UsedEntries();

		if (this.jobIndex >= this.queue.Count) {

			this.state = this.loose.Count > 0 ? State.Loose : State.Duplicates;
			this.restoreIssued = false;
			this.restoreAttempts = 0;
			this.restoreWait = 0;
			this.Status = "Pulling out duplicates...";
			return;
		}

		this.state = State.Waiting;
		this.Status = $"Packing {this.jobIndex + 1} of {this.queue.Count}: {this.queue[this.jobIndex].Name}";
	}

	private bool PiecesGone() {

		if (this.SlotsFilled() == true)
			return true;

		foreach (var itemId in this.storing) {
			if (!this.countBeforeStore.TryGetValue(itemId, out var before)) {
				if (FindInBags(itemId, out _, out _)) return false;
				continue;
			}

			if (CountInBags(itemId) >= before) return false;
		}

		return true;
	}

	private bool? SlotsFilled() {
		if (this.placedSlots.Count == 0)
			return null;

		var mirage = MirageManager.Instance();
		if (mirage is null || !mirage->PrismBoxLoaded)
			return null;

		var job = this.jobIndex < this.queue.Count ? this.queue[this.jobIndex] : null;
		if (job is null)
			return null;

		var ids = mirage->PrismBoxItemIds;
		var index = -1;
		for (var i = 0; i < ids.Length; i++) {
			if (ids[i] != job.SetItemId) continue;
			index = i;
			break;
		}

		if (index < 0)
			return null;

		foreach (var slot in this.placedSlots) {
			if (!MirageManager.MemberFunctionPointers.IsSetSlotUnlocked(mirage, (uint)index, slot))
				return false;
		}

		return true;
	}

	private readonly List<int> placedSlots = new();

	private static int CountInBags(uint itemId) {
		var manager = InventoryManager.Instance();
		if (manager is null) return 0;

		var count = 0;
		foreach (var where in new[] { Bags, Armoury }) {
			foreach (var bag in where) {
				var page = manager->GetInventoryContainer(bag);
				if (page is null || !page->IsLoaded) continue;

				for (var i = 0; i < page->Size; i++) {
					var item = page->GetInventorySlot(i);
					if (item is not null && item->ItemId == itemId) count++;
				}
			}
		}

		return count;
	}

	private readonly Dictionary<uint, int> countBeforeStore = new();

	private static bool TryFire(string addonName, int value) {
		var addon = Plugin.GameGui.GetAddonByName(addonName, 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return false;

		var unit = (AtkUnitBase*)addon.Address;

		var values = stackalloc AtkValue[1];
		values[0].Type = AtkValueType.Int;
		values[0].Int = value;

		unit->FireCallback(1, values, true);
		return true;
	}

	private static readonly InventoryType[] Everywhere = {
		InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3,
		InventoryType.Inventory4, InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand,
		InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands,
		InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
		InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
		InventoryType.EquippedItems, InventoryType.ArmorySoulCrystal,
	};

	private void DumpMissing(uint itemId) {
		DresserLog.Step($"  MISSING: {ItemName(itemId)} ({itemId}) never landed. Searching everywhere:");

		var manager = InventoryManager.Instance();
		if (manager is null) { DresserLog.Trace("        no inventory manager"); return; }

		var found = false;
		foreach (var bag in Everywhere) {
			var page = manager->GetInventoryContainer(bag);
			if (page is null || !page->IsLoaded) continue;

			for (var i = 0; i < page->Size; i++) {
				var item = page->GetInventorySlot(i);
				if (item is null || item->ItemId != itemId) continue;

				DresserLog.Trace($"        found in {bag} slot {i}");
				found = true;
			}
		}

		var mirage = MirageManager.Instance();
		if (mirage is not null && mirage->PrismBoxLoaded) {
			var ids = mirage->PrismBoxItemIds;
			for (var i = 0; i < ids.Length; i++) {
				if (ids[i] != itemId) continue;
				DresserLog.Trace($"        still in the DRESSER at index {i}");
				found = true;
			}
		}

		if (!found) DresserLog.Trace("        nowhere at all — the restore was silently refused");

		DresserLog.Trace($"        free bag slots: {FreeBagSlots()}");
	}

	private static bool AddonVisible(string addonName) {
		var addon = Plugin.GameGui.GetAddonByName(addonName, 1);
		return addon.Address != nint.Zero && addon.IsVisible;
	}

	private static bool TryFireMenu(string addonName) {
		var addon = Plugin.GameGui.GetAddonByName(addonName, 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return false;

		var unit = (AtkUnitBase*)addon.Address;

		var values = stackalloc AtkValue[4];
		values[0].Type = AtkValueType.Int;
		values[0].Int = 0;
		values[1].Type = AtkValueType.Int;
		values[1].Int = 0;
		values[2].Type = AtkValueType.UInt;
		values[2].UInt = 0;
		values[3].Type = AtkValueType.UInt;
		values[3].UInt = 0;

		unit->FireCallback(4, values, true);
		return true;
	}

	private static bool TryFire2(string addonName, int a, int b) {
		var addon = Plugin.GameGui.GetAddonByName(addonName, 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return false;

		var unit = (AtkUnitBase*)addon.Address;

		var values = stackalloc AtkValue[2];
		values[0].Type = AtkValueType.Int;
		values[0].Int = a;
		values[1].Type = AtkValueType.UInt;
		values[1].UInt = (uint)b;

		unit->FireCallback(2, values, false);
		return true;
	}

	private static bool TryFireCogRow(string addonName, int row) {
		var addon = Plugin.GameGui.GetAddonByName(addonName, 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return false;

		var unit = (AtkUnitBase*)addon.Address;

		var values = stackalloc AtkValue[3];
		for (var i = 0; i < 3; i++) values[i].Type = AtkValueType.UInt;
		values[0].UInt = 14;
		values[1].UInt = (uint)row;
		values[2].UInt = 1;

		unit->FireCallback(3, values, true);
		return true;
	}

	private void TickWaiting() {
		var job = this.queue[this.jobIndex];
		var free = FreeBagSlots();

		if (free >= job.FromDresser) {
			DresserLog.Step($"job {this.jobIndex + 1}/{this.queue.Count}: {job.Name} "
			                + $"({job.ItemIds.Count} piece(s), {free} free slot(s))");

			this.pending.Clear();
			this.pending.AddRange(job.ItemIds);
			this.restoreIssued = false;
			this.restoreAttempts = 0;
			this.restoreWait = 0;
			this.state = State.Restoring;
			this.waited = 0;
			return;
		}

		this.Status = $"Waiting for {job.FromDresser - free} more free bag slot(s)...";
	}

	private static List<string> VisibleAddonNames() {
		var names = new List<string>();

		var stage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance();
		if (stage is null || stage->RaptureAtkUnitManager is null) return names;

		var units = &stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
		for (var i = 0; i < units->Count; i++) {
			var unit = units->Entries[i].Value;
			if (unit is null || !unit->IsVisible) continue;

			var name = unit->NameString;
			if (!string.IsNullOrEmpty(name)) names.Add(name);
		}

		names.Sort(StringComparer.Ordinal);
		return names;
	}

	internal static int FreeBagSlots() {
		var manager = InventoryManager.Instance();
		if (manager is null) return 0;

		var free = 0;
		foreach (var bag in Bags) {
			var page = manager->GetInventoryContainer(bag);
			if (page is null || !page->IsLoaded) continue;

			for (var i = 0; i < page->Size; i++) {
				var item = page->GetInventorySlot(i);
				if (item is null || item->ItemId == 0) free++;
			}
		}

		return free;
	}

	private static readonly InventoryType[] Bags = {
		InventoryType.Inventory1, InventoryType.Inventory2,
		InventoryType.Inventory3, InventoryType.Inventory4,
	};

	private static readonly InventoryType[] Armoury = {
		InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
		InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
		InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
		InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
	};

	private static bool FindInBags(uint itemId, out InventoryType container, out ushort slot)
		=> Search(Bags, itemId, out container, out slot)
		|| Search(Armoury, itemId, out container, out slot);

	private static bool Search(
		InventoryType[] where, uint itemId, out InventoryType container, out ushort slot) {
		container = 0;
		slot = 0;

		var manager = InventoryManager.Instance();
		if (manager is null) return false;

		foreach (var bag in where) {
			var page = manager->GetInventoryContainer(bag);
			if (page is null || !page->IsLoaded) continue;

			for (var i = 0; i < page->Size; i++) {
				var item = page->GetInventorySlot(i);
				if (item is null || item->ItemId != itemId) continue;

				container = bag;
				slot = (ushort)i;
				return true;
			}
		}

		return false;
	}

	private static bool DresserOpen() {
		var addon = Plugin.GameGui.GetAddonByName("MiragePrismPrismBox", 1);
		return addon.Address != nint.Zero && addon.IsVisible;
	}

	private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

	private static string ItemName(uint itemId)
		=> Plugin.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"#{itemId}";
}
