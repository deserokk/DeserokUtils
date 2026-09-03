using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Excel.Sheets;

namespace DeserokUtils.Features.Dresser;

/// <summary>
/// Packs loose pieces into outfits, one step per tick.
///
/// ⭐⭐⭐ THE DESIGN IS SHAPED BY ONE PERMISSION. deserok, 2026-09-03: *"speed is not a constraint,
/// if the process takes 5 minutes, that is fine, because the alternative takes hours."*
///
/// That removes the entire class of bug I was bracing for. With no pressure to be quick, this never
/// has to remember anything across a step: it **re-reads the dresser from scratch every time**, finds
/// the piece it wants by item id rather than by a remembered index, and confirms each action landed
/// before doing the next. Nothing can drift out of sync with the game, because nothing is carried.
///
/// ⚠⚠ The specific danger that buys off: restoring an item may or may not shift the indices of the
/// entries after it. I never found out, and I no longer need to — an index is only ever used in the
/// same tick it was read.
///
/// ## What it will not do
///
///  - Run unless the Glamour Dresser window is open, and it stops the moment it closes. That window
///    being open is the user standing there deliberately.
///  - Continue after anything unexpected. Every wait has a timeout and every failure stops the whole
///    run rather than skipping ahead.
/// </summary>
internal sealed unsafe class DresserPacker {
	/// <summary>Ticks to wait for the server before giving up on a step.</summary>
	private const int StepTimeoutTicks = 600;

	/// <summary>
	/// ⚠ A pause between actions. Not required by anything we can see — but every one of these is a
	/// request to the server, and hammering them as fast as the framework ticks is both rude and the
	/// most likely way to end up in a state the client did not expect. Costs seconds we were given.
	/// </summary>
	private const int SettleTicks = 20;

	internal enum State { Idle, Waiting, Restoring, Storing, Confirming, Duplicates, Done, Failed }

	/// <summary>One outfit to build: the set, and the item ids that should go into it.</summary>
	private sealed record Job(uint SetItemId, string Name, List<uint> ItemIds, uint? ExistingIndex);

	private readonly List<Job> queue = new();
	private int jobIndex;

	private State state = State.Idle;
	private int waited;
	private int settle;

	/// <summary>Item ids for the current job still to be pulled out of the dresser.</summary>
	private readonly List<uint> pending = new();

	/// <summary>Item ids handed to the game to store, still waiting to leave the bags.</summary>
	private readonly List<uint> storing = new();

	/// <summary>Outfits abandoned this run, and why. Reported at the end.</summary>
	private readonly List<string> skipped = new();

	/// <summary>
	/// Surplus copies to pull back out at the end, one entry per copy beyond the first.
	///
	/// ⭐ deserok, 2026-09-03: the scan counts duplicates but never did anything about them, and a
	/// duplicate sitting in the dresser is worth less than one in your bags — where it can be
	/// vendored or desynthesised. Freeing the slot and handing back the item are the same action.
	/// </summary>
	private readonly List<uint> duplicates = new();

	private int duplicatesPulled;

	/// <summary>Diagnostic: dump the open windows once per run, not once per tick.</summary>
	private bool loggedAddons;

	/// <summary>Whether a restore for the current piece is already in flight.</summary>
	private bool restoreIssued;
	private int restoreAttempts;
	private int restoreWait;

	/// <summary>How long to wait for a restore before assuming it was refused and asking again.</summary>
	private const int RestoreRetryTicks = 120;

	private const int MaxRestoreAttempts = 3;

	/// <summary>Whether the cogwheel has been pressed for the current job.</summary>
	/// <summary>Which row of the equipment list to try the cogwheel on next.</summary>
	private int cogRow;

	/// <summary>Whether the cogwheel has already opened the outfit dialog for this job.</summary>
	private bool cogDone;

	/// <summary>⚠ A ceiling: headers plus items, so comfortably more rows than any real list.</summary>
	private const int MaxCogRows = 40;

	/// <summary>How many of the outfit dialog's slots have been ticked for this job.</summary>
	private int tickSlot;

	/// <summary>Eleven, matching MirageStoreSetItem. Out-of-range indices are harmless.</summary>
	private const int SetSlots = 11;

	/// <summary>⚠ Shorter than the others: ticking a box needs no server round trip.</summary>
	private const int TickSettle = 4;


	public State Current => this.state;
	public string Status { get; private set; } = string.Empty;
	public int OutfitsPacked { get; private set; }

	/// <summary>Outfits that did not exist before this run.</summary>
	public int OutfitsCreated { get; private set; }

	/// <summary>
	/// Outfits that already existed and gained pieces.
	///
	/// ⚠ "Extended", never "completed". Completed would mean every slot of the set is filled, and
	/// usually it will not be — you do not own the earrings. Claiming otherwise is the kind of small
	/// untruth that makes somebody stop believing the rest of the figures.
	/// </summary>
	public int OutfitsExtended { get; private set; }
	public int SlotsFreed { get; private set; }

	/// <summary>Dresser occupancy when the run began, for the measured result at the end.</summary>
	private int usedAtStart;
	private int predicted;

	/// <summary>What the final re-scan actually measured, once there is one.</summary>
	public string? Verified { get; private set; }

	public bool Running
		=> this.state is State.Waiting or State.Restoring or State.Storing
			or State.Confirming or State.Duplicates;

	/// <summary>
	/// Queue everything a scan found. ⚠ Additions first: a piece joining an outfit that already
	/// exists frees its whole slot, where a new outfit keeps one for itself.
	/// </summary>
	public void Start(DresserScan.Result r) {
		this.queue.Clear();
		this.jobIndex = 0;
		this.OutfitsPacked = 0;
		this.OutfitsCreated = 0;
		this.OutfitsExtended = 0;
		this.SlotsFreed = 0;
		this.skipped.Clear();
		this.duplicates.Clear();
		this.duplicatesPulled = 0;

		// ⚠ One entry per SURPLUS copy: the first of each stays in the dresser.
		foreach (var d in r.Duplicates)
			for (var i = 1; i < d.Indices.Count; i++) this.duplicates.Add(d.ItemId);
		this.Verified = null;
		this.usedAtStart = r.Used;
		// ⚠ Duplicates count too. Leaving them out made the run always report a mismatch —
		// predicted 173, measured 179, the difference being exactly the six duplicates pulled.
		// A check that is always wrong by a known amount is a check nobody reads.
		this.predicted = r.SlotsFromAdditions + r.SlotsFromNewOutfits + r.SlotsFromDuplicates;

		foreach (var a in r.Additions)
			this.queue.Add(new Job(a.OutfitItemId, a.OutfitName,
				a.Pieces.Select(p => p.ItemId).ToList(), a.OutfitIndex));

		foreach (var o in r.NewOutfits)
			this.queue.Add(new Job(o.SetItemId, o.SetName,
				o.Pieces.Select(p => p.ItemId).ToList(), null));

		// ⚠⚠ Refuse before starting rather than stalling halfway. A run that stops mid-way leaves the
		// bags full of loose gear the player now has to sort out by hand; refusing leaves everything
		// exactly as it was. deserok, 2026-09-03, after the first run choked: check the largest job
		// up front and simply do not begin.
		var needed = this.queue.Count == 0 ? 0 : this.queue.Max(j => j.ItemIds.Count);
		var free = FreeBagSlots();

		if (needed > free) {
			this.queue.Clear();
			this.state = State.Failed;
			this.Status = $"Needs {needed} free bag slot(s) for the biggest outfit; you have {free}. "
			            + "Make room and try again.";
			DresserLog.Step($"REFUSED: needs {needed} free slots, has {free}");
			Plugin.Chat.Print($"Dresser: {this.Status}");
			return;
		}

		if (this.queue.Count == 0) {
			// ⭐ Idempotent, and it says so. Running this after a session with nothing new must do
			// nothing rather than churn -- it is meant to be run habitually.
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
		this.Status = $"Packing {this.queue.Count} outfit(s)...";

		DresserLog.Step($"=== PACK START: {this.queue.Count} job(s), dresser at {r.Used}, predicted {this.predicted} ===");
		foreach (var j in this.queue)
			DresserLog.Step($"  queued {(j.ExistingIndex is null ? "new" : "add-to")} {j.Name} ({j.ItemIds.Count} piece(s)) set={j.SetItemId}");
	}

	public void Stop(string why) {
		if (!this.Running) return;
		this.state = State.Failed;
		this.Status = why;
		DresserLog.Step($"STOPPED: {why}");
		Plugin.Chat.Print($"Dresser: stopped -- {why}");
	}

	public void Tick() {
		if (!this.Running) return;

		// ⚠⚠ The window being open is the only evidence that the player is standing at a dresser and
		// meant this. Losing it aborts rather than continuing blind.
		if (!DresserOpen()) {
			this.Stop("the glamour dresser closed");
			return;
		}

		if (this.settle > 0) { this.settle--; return; }

		// ⚠ The timeout is for the SERVER being slow, not for the player being slow. Waiting for bag
		// space is waiting on a person, and a person may well be off making room — killing the run
		// under them would be both rude and exactly when they least want to start over. It says what
		// it is waiting for, and Stop is always there.
		if (this.state is not (State.Waiting or State.Restoring) && ++this.waited > StepTimeoutTicks) {
			// ⭐⭐ Do not just give up — say where the thing actually is. The difference between "it
			// went somewhere I do not search", "the game refused it" and "it is still in the dresser"
			// is invisible from the outside, and dumping every container settles it in one run.
			if (this.state == State.Restoring && this.pending.Count > 0) this.DumpMissing(this.pending[0]);

			// ⭐⭐⭐ Skip this outfit, do not end the run. deserok, 2026-09-03: *"give the game some
			// leeway, who knows if a single lag hiccup can cause a stall, should always retry a few
			// times."* One stubborn outfit costing the other twenty-five is a far worse outcome than
			// finishing twenty-five and naming the one that did not.
			// ⚠ The duplicates phase has no job to skip — SkipJob there walked jobIndex past the
			// end and logged "SKIPPED ?". Abandon the remaining duplicates instead; the packing is
			// already banked and that was the point of the run.
			if (this.state == State.Duplicates) {
				DresserLog.Step("  duplicates: gave up waiting, leaving the rest");
				this.duplicates.Clear();
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
			case State.Duplicates: this.TickDuplicates(mirage); break;
		}
	}

	/// <summary>
	/// Pull the next piece out of the dresser.
	///
	/// ⚠⚠ A restore is a REQUEST, and for a moment afterwards the piece is in neither place: gone
	/// from the dresser, not yet in the bags. The first version treated that as "could not find it any
	/// more" and killed the run 186ms after issuing the very first restore.
	///
	/// ⚠⚠ The same staleness caused the opposite fault in the other direction — the dresser still
	/// listing an item that was already on its way out made it fire the restore a SECOND time, which
	/// with two copies of something would have pulled both. So a restore is issued once and then
	/// waited on; never re-issued because a read looks unchanged.
	///
	/// ⭐ Neither needs a special case for the failure that matters: if the piece truly never arrives,
	/// the step timeout says so.
	/// </summary>
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

		// ⚠⚠ A restore can raise a SelectYesno, and nobody was answering it — so the item stayed
		// put while we waited for it to arrive. Recorded from deserok restoring one by hand.
		//
		// ⚠ WHAT IS NOT KNOWN: when the prompt appears. Nineteen outfits restored through this API
		// with no dialog to answer, then Skyworker's Boots (a piece a glamour plate was using) hung
		// three runs in a row — which looked like "plates prompt". But the manual route asks for
		// ordinary items too, so that rule is not established. All that is demonstrated is that the
		// prompt sometimes appears and must be answered.
		//
		// ⭐ Which is why this is written as "answer it if it is there" rather than as a condition on
		// item kind. A guess about WHEN would be a guess; answering whatever is in front of us is
		// merely true. An earlier attempt encoded the guess by excluding plate-held pieces from the
		// scan entirely, and cost six recoverable slots for nothing.
		if (this.restoreIssued && TryFire("SelectYesno", 0)) {
			DresserLog.Trace("  fired: SelectYesno [0] (yes, restore it anyway)");
			this.settle = SettleTicks;
			return;
		}

		// ⚠⚠ A restore can be refused SILENTLY — no dialog, no false return, the piece simply stays
		// in the dresser. Measured 2026-09-03 on Sophist's Robe, 187ms after the previous piece
		// landed: the client's copy of the dresser was still catching up from that removal, so the
		// index we read pointed at nothing by the time the call arrived.
		//
		// ⭐ Rather than model when the array is settled — which would be a guess about someone
		// else's timing — just try again with a freshly read index. Three attempts, then give up and
		// name the piece. The wait before retrying is generous because the failure mode being
		// avoided is asking twice for something that was only slow.
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
			// ⚠ Not in the dresser and not in the bags. A piece that started in the bags would have
			// been picked up by the check at the top of this method, so this is genuinely gone.
			this.SkipJob($"could not find {ItemName(want)} in the dresser or your bags");
			return;
		}

		DresserLog.Trace($"  restore: {ItemName(want)} ({want}) from dresser index {index}");

		// ⚠ Returns false for a full inventory or a unique item already held. Both are the user's
		// problem to fix, and both are worth naming rather than retrying forever.
		if (!MirageManager.MemberFunctionPointers.RestorePrismBoxItem(mirage, (uint)index)) {
			this.SkipJob($"the game refused to restore {ItemName(want)} -- inventory may be full");
			return;
		}

		this.restoreIssued = true;
		this.settle = SettleTicks;
	}

	/// <summary>
	/// Deposit the gathered pieces as an outfit.
	///
	/// ⚠⚠ The arrays are indexed by the set's own slot order, not by the order we collected them:
	/// *"Must be in order of MirageStoreSetItem. Leftovers must be 0."* Getting this wrong builds the
	/// wrong outfit, which is the one genuinely destructive mistake available here -- recoverable by
	/// right-click restore, but still worth being careful about.
	/// </summary>
	private void TickStore(MirageManager* mirage) {
		var job = this.queue[this.jobIndex];

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

		DresserLog.Step($"  store {(job.ExistingIndex is null ? "new" : "existing@" + job.ExistingIndex)} "
			+ $"{job.Name} set={job.SetItemId} placed={placed} -> {ok}");
		for (var slot = 0; slot < 11; slot++) {
			if (containers[slot] == 0 && slots[slot] == 0) continue;
			DresserLog.Step($"      slot {slot,2} {DresserScan.SlotNames[slot],-10} container={containers[slot]} bagSlot={slots[slot]}");
		}

		if (!ok) {
			// ⚠ Refused. Try again from the top of this job — the pieces are in the bags, so a retry
			// costs a restore-check and nothing else.
			this.SkipJob($"the game refused to store {job.Name}");
			return;
		}

		this.OutfitsPacked++;
		if (job.ExistingIndex is null) this.OutfitsCreated++;
		else this.OutfitsExtended++;

		this.SlotsFreed += job.ExistingIndex is null ? placed - 1 : placed;

		// ⚠⚠ A true return means "sent to the server", NOT "done". The pieces are still in the bags
		// for a moment longer, and the first version of this moved straight on to the next outfit and
		// restored ITS pieces on top of them. Thirty-five outfits later the bags were full and the run
		// aborted. Confirm the departure before touching anything else.
		this.storing.Clear();
		this.storing.AddRange(job.ItemIds);

		this.state = State.Confirming;
		this.loggedAddons = false;
		this.cogRow = 0;
		this.cogDone = false;
		this.tickSlot = 0;
		this.settle = SettleTicks;
		this.waited = 0;
	}

	/// <summary>
	/// Pull surplus copies back into the bags, once the packing is done.
	///
	/// ⚠ Best effort. A duplicate that will not come out is not worth ending on — the outfits are
	/// already packed and that was the point of the run — so a failure here just moves to the next
	/// one and is counted at the end.
	/// </summary>
	private void TickDuplicates(MirageManager* mirage) {
		if (this.duplicates.Count == 0) {
			this.state = State.Done;
			this.Finish();
			return;
		}

		// ⚠ Never fill somebody's bags on their behalf. Stopping short is fine here; the packing is
		// already banked.
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
			// Already gone, or only one copy left. Either way there is nothing to do.
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

	/// <summary>
	/// Re-read the dresser and report what actually changed.
	///
	/// ⭐⭐ deserok's idea, and it is better than the running tally this used to print. The tally is
	/// what the code BELIEVES it did; a fresh count is what the game agrees happened. When the two
	/// disagree, something silently failed — and the user learns that instead of assuming a number
	/// that was only ever a prediction.
	/// </summary>
	private void Finish() {
		var after = new DresserScan().Scan();

		if (after.Problem is not null || !after.Loaded) {
			this.Status = $"Packed {this.OutfitsPacked} outfit(s). Could not re-check the dresser.";
			Plugin.Chat.Print($"Dresser: {this.Status}");
			return;
		}

		var actual = this.usedAtStart - after.Used;

		// ⭐ Say what was ACHIEVED, not what the machine did. "55 outfits packed" is a statement
		// about the tool; "179 slots recovered, 55 new outfits" is a statement about the dresser,
		// and the second is the one somebody wanted.
		var parts = new List<string>();
		if (this.OutfitsCreated > 0) parts.Add(Plural(this.OutfitsCreated, "new outfit"));
		if (this.OutfitsExtended > 0) parts.Add($"{Plural(this.OutfitsExtended, "outfit")} extended");
		if (this.duplicatesPulled > 0)
			parts.Add($"{Plural(this.duplicatesPulled, "duplicate")} back in your bags");

		this.Status = $"All done — {Plural(actual, "dresser slot")} recovered "
		            + $"({this.usedAtStart} → {after.Used})";

		if (parts.Count > 0) this.Status += ": " + string.Join(", ", parts) + ".";
		else this.Status += ".";

		Plugin.Chat.Print($"Dresser: {this.Status}");

		// ⚠ Only speaks when the prediction and the measurement disagree. Silence means they matched,
		// which is the ordinary case and does not need announcing.
		if (actual != this.predicted) {
			Plugin.Chat.Print(
				$"Dresser: expected {this.predicted}, measured {actual}. "
				+ "Run the scan again to see what is left.");
		}

		if (this.skipped.Count > 0) {
			Plugin.Chat.Print($"Dresser: {this.skipped.Count} outfit(s) skipped -- see the log.");
			foreach (var entry in this.skipped) DresserLog.Step($"  skipped: {entry}");
		}

		this.Verified = this.Status;
		DresserLog.Step($"=== PACK DONE: {this.Status} ===");
		DresserLog.Write(after);
	}

	/// <summary>
	/// Wait for the stored pieces to actually leave the bags, then move to the next outfit.
	/// </summary>
	/// <summary>
	/// Drive the commit, then wait for the pieces to leave the bags.
	///
	/// ⚠⚠ <c>StoreNewOutfit</c> returning true does NOT store anything — it fills in the outfit
	/// creation dialog and stops. The button still has to be pressed. That cost two aborted runs and
	/// sixteen stores that did nothing, and it is invisible from the return value.
	///
	/// ⭐⭐ The chain was RECORDED from deserok doing it by hand, never derived:
	/// <code>
	///   MiragePrismPrismSetConvert   [Int=14]   Store as Glamour
	///   MiragePrismPrismSetConvertC  [Int=0]    the confirmation
	/// </code>
	/// (A preceding <c>MiragePrismPrismBoxCrystallize [14,0,1]</c> is the cogwheel, which is what
	/// StoreNewOutfit stands in for.) ⚠ Inventing these values would not have failed loudly — it
	/// either does nothing or presses something else, and both look like "the feature is broken".
	/// </summary>
	/// <summary>
	/// Drive the commit: press whichever of the known dialogs is in front of us, in priority order,
	/// until the pieces have left the bags.
	///
	/// ⚠⚠ <c>StoreNewOutfit</c> returning true stores NOTHING. It fills the equipment list and
	/// stops; the buttons still have to be pressed. Two runs and sixteen "successful" stores were
	/// lost to that, and the return value gives no hint of it.
	///
	/// ⭐⭐ A LOOP, not a fixed sequence, and that is deliberate. The recorded chain was taken from
	/// deserok packing a NEW outfit — and adding to an EXISTING one asks an extra question first
	/// (*"an outfit glamour matching this gear is available, add to pre-existing?"*) that the
	/// recording could never have shown. Rather than encode one branch and be surprised by the next,
	/// this dismisses whatever known dialog is up and re-checks. A new branch then costs one line
	/// instead of a rebuild.
	///
	/// The buttons, every one recorded from a real click, none derived:
	/// <code>
	///   MiragePrismPrismBoxCrystallize  [UInt 14,0,1]  the cogwheel: make an outfit from this
	///   SelectYesno                     [Int 0]        yes, add to the existing outfit
	///   MiragePrismPrismSetConvert      [Int 14]       Store as Glamour
	///   MiragePrismPrismSetConvertC     [Int 0]        confirm
	/// </code>
	///
	/// ⚠ SelectYesno is a GENERIC dialog, so answering it is only safe inside this narrow window —
	/// mid-run, at a dresser, with a store outstanding. Never answer it anywhere else.
	/// </summary>
	private void TickConfirming() {
		if (this.PiecesGone()) {
			DresserLog.Trace($"  confirmed: {this.queue[this.jobIndex].Name} left the bags");
			this.NextJob();
			return;
		}

		// ⚠⚠ INNERMOST FIRST. The equipment list stays open for the whole flow, so checking it
		// before the dialogs matched on every single tick and re-opened the very prompt it was
		// supposed to be answering: 109 cogwheel presses and nothing else, with the yes/no visibly
		// flickering. Answer what is in front before touching what is behind it.
		if (TryFire("SelectYesno", 0)) {
			DresserLog.Trace("  fired: SelectYesno [0] (yes, add to the existing outfit)");
			this.settle = SettleTicks;
			return;
		}

		if (TryFire("MiragePrismPrismSetConvertC", 0)) {
			DresserLog.Trace("  fired: MiragePrismPrismSetConvertC [0] (confirm)");
			this.settle = SettleTicks;
			return;
		}

		// ⚠⚠ A slot is not a checkbox. [13, n] opens a PICKER — a ContextIconMenu of the items
		// eligible for that slot — and the choice is a second callback. deserok, watching it by hand:
		// *"left clicking then left clicking again to select the item fills it."* Firing [13, n]
		// eleven times and then pressing Store selected nothing at all, which presented as a hang.
		//
		// ⭐ Index 0 is right by construction rather than by luck: we restored ONLY the pieces this
		// outfit wants, so each slot's menu should hold exactly one candidate — ours.
		//
		// ⚠ The trailing value the recording carries (55316, 54909, 55714) is not an item id and I do
		// not know what it is. Following the in-house ContextMenu precedent in FcActionActivator,
		// which passes zeros there. If this turns out to matter it will show up as a wrong piece,
		// which is undoable, rather than as silence.
		if (AddonVisible("ContextIconMenu")) {
			TryFireMenu("ContextIconMenu");
			DresserLog.Trace("  fired: ContextIconMenu [0,0,0,0] (take the first candidate)");
			this.settle = TickSettle;
			return;
		}

		if (AddonVisible("MiragePrismPrismSetConvert") && this.tickSlot < SetSlots) {
			TryFire2("MiragePrismPrismSetConvert", 13, this.tickSlot);
			DresserLog.Trace($"  fired: MiragePrismPrismSetConvert [13,{this.tickSlot}] (open slot picker)");
			this.tickSlot++;
			this.settle = TickSettle;
			return;
		}

		if (TryFire("MiragePrismPrismSetConvert", 14)) {
			DresserLog.Trace("  fired: MiragePrismPrismSetConvert [14] (Store as Glamour)");
			this.settle = SettleTicks;
			return;
		}

		// ⚠ Once per job. The window it lives on never closes, so without this it is not a step but
		// a loop.
		// ⚠⚠ THE ROW IS NOT ALWAYS ZERO. That list interleaves slot headers with items — "Main
		// Hand", the weapon, "Head", the hat — so which row carries our piece depends on whatever
		// else is in the bags. Row 0 worked for fifty-five outfits by luck of what happened to be
		// there, then missed every single-piece job: deserok's recording of one by hand shows
		// [14, 4, 1].
		//
		// ⭐ Rather than model that layout, walk the rows until the outfit dialog opens. A cog on a
		// header does nothing, so a wrong row costs one tick. Same shape as answering whichever
		// dialog is in front of us: cheaper to try than to predict, and it cannot rot when the
		// list changes.
		// ⚠⚠ STOP WALKING once the dialog has been seen. Without this the walk resumes after the
		// store completes and SetConvert closes — cogging whatever item happens to sit on the next
		// row and committing an outfit for it. It made outfits nobody asked for and the dresser count
		// went UP. Found by deserok, 2026-09-03.
		//
		// ⭐ The row search is a search: it must end when it has found something, not when it runs
		// out of rows.
		if (AddonVisible("MiragePrismPrismSetConvert")) this.cogDone = true;

		if (!this.cogDone && this.cogRow < MaxCogRows) {
			TryFireCogRow("MiragePrismPrismBoxCrystallize", this.cogRow);
			DresserLog.Trace($"  fired: MiragePrismPrismBoxCrystallize [14,{this.cogRow},1] (cogwheel)");
			this.cogRow++;
			this.settle = TickSettle;
			return;
		}

		// Nothing known is up and the pieces are still here. Say what IS open, once, so a stall
		// names its own cause rather than needing to be described.
		if (!this.loggedAddons) {
			this.loggedAddons = true;
			DresserLog.Trace("  stuck; no known dialog is open. Currently visible:");
			foreach (var name in VisibleAddonNames()) DresserLog.Trace($"        {name}");
		}
	}

	/// <summary>
	/// Abandon the current outfit and move on.
	///
	/// ⚠ Its pieces are left loose in the bags. That is deliberately not tidied up automatically:
	/// putting them back is a restore-shaped operation of its own and could fail the same way, and
	/// the honest thing is to say what is where rather than to shuffle things further.
	/// </summary>
	private void SkipJob(string why) {
		var name = this.jobIndex < this.queue.Count ? this.queue[this.jobIndex].Name : "?";

		DresserLog.Step($"SKIPPED {name}: {why}");
		this.skipped.Add($"{name} ({why})");

		this.pending.Clear();
		this.storing.Clear();
		this.restoreIssued = false;
		this.restoreAttempts = 0;
		this.restoreWait = 0;

		this.NextJob();
	}

	private void NextJob() {
		this.jobIndex++;
		this.cogRow = 0;
		this.cogDone = false;
		this.tickSlot = 0;
		this.waited = 0;
		this.loggedAddons = false;

		if (this.jobIndex >= this.queue.Count) {
			this.state = State.Duplicates;
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
		foreach (var itemId in this.storing) {
			if (FindInBags(itemId, out _, out _)) return false;
		}

		return true;
	}

	/// <summary>
	/// Press one button on a named window, if that window is up. Returns whether it fired.
	/// </summary>
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

	/// <summary>Every container the game might have put a restored item into.</summary>
	private static readonly InventoryType[] Everywhere = {
		InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3,
		InventoryType.Inventory4, InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand,
		InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands,
		InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
		InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
		InventoryType.EquippedItems, InventoryType.ArmorySoulCrystal,
	};

	/// <summary>Where is it, really? Written to the log when a restore has failed to land.</summary>
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

	/// <summary>
	/// Choose the first entry of a context menu. ⚠ Zeros for the trailing values, matching the
	/// in-house ContextMenu pattern in FcActionActivator rather than a number copied without
	/// understanding it.
	/// </summary>
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

	/// <summary>Fire a two-value callback, as recorded: [Int, UInt].</summary>
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

	/// <summary>
	/// Fire the cogwheel on the first row of the equipment list.
	///
	/// ⚠⚠ Replayed verbatim from the recording: <c>[UInt=14, UInt=0, UInt=1]</c>. The 0 is the row
	/// and the 1 is something whose meaning I do not know — which is exactly why it is copied rather
	/// than reasoned about. Row 0 is right for us because every piece we restore for a job belongs to
	/// the same set, so whichever one is cogged leads to the same outfit.
	/// </summary>
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

	/// <summary>
	/// Hold until there is room for the next outfit's pieces.
	///
	/// ⭐ A safety net, not the gate. Start() already refuses to begin without room for the largest
	/// outfit, so this should never fire — it exists for the case where something else fills the bags
	/// mid-run, which is a player doing something else rather than a bug.
	/// </summary>
	private void TickWaiting() {
		var job = this.queue[this.jobIndex];
		var free = FreeBagSlots();

		if (free >= job.ItemIds.Count) {
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

		this.Status = $"Waiting for {job.ItemIds.Count - free} more free bag slot(s)...";
	}

	/// <summary>Every addon the game currently has on screen, for the diagnostic above.</summary>
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

	// ── Helpers ──────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// ⚠ Only the four ordinary bags. Never the armoury, never equipped gear, never a retainer --
	/// storing something a player is wearing or has filed away is not what anybody asked for.
	/// </summary>
	private static readonly InventoryType[] Bags = {
		InventoryType.Inventory1, InventoryType.Inventory2,
		InventoryType.Inventory3, InventoryType.Inventory4,
	};

	/// <summary>
	/// The armoury, searched only AFTER the bags.
	///
	/// ⚠⚠ Restored gear does not always land in the bags: the game files equipment into the
	/// matching armoury category when there is room. Searching only Inventory1-4 meant a piece could
	/// arrive perfectly well and never be seen, which presented as "the game did not respond in
	/// time" — measured on Skyworker's Boots, 2026-09-03, after nineteen outfits had packed fine.
	///
	/// ⚠ Bags first, deliberately. A piece we just restored is in the bags if it is anywhere; the
	/// armoury may also hold a DIFFERENT copy of the same item that the player is using, and
	/// preferring the bags means we consume the one we pulled rather than the one they kept.
	/// </summary>
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

	/// <summary>"1 outfit", "3 outfits". ⚠ Never "outfit(s)" — that reads as unfinished software.</summary>
	private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

	private static string ItemName(uint itemId)
		=> Plugin.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"#{itemId}";
}
