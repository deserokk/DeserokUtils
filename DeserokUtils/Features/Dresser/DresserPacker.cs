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
	/// <summary>
	/// WITHDRAWN. The packing does not run, and the Dresser tab is a scan again.
	///
	/// ⚠⚠⚠ deserok, 2026-09-03, after a run built a second Vanguard Attire of Scouting:
	/// *"before we added packing we had a stable version that simply checked your dresser, we might
	/// want to revert to that because this is a regression."* He is right. The scan has been correct
	/// and read-only since the day it was written; the packing has now damaged his dresser three
	/// times, and shipping a tool that sometimes wrecks the thing it is tidying is not a trade
	/// anybody made knowingly.
	///
	/// ⭐⭐ The code stays, because the run that prompted this is also the run that finally
	/// answered the question. See DresserProbe, and the note on TickConfirming: the cogwheel row is
	/// derivable and the dialog announces which set it is for. Neither was knowable before, both are
	/// now, and rebuilding the state machine from memory to use them would be worse than turning one
	/// flag back on.
	///
	/// ⚠ Turning it on again means the verification in TickConfirming first, not just this flag.
	/// </summary>
	/// ⚠ static readonly rather than const, so parking it does not compile the feature away into
	/// a wall of unreachable-code warnings that would train everyone to ignore warnings.
	internal static readonly bool Enabled = true;

	/// <summary>Ticks to wait for the server before giving up on a step.</summary>
	private const int StepTimeoutTicks = 600;

	/// <summary>
	/// ⚠ A pause between actions. Not required by anything we can see — but every one of these is a
	/// request to the server, and hammering them as fast as the framework ticks is both rude and the
	/// most likely way to end up in a state the client did not expect. Costs seconds we were given.
	/// </summary>
	private const int SettleTicks = 20;

	internal enum State { Idle, Waiting, Restoring, Storing, Confirming, Loose, Duplicates, Done, Failed }

	/// <summary>
	/// One outfit to build: the set, the item ids that should go into it, and how many of those
	/// were sitting in the DRESSER rather than in the bags.
	///
	/// ⭐⭐ That last number is not bookkeeping, it is the safety check. It says exactly how the
	/// dresser's entry count should move when this job finishes: minus one per piece taken out of it,
	/// plus one if a new outfit entry appears. Anything else means the packer pressed a button that
	/// did something nobody asked for — which is precisely the failure that made three empty
	/// "ghost" outfits and a run of duplicates on 2026-09-03, and which nothing was watching for.
	/// </summary>
	private sealed record Job(
		uint SetItemId, string Name, List<uint> ItemIds, uint? ExistingIndex, int FromDresser,
		int SlotCount) {
		/// <summary>How the dresser's entry count should move when this job succeeds.</summary>
		public int ExpectedDelta => (this.ExistingIndex is null ? 1 : 0) - this.FromDresser;
	}

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

	/// <summary>
	/// The row of the glamour-ready list holding one of this job's pieces. ⚠ -1 until it is read.
	///
	/// ⭐⭐⭐ READ, not guessed. This used to be row 0, and then a walk over every row until
	/// something happened — both of which open the outfit dialog for whatever item is sitting there,
	/// which is how outfits appeared for sets nobody asked about. DresserList reads it.
	/// </summary>
	private int cogTarget = -1;

	/// <summary>Whether the cogwheel has already opened the outfit dialog for this job.</summary>
	private bool cogDone;

	private int cogAttempts;

	/// <summary>⚠ The cog is a request; the dialog takes a moment. Three tries, then give up on the
	/// job rather than press a fourth time at something that is clearly not listening.</summary>
	private const int MaxCogAttempts = 3;

	/// <summary>
	/// Set when the dialog turned out to be for the wrong set and has to be dismissed before
	/// anything else happens.
	///
	/// ⚠ Not a straight SkipJob: leaving a stray dialog open means the NEXT job's first act is to
	/// find it already there, and the mess compounds. Close it, watch it close, then move on.
	/// </summary>
	private bool cancelling;

	private int cancelAttempts;

	/// <summary>Ticks spent waiting for the dialog to say which set it is about.</summary>
	private int verifyWaits;

	/// <summary>Pieces handed to the current store, counted once it is confirmed.</summary>
	private int placed;

	/// <summary>⚠ About half a second. Long enough for a slow frame, short enough to notice.</summary>
	private const int VerifyWaitTicks = 60;

	/// <summary>How many of the outfit dialog's slots have been ticked for this job.</summary>
	private int tickSlot;

	/// <summary>
	/// Whether "Store as Glamour" has already been pressed for this job.
	///
	/// ⚠⚠⚠ THE GHOST-OUTFIT BUG, AND IT WAS THIS LINE MISSING. The commit had no guard at all,
	/// so once the dialog stayed open — which is what happens when the cogwheel opened the wrong set
	/// and none of its slots could be filled — the loop simply pressed Store, confirmed, pressed
	/// Store, confirmed, six times in nine seconds. Every one of those committed an EMPTY outfit:
	/// an entry holding nothing, which cannot be removed because removing one requires restoring an
	/// item out of it and there is no item in it. Measured from the 19:19 run, 2026-09-03.
	///
	/// ⭐ Pressing a button twice is never the answer to it not having worked. If the pieces have
	/// not left the bags after one press, the step timeout says so and the job is skipped.
	/// </summary>
	private bool storePressed;

	/// <summary>Whether the confirmation has already been answered for this job. Same reason.</summary>
	private bool confirmPressed;

	/// <summary>
	/// How many yes/no prompts this job has answered.
	///
	/// ⚠ Capped rather than unlimited. SelectYesno is a GENERIC dialog: if one ever appears that we
	/// are not expecting, answering it forever is worse than stalling.
	/// </summary>
	private int yesnoAnswered;

	private const int MaxYesno = 3;

	/// <summary>Slot pickers actually answered for this job, for the log.</summary>
	private int menusAnswered;

	/// <summary>The dresser's entry count when the current job began.</summary>
	private int usedAtJobStart;

	/// <summary>The inventory list is dumped once a run, not once a job.</summary>
	private bool probedList;

	/// <summary>The slot picker likewise: its contents are the same shape every time.</summary>
	private bool probedMenu;

	/// <summary>Eleven, matching MirageStoreSetItem's columns. A hard ceiling on Job.SlotCount.</summary>
	private const int SetSlots = 11;

	/// <summary>
	/// How many slots the outfit dialog shows for a set: its columns that are not empty.
	///
	/// ⚠⚠ THE DIALOG'S SLOT INDEX IS DENSE, and is not the MirageStoreSetItem column number.
	/// Vanguard Attire of Aiming has nine pieces spread across columns 2..10, and the dialog answered
	/// [13,1] through [13,8] — nine rows numbered from zero, with the cogged piece already filled in.
	/// Firing past the end does nothing, which is why the old code got away with always firing all
	/// eleven, but there is no reason to knock on doors that are not there.
	/// </summary>
	private static int SetSlotCount(uint setItemId) {
		if (Plugin.Data.GetExcelSheet<MirageStoreSetItem>()?.GetRowOrDefault(setItemId)
			is not { } row) return SetSlots;

		var columns = new[] {
			row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId,
			row.Hands.RowId, row.Legs.RowId, row.Feet.RowId, row.Earrings.RowId,
			row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId,
		};

		var count = 0;
		foreach (var id in columns) {
			if (id != 0) count++;
		}

		return count == 0 ? SetSlots : count;
	}

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

	/// <summary>
	/// Which pass of the run this is.
	///
	/// ⭐⭐⭐ THE RUN KEEPS GOING UNTIL NOTHING MOVES. deserok, 2026-09-04, on being told
	/// "expected -3, measured -2. Run the scan again to see what is left": *"I mean, it saw it, it
	/// should just do the scan again."* He is right, and it is worse than a missing convenience —
	/// pressing Pack twice off ONE scan is what produced the failure he was looking at, because the
	/// second run tried to restore pieces the first had already packed away.
	///
	/// ⚠ A pass exists because packing changes the answer. Every outfit built frees slots and
	/// removes loose pieces, which can make a job possible that was not before. The scan that started
	/// the run is stale the moment the first outfit lands.
	/// </summary>
	private int pass;

	/// <summary>⚠ A ceiling. A pass that packs nothing stops the run anyway; this is for safety.</summary>
	private const int MaxPasses = 5;

	/// <summary>Dresser occupancy at the very start, across all passes.</summary>
	private int usedAtRunStart;

	/// <summary>Outfits packed in THIS pass, which is the loop's stopping condition.</summary>
	private int packedThisPass;

	/// <summary>
	/// Hands a fresh scan back to whoever owns this packer.
	///
	/// ⚠ Without it the Dresser tab and its Pack button keep showing the scan from BEFORE the run,
	/// so pressing Pack again re-queues work that is already done.
	/// </summary>
	internal Action<DresserScan.Result>? Rescanned;

	/// <summary>What the final re-scan actually measured, once there is one.</summary>
	public string? Verified { get; private set; }

	/// <summary>
	/// What could not be packed, and why, for the tab to show.
	///
	/// ⚠ The chat line says "see the Dresser tab", and until this existed that was a promise the
	/// UI did not keep — the reasons were in the log file, which is exactly the place somebody who
	/// does not read logs was being sent away from.
	/// </summary>
	public IReadOnlyList<string> Skipped => this.skipped;

	public bool Running
		=> this.state is State.Waiting or State.Restoring or State.Storing
			or State.Confirming or State.Loose or State.Duplicates;

	/// <summary>Loose pieces stored into the dresser this run. ⚠ Phase two.</summary>
	public int LooseStored { get; private set; }

	/// <summary>
	/// Queue everything a scan found. ⚠ Additions first: a piece joining an outfit that already
	/// exists frees its whole slot, where a new outfit keeps one for itself.
	/// </summary>
	public void Start(DresserScan.Result r) {
		// ⚠ The last line of defence, not the first. Both buttons are gone, but a packer that can
		// still be started by anything at all is a packer that will be.
		if (!Enabled) {
			this.pass = 0;
			this.state = State.Failed;
			this.Status = "Packing is turned off in this build.";
			return;
		}

		this.queue.Clear();
		this.jobIndex = 0;

		// ⚠⚠ THE TALLIES SURVIVE A PASS. pass == 0 means a run somebody started; anything else is
		// this packer calling itself after a re-scan, and zeroing the totals there would report only
		// the last pass's work — "1 new outfit" at the end of a run that built nine.
		var fresh = this.pass == 0;

		if (fresh) {
			this.OutfitsPacked = 0;
			this.OutfitsCreated = 0;
			this.OutfitsExtended = 0;
			this.SlotsFreed = 0;
			this.duplicatesPulled = 0;
		}

		// ⚠ These two are per-pass by nature: the skip list describes THIS attempt, and duplicates
		// are re-derived from the scan that just ran.
		this.skipped.Clear();
		this.duplicates.Clear();

		// ⚠ One entry per SURPLUS copy: the first of each stays in the dresser.
		foreach (var d in r.Duplicates)
			for (var i = 1; i < d.Indices.Count; i++) this.duplicates.Add(d.ItemId);

		this.loose.Clear();
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
		// ⚠ Duplicates count too. Leaving them out made the run always report a mismatch —
		// predicted 173, measured 179, the difference being exactly the six duplicates pulled.
		// A check that is always wrong by a known amount is a check nobody reads.
		this.predicted = r.SlotsFromAdditions + r.SlotsFromNewOutfits + r.SlotsFromDuplicates;

		// ⚠ FromBags is uint.MaxValue — see DresserScan. A piece that was already in the bags costs
		// the dresser nothing to take, so it must not be counted as a slot the job will free.
		foreach (var a in r.Additions)
			this.queue.Add(new Job(a.OutfitItemId, a.OutfitName,
				a.Pieces.Select(p => p.ItemId).ToList(), a.OutfitIndex,
				a.Pieces.Count(p => p.Index != uint.MaxValue), SetSlotCount(a.OutfitItemId)));

		foreach (var o in r.NewOutfits)
			this.queue.Add(new Job(o.SetItemId, o.SetName,
				o.Pieces.Select(p => p.ItemId).ToList(), null,
				o.Pieces.Count(p => p.Index != uint.MaxValue), SetSlotCount(o.SetItemId)));

		// ⚠⚠ Refuse before starting rather than stalling halfway. A run that stops mid-way leaves the
		// bags full of loose gear the player now has to sort out by hand; refusing leaves everything
		// exactly as it was. deserok, 2026-09-03, after the first run choked: check the largest job
		// up front and simply do not begin.
		// ⚠⚠ FromDresser, not ItemIds.Count. Only pieces that have to be TAKEN OUT of the dresser
		// need somewhere to land; ones already in the bags are already landed. Counting all of them
		// made a nine-piece outfit you already hold demand nine free slots to move nothing.
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

		if (this.queue.Count == 0 && this.loose.Count > 0) {
			this.state = State.Loose;
			this.waited = 0;
			this.settle = 0;
			this.Status = $"Storing {this.loose.Count} loose piece(s)...";
			DresserLog.Step($"=== PACK START: {this.loose.Count} loose piece(s), no outfits ===");
			return;
		}

		if (this.queue.Count == 0) {
			// ⭐ Idempotent, and it says so. Running this after a session with nothing new must do
			// nothing rather than churn -- it is meant to be run habitually.
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

		// ⚠ Ends the whole run, not just this pass. Stopping should stop.
		this.pass = 0;
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

			// ⚠⚠⚠ THE SAME BUG AS ABOVE, IN A PHASE THAT DID NOT EXIST WHEN IT WAS FIXED. There is
			// no JOB in phase two — there are loose pieces — so SkipJob logs "SKIPPED ?" and walks
			// jobIndex past the end of the queue, which sends it straight back here. Seven times in
			// forty seconds, with the status stuck on "Pulling out duplicates...".
			//
			// ⭐ Abandon the piece, keep the rest. One stubborn item must not cost the other thirty,
			// which is the same rule the outfit jobs get.
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

		// ⚠⚠⚠ DOES THIS OUTFIT ALREADY EXIST, RIGHT NOW? The scan answers that when it runs, and
		// a queue is a photograph of an answer. Anything that changes the dresser between the scan
		// and this moment — an Armoire transfer, a previous job, the player — can make a "new outfit"
		// job into a duplicate, and the packer would build it without hesitating.
		//
		// ⚠ That is not hypothetical. 2026-09-05: a Pack pressed after an Armoire transfer built a
		// second Boulevardier's Attire beside the one already there, because the queue still believed
		// there was none. The transfer now hands back a fresh scan, which fixes the cause — this
		// checks the fact instead of trusting the paperwork, because there will be another cause.
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

		// ⚠⚠ NOT COUNTED HERE. A true return means "sent to the server", and the tally used to go
		// up on it — so a job that was sent and then skipped still showed in the summary. Measured
		// 2026-09-03: seven outfits packed, one skipped, "8 new outfits" reported. The count moves to
		// the confirmation, where the pieces have demonstrably left the bags.
		this.placed = placed;

		// ⚠⚠ A true return means "sent to the server", NOT "done". The pieces are still in the bags
		// for a moment longer, and the first version of this moved straight on to the next outfit and
		// restored ITS pieces on top of them. Thirty-five outfits later the bags were full and the run
		// aborted. Confirm the departure before touching anything else.
		this.storing.Clear();
		this.storing.AddRange(job.ItemIds);

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

	/// <summary>
	/// Pull surplus copies back into the bags, once the packing is done.
	///
	/// ⚠ Best effort. A duplicate that will not come out is not worth ending on — the outfits are
	/// already packed and that was the point of the run — so a failure here just moves to the next
	/// one and is counted at the end.
	/// </summary>
	/// <summary>
	/// Phase two: put loose gear into the dresser, plainly.
	///
	/// ⭐⭐⭐ THE ORIGINAL POINT OF THE WHOLE FEATURE, and it was missing until 2026-09-05.
	/// Everything before this could only form OUTFITS, so a piece belonging to no set, or to a set
	/// you own nothing else of, was looked at and left in your bags. deserok: *"The entire spec was
	/// 'save me from having to sift through what exists'."* Sifting is exactly what you still had to
	/// do for anything phase one could not use.
	///
	/// ⭐⭐ The call is one flag away from the one we already had:
	/// <code>
	///   MiragePrismPrismBoxCrystallize [14, row, 1]   the cogwheel — store as an outfit
	///   MiragePrismPrismBoxCrystallize [14, row, 0]   store it plainly
	/// </code>
	/// Recorded from deserok clicking one item's name rather than its cog. It also explains a
	/// [14, 0, 0] in the very first recording three days ago that nothing could account for at the
	/// time — that was a plain store, seen before we knew there was such a thing.
	///
	/// ⚠ It raises a SelectYesno every time, which is answered here rather than assumed away.
	/// </summary>
	private void TickLoose(MirageManager* mirage) {
		// The one in flight has arrived when it is no longer in the bags.
		if (this.storingLoose is { } sent) {
			if (FindInBags(sent.ItemId, out _, out _)) {
				// ⚠ The prompt is part of the store, not an interruption to it.
				// ⚠⚠ READ IT BEFORE ANSWERING IT, under Verbose. Phase two fires [14, row, 0] with no
				// check on what is actually on that row — if the row is wrong it stores a DIFFERENT
				// item and says nothing, which is the ghost-outfit lesson in a new place. The
				// confirmation names the item, so the log will say which one we were really agreeing
				// to the next time a piece fails to leave the bags.
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

		// ⚠ Read the row now. The list is rebuilt every time something leaves it, so a row number
		// from a moment ago is a different item.
		var items = Plugin.Data.GetExcelSheet<Item>();
		if (items?.GetRowOrDefault(piece.ItemId) is not { } row) return;

		var listRow = DresserList.RowForIcon(row.Icon);
		if (listRow < 0) {
			DresserLog.Step($"  {piece.Name}: the game is not offering it as glamour-ready");
			this.skipped.Add($"{piece.Name} (not offered as glamour-ready)");
			return;
		}

		DresserLog.Step($"  storing {piece.Name} from row {listRow}");
		TryFireCrystallize(listRow, 0);

		this.storingLoose = piece;
		this.looseYesno = 0;
		this.waited = 0;
		this.settle = SettleTicks;
	}

	private readonly List<(uint ItemId, string Name)> loose = new();
	private (uint ItemId, string Name)? storingLoose;
	private int looseYesno;

	/// <summary>
	/// Press a row of the glamour-ready list. ⚠ <paramref name="action"/> is 1 for the cogwheel
	/// (store as an outfit) and 0 for a plain store — recorded, not derived.
	/// </summary>
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
			this.pass = 0;
			this.Status = $"Packed {this.OutfitsPacked} outfit(s). Could not re-check the dresser.";
			Plugin.Chat.Print($"Dresser: {this.Status}");
			return;
		}

		// ⚠⚠ HAND THE FRESH SCAN BACK BEFORE ANYTHING ELSE. The tab and its Pack button otherwise
		// keep showing the scan from before the run — which is exactly how deserok hit this: pressing
		// Pack twice off ONE scan made the second run try to restore pieces the first had already
		// packed, and report them as missing.
		this.Rescanned?.Invoke(after);

		// ⭐⭐⭐ GO AGAIN IF THE LAST PASS ACHIEVED SOMETHING. Packing changes the answer: every
		// outfit built frees slots and consumes loose pieces, which can make a job possible that was
		// not before. Telling somebody "run the scan again to see what is left" was the tool asking
		// them to do a thing it had already done — the fresh scan is right here.
		//
		// ⚠ The stopping condition is a pass that packs NOTHING, not an empty queue. Some jobs can
		// never succeed — a piece the game filed into the armoury, a set whose dialog opens wrong —
		// and those would otherwise be retried forever.
		// ⚠⚠ StoreLoose COUNTS. Without it the multi-pass loop stops the moment no outfits are
		// left, even with a bag full of loose gear phase two has not touched — the same one-sided
		// question that has now produced four separate bugs tonight, asked in a fifth place.
		var stillToDo = after.Additions.Count + after.NewOutfits.Count + after.StoreLoose.Count;

		if (stillToDo > 0 && this.packedThisPass > 0 && this.pass < MaxPasses) {
			this.pass++;
			DresserLog.Step($"=== PASS {this.pass}: {stillToDo} job(s) still to do "
			              + $"after packing {this.packedThisPass} ===");

			this.Status = $"Packed {this.OutfitsPacked} so far; going again...";
			this.Start(after);
			return;
		}

		var actual = this.usedAtRunStart - after.Used;
		this.pass = 0;

		// ⭐ Say what was ACHIEVED, not what the machine did. "55 outfits packed" is a statement
		// about the tool; "179 slots recovered, 55 new outfits" is a statement about the dresser,
		// and the second is the one somebody wanted.
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

		// ⭐ Folded into the same sentence rather than a line of its own. A run that packs
		// twenty-six outfits and cannot do two is a success with a footnote, and it should read like
		// one — not like two separate announcements of equal weight.
		if (this.skipped.Count > 0)
			this.Status += $". {this.skipped.Count} could not be packed — see the Dresser tab";

		this.Status += ".";

		// ⚠⚠ ONE LINE FOR THE WHOLE RUN, however many passes it took. This used to print the
		// result, then the prediction, then the skip count — three lines for one button, on somebody
		// else's screen, every time.
		Plugin.Chat.Print($"Dresser: {this.Status}");

		// ⚠⚠ THE PREDICTION LINE IS GONE. It said "expected -3, measured -2. Run the scan again to
		// see what is left" — which is a number nobody can act on followed by an instruction the tool
		// should have followed itself. It now does, so a mismatch resolves into another pass or into
		// the skip list below, both of which say something useful.
		foreach (var entry in this.skipped) DresserLog.Step($"  skipped: {entry}");

		this.Verified = this.Status;
		DresserLog.Step($"=== PACK DONE: {this.Status} ===");

		// ⚠ Behind Verbose with the rest of the dumps. The outcome line above always logs.
		if (Plugin.Verbose) DresserLog.Write(after);
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
			var done = this.queue[this.jobIndex];
			DresserLog.Trace($"  confirmed: {done.Name} left the bags");

			// ⭐ Here, and only here. The pieces are gone from the bags, which is the first moment
			// anything is known rather than assumed.
			this.OutfitsPacked++;
			this.packedThisPass++;
			if (done.ExistingIndex is null) this.OutfitsCreated++;
			else this.OutfitsExtended++;

			this.SlotsFreed += done.ExistingIndex is null ? this.placed - 1 : this.placed;

			this.NextJob(done.ExpectedDelta);
			return;
		}

		// ⭐⭐ FIRST, before any branch can return. The old code set this AFTER the "Store as Glamour"
		// press — which fires on every tick the dialog is open — so the line was never once reached and
		// the row walk it was meant to stop kept walking. Dead code that reads like a fix is worse than
		// no fix: it makes the bug look already handled.
		var dialogUp = AddonVisible("MiragePrismPrismSetConvert");

		// ⚠ Dismissing a wrong dialog comes before everything, including answering prompts — the
		// whole point is to touch nothing further.
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

			// ⚠ Visible and filled in are not the same instant. A zero here on the first tick or two
			// is the window having opened before the server said what it is about; a zero that never
			// resolves is the field having moved, which a patch could do. Waiting a moment tells the
			// two apart, and both end safely — one proceeds, the other cancels.
			if (shown == 0 && ++this.verifyWaits < VerifyWaitTicks) return;

			this.cogDone = true;
			DresserProbe.Values("MiragePrismPrismSetConvert");
			DresserProbe.Text("MiragePrismPrismSetConvert");

			// ⭐⭐⭐ THE CHECK THAT WAS MISSING, and it is one comparison. The dialog says which
			// set it is about; until now nothing read it, so the packer would happily fill in and
			// commit whatever had opened. Measured 2026-09-03, 19:51: job two asked for 45314,
			// Boulevardier's Attire, and the dialog that opened said 51649, Vanguard Attire of
			// Scouting — which is the duplicate that landed in deserok's dresser.
			//
			// ⚠ A zero here means the value could not be read at all, which after a patch is
			// exactly what a moved field looks like. Treated as a failure, deliberately: skipping
			// every job and saying so is a far better way to find that out than committing them.
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

		// ⚠⚠ INNERMOST FIRST. The equipment list stays open for the whole flow, so checking it
		// before the dialogs matched on every single tick and re-opened the very prompt it was
		// supposed to be answering: 109 cogwheel presses and nothing else, with the yes/no visibly
		// flickering. Answer what is in front before touching what is behind it.
		if (this.yesnoAnswered < MaxYesno && TryFire("SelectYesno", 0)) {
			this.yesnoAnswered++;
			DresserLog.Trace("  fired: SelectYesno [0] (yes)");
			this.settle = SettleTicks;
			return;
		}

		// ⚠ Once. See storePressed — the confirmation is the other half of the pair that made ghosts.
		if (!this.confirmPressed && TryFire("MiragePrismPrismSetConvertC", 0)) {
			this.confirmPressed = true;
			DresserLog.Trace("  fired: MiragePrismPrismSetConvertC [0] (confirm)");
			this.settle = SettleTicks;
			return;
		}

		// ⚠⚠ A slot is not a checkbox. [13, n] opens a PICKER — a ContextIconMenu of the items
		// eligible for that slot — and the choice is a second callback. deserok, watching it by hand:
		// *"left clicking then left clicking again to select the item fills it."* Firing [13, n]
		// eleven times and then pressing Store selected nothing at all, which presented as a hang.
		//
		// ⚠ WHAT IS NOT KNOWN: whether entry 0 is always our piece. It is assumed to be, because we
		// restore only the pieces this outfit wants — but nothing has ever READ the menu to check, and
		// a menu whose first entry is "remove this piece" would empty a slot instead of filling one.
		// DresserProbe writes the menu out under Verbose so this stops being an assumption.
		if (AddonVisible("ContextIconMenu")) {
			// ⚠ Values as well as text, and only the first time in a run. This is the one step
			// nothing has ever been able to see: every job so far has been a single piece, where the
			// cogged item fills its own slot and no picker ever opens. A multi-piece run is the first
			// time entry 0 gets chosen sight unseen, and if it turns out to be a "remove" option
			// rather than the piece, this is the line that will say so.
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

		if (!this.storePressed && dialogUp && this.tickSlot < this.queue[this.jobIndex].SlotCount) {
			TryFire2("MiragePrismPrismSetConvert", 13, this.tickSlot);
			DresserLog.Trace($"  fired: MiragePrismPrismSetConvert [13,{this.tickSlot}] (open slot picker)");
			this.tickSlot++;
			this.settle = TickSettle;
			return;
		}

		// ⚠⚠⚠ ONCE PER JOB, and this guard is the whole ghost-outfit fix. Without it the loop
		// pressed Store and confirmed it six times over for a single job, committing an EMPTY outfit
		// each time — an entry holding nothing, which cannot then be removed, because removing an
		// outfit means restoring an item out of it and there is no item in it. If one press did not
		// finish the job, the timeout reports it; pressing again only ever made more wreckage.
		if (!this.storePressed && dialogUp && TryFire("MiragePrismPrismSetConvert", 14)) {
			this.storePressed = true;
			DresserLog.Step($"  commit {this.queue[this.jobIndex].Name}: "
				+ $"{this.menusAnswered} slot(s) picked from row {this.cogTarget}");
			this.settle = SettleTicks;
			return;
		}

		// ⭐⭐⭐ ONE KNOWN ROW. Not row 0, and not a walk over every row until something opens —
		// both of those cog whatever item happens to be sitting there, and the dialog they open is
		// for that item's set. See DresserList for how the row is read, and the check above for what
		// catches it when the read is wrong anyway.
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

		// Nothing known is up and the pieces are still here. Say what IS open, once, so a stall
		// names its own cause rather than needing to be described.
		if (!this.loggedAddons) {
			this.loggedAddons = true;
			DresserLog.Trace("  stuck; no known dialog is open. Currently visible:");
			foreach (var name in VisibleAddonNames()) DresserLog.Trace($"        {name}");
		}
	}

	/// <summary>
	/// Which row of the glamour-ready list to cog for the current job. ⚠ -1 if none of its pieces
	/// are in that list.
	///
	/// ⚠ ANY piece of the set will do — the dialog that opens is for the SET, not for the piece —
	/// so this takes the first one the game is offering rather than insisting on a particular one.
	/// The cogged piece gets its own slot filled for free, which is why a single-piece outfit needs
	/// no slot picking at all.
	/// </summary>
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

	/// <summary>
	/// Why none of this job's pieces are in the glamour-ready list.
	///
	/// ⭐⭐ A restore does not always land in your bags. The game files equipment into the matching
	/// ARMOURY category when there is room there — and the glamour-ready list is built from your
	/// bags, so a piece that went to the armoury is present, findable, and not on offer. Measured on
	/// Boulevardier's Ruffled Shirt, 2026-09-03, which failed the same way three runs running.
	///
	/// ⚠ Worth naming rather than reporting as a generic refusal, because it is fixable in about
	/// two seconds by whoever is standing there and by nobody else.
	/// </summary>
	/// <summary>
	/// ⚠⚠⚠ DO NOT "FIX" THIS BY MOVING THE PIECE OUT OF THE ARMOURY. It was tried on
	/// 2026-09-05 and reverted within the minute.
	///
	/// The suggestion is obvious — the message tells the player to move an item by hand while
	/// MoveItemSlot sits right there — and it is wrong for two reasons that compound.
	///
	/// ⚠⚠ FindInBags searches the bags and THEN the armoury, so a piece found in the armoury is not
	/// necessarily the one we just restored. If the restore quietly failed and the player owns a
	/// second copy filed away, the "cleanup" moves THEIR copy.
	///
	/// ⭐⭐ And that copy is not spare. deserok: *"there are some pieces of gear that are in valid
	/// use, while the rest of the set is less ideal — you glam those, keep the useful ones in the
	/// chest."* The armoury is WORKING INVENTORY. The same item id means two different things
	/// depending on which container holds it: a dresser copy is an appearance, an armoury copy is
	/// equipment somebody is wearing or has bound to a gear set. A tool that tidies the second while
	/// thinking about the first is rearranging gear its owner is using.
	///
	/// ⭐ The Armoire transfer moves armoury pieces and that is not a contradiction: everything in
	/// its queue came out of the dresser seconds earlier, so anything it finds IS the piece it just
	/// restored. The difference is provenance, not permission.
	///
	/// ⚠ If this ever does need fixing, the safe shape is to record whether the item was in the
	/// armoury BEFORE issuing the restore. Absent then and present now is provably ours; present in
	/// both is ambiguous and must stay untouched.
	/// </summary>
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

	/// <summary>
	/// Which set the outfit dialog is currently about. ⚠ 0 when it cannot be read.
	///
	/// ⭐ Value 4, measured from a live dialog on 2026-09-03: a job for Whisperfine Wool Attire
	/// opened a dialog reporting 45325, which is that set's item id exactly.
	/// </summary>
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

	/// <summary>
	/// Close the outfit dialog without storing anything.
	///
	/// ⭐ [Int = -2] with close FALSE, copied from the game closing the dialog itself in a recording
	/// rather than invented. Every other value in this file was recorded the same way, and the one
	/// time a payload here was reasoned about instead of observed it pressed something else.
	/// </summary>
	private static void CancelDialog() {
		var addon = Plugin.GameGui.GetAddonByName("MiragePrismPrismSetConvert", 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return;

		var unit = (AtkUnitBase*)addon.Address;
		var values = stackalloc AtkValue[1];
		values[0].Type = AtkValueType.Int;
		values[0].Int = -2;
		unit->FireCallback(1, values, false);
	}

	/// <summary>How many entries the dresser holds right now. ⚠ -1 when it cannot be read.</summary>
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

	/// <summary>
	/// Did that job change the dresser by more than it was supposed to?
	///
	/// ⭐⭐⭐ THE BACKSTOP THAT WAS MISSING. Every other fix here stops one particular way of
	/// pressing the wrong button; this one does not care which button was wrong. A job knows exactly
	/// how the dresser's entry count should move — minus the pieces it takes out of it, plus one if
	/// it makes a new outfit — so anything above that is an entry nobody asked for. It ends the run
	/// on the first one instead of carrying on to make fourteen more, which is what happened on
	/// 2026-09-03.
	///
	/// ⚠ ABOVE, not merely different. Fewer entries than expected is somebody tidying up in another
	/// window, or a piece that had already gone; neither is this tool doing damage.
	/// </summary>
	private bool MadeCollateral(int expectedDelta) {
		var now = UsedEntries();
		if (this.usedAtJobStart < 0 || now < 0) return false;

		var delta = now - this.usedAtJobStart;
		if (delta <= expectedDelta) return false;

		var name = this.jobIndex < this.queue.Count ? this.queue[this.jobIndex].Name : "?";
		this.pass = 0;
		this.state = State.Failed;
		this.Status = $"Stopped after {this.OutfitsPacked} outfit(s): the dresser gained "
		            + $"{Plural(delta - expectedDelta, "entry")} nobody asked for while packing {name}. "
		            + "Nothing else was touched.";

		DresserLog.Step($"ABORTED: {name} moved the dresser by {delta}, expected {expectedDelta} "
		              + $"({this.usedAtJobStart} -> {now})");
		Plugin.Chat.Print($"Dresser: {this.Status}");
		return true;
	}

	/// <summary>
	/// Abandon the current outfit and move on.
	///
	/// ⚠ Its pieces are left loose in the bags. That is deliberately not tidied up automatically:
	/// putting them back is a restore-shaped operation of its own and could fail the same way, and
	/// the honest thing is to say what is where rather than to shuffle things further.
	/// </summary>
	private void SkipJob(string why) {
		// ⚠⚠ A guard, not a fix. Every "SKIPPED ?" in the log is this being called from a phase
		// that has no job — twice now, once for duplicates and once for loose pieces — and each time
		// it silently walked jobIndex past the end and looped. If it ever happens again, this says so
		// instead of spinning.
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

		this.NextJob();
	}

	/// <param name="expectedDelta">
	/// How the dresser's entry count should have moved. ⚠ Zero for an abandoned job: one that did
	/// nothing should have changed nothing, and a job that changed something anyway is exactly the
	/// case worth stopping on.
	/// </param>
	private void NextJob(int expectedDelta = 0) {
		if (this.MadeCollateral(expectedDelta)) return;

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
			// ⭐ Phase two before phase three: put things IN before taking duplicates OUT, so a piece
			// stored this run can be recognised as the copy that makes another one surplus.
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

		// ⚠ The same correction as the gate: wait only for the space a restore actually needs.
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
