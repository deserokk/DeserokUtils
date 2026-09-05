using System;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
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
/// ⭐⭐ PROVEN 2026-09-05, and better than the bet: it stored successfully with the GLAMOUR
/// DRESSER window open and the Armoire shut. So the cabinet does not need to be on screen at all —
/// only to have been opened once, so the server has sent its contents. No window driving, no
/// interacting with furniture, no walking.
///
/// ⭐ Which means the Interact fallback stays unbuilt. It was ready — both furnishings are in
/// range from the ordinary standing spot, and interacting raises a SelectString we already know how
/// to answer — and none of it was needed.
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
	internal enum State { Idle, Opening, Unpacking, Restoring, Storing, Done, Failed }

	/// <summary>
	/// ⚠ Kept clear of full: gather (free slots − 5) at a time. The margin is not superstition — a
	/// restore that arrives with nowhere to go is refused, and a run that fills somebody's bags to
	/// the brim leaves them worse off than it found them.
	///
	/// ⭐ Five rather than three, on deserok's instruction: *"should work off of n − like 5 slots,
	/// so it uses what is available to make transfer smoother."* The point of the margin is safety;
	/// the point of using everything ABOVE it is throughput.
	/// </summary>
	private const int SlotsToLeaveFree = 5;

	/// <summary>
	/// ⚠⚠ A SANITY BOUND, NOT A POLICY, and it used to be eight — which throttled a run with
	/// eighty-three free slots down to eight pieces a trip. deserok: *"homie if it doesn't work with
	/// 83 free slots it's kind of broken anyways."*
	///
	/// ⭐ Eight was insurance from when a batch appeared to ignore its own limit and pulled
	/// forty-seven pieces inside twenty milliseconds. That was a BURST problem, and the burst is gone:
	/// every restore is now issued one at a time, half a second apart. A large batch no longer means
	/// a flood, only fewer switches between gathering and storing — which is smoother, not riskier.
	///
	/// ⚠ So this stays only to stop an arithmetic slip emptying a whole dresser into somebody's
	/// bags. It should never be the binding constraint; free slots should.
	/// </summary>
	private const int MaxBatch = 40;

	/// <summary>⚠ Ticks between one item and the next — roughly half a second at 60fps.</summary>
	private const int PaceTicks = 30;

	/// <summary>⚠ Up to this much more, at random. An even beat is a signature of its own.</summary>
	private const int PaceJitter = 25;

	private readonly Random jitter = new();

	/// <summary>The one piece currently on its way out of the dresser, if any.</summary>
	private (uint ItemId, uint CabinetRow, string Name)? inFlight;

	/// <summary>Ticks between actions. ⚠ Every one of these is a request to the server.</summary>
	private const int Settle = 20;

	/// <summary>
	/// How long to wait for one step before giving up on it.
	///
	/// ⚠ Generous, because it now also covers opening a piece of furniture and waiting for a server
	/// round trip — roughly seven seconds at 60fps.
	/// </summary>
	private const int StepTimeoutTicks = 400;

	/// <summary>
	/// ⚠⚠ PER ACTION, NOT PER RUN, and getting that wrong cost four outfits. A restore raises
	/// "this is used by a glamour plate, take it anyway?" — the same prompt the packer met on
	/// Skyworker's Boots — so a run of twenty outfits can legitimately need twenty answers. Capped at
	/// three for the whole run, it went silent after six and every unpack after that hung waiting for
	/// a box nobody was answering. deserok saw it from the other side: *"it did a lot of 'a yes no
	/// opened but not from this key, leaving it alone'"* — another plugin narrating our own stall.
	///
	/// ⚠ Still bounded, because SelectYesno is generic and answering one in a loop is how you say
	/// yes to something nobody asked about. Three per action is a prompt and a retry, not a habit.
	/// </summary>
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
	public bool Running
		=> this.state is State.Opening or State.Unpacking or State.Restoring or State.Storing;

	/// <summary>Outfits dissolved this run, for the report.</summary>
	public int Unpacked { get; private set; }

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
		this.dissolving = null;
		this.Stored = 0;
		this.yesno = 0;

		var mirage = MirageManager.Instance();
		if (mirage is null || !mirage->PrismBoxLoaded) {
			this.Fail("Open your glamour dresser once first, then try again.");
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
		// ⭐⭐⭐ OPEN IT OURSELVES RATHER THAN ASKING. This used to refuse with "open your Armoire
		// once first" — and deserok cut that down for the right reason: *"we design for the idiot, and
		// 'open armoire first' is a missable step that will read as 'this is broken' when it doesn't."*
		//
		// He is right, and it is worse than an inconvenience. A prerequisite the player cannot see,
		// whose failure looks identical to a bug, is a bug. The plugin knows the cabinet is not loaded;
		// it can go and load it.
		// ⭐⭐⭐ OUTFITS FIRST, and they are why this exists at all. deserok, seeing the count:
		// *"being able to unpack is kind of needed.. badly, because hell the scan told me there's 20
		// packed outfits that can be stored."* Twenty entries whose every piece the Armoire takes for
		// free is twenty dresser slots paying rent for nothing.
		//
		// ⚠ Before the loose pieces, because dissolving an outfit PRODUCES loose pieces — doing it
		// the other way round would gather a batch, store it, and only then discover more work.
		this.dissolve.Clear();
		this.dissolve.AddRange(r.Dissolvable);
		this.Unpacked = 0;

		this.state = UIState.Instance()->Cabinet.IsCabinetLoaded()
			? (this.dissolve.Count > 0 ? State.Unpacking : State.Restoring)
			: State.Opening;
		this.settle = 0;
		this.waited = 0;
		this.opens = 0;
		this.menuAnswers = 0;

		this.Status = this.state == State.Opening
			? "Opening your Armoire..."
			: $"Moving {this.queue.Count} piece(s) to your Armoire...";

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

		switch (this.state) {
			case State.Opening: this.TickOpening(); break;
			case State.Unpacking: this.TickUnpack(); break;
			case State.Restoring: this.TickRestore(); break;
			default: this.TickStore(); break;
		}
	}

	/// <summary>
	/// Pull a batch out of the dresser, up to what the bags can hold.
	///
	/// ⭐ A batch rather than one at a time because each round trip is a restore AND a store, and
	/// deserok's rule sizes it by what is actually free rather than by a constant somebody guessed.
	/// </summary>
	/// <summary>
	/// Touch the Armoire so the server sends its contents.
	///
	/// ⭐⭐ Only ever needed once per visit, and only when the player has not already opened it. The
	/// contents outlive the window — proven 2026-09-05, when a whole transfer ran with the Armoire
	/// shut and the glamour dresser open — so this is a doorbell, not a door held open.
	///
	/// ⭐⭐ FOUND BY DATA ID, 2005630, read off the real furnishing rather than guessed. This matched
	/// on the English NAME for about ten minutes, which would have failed silently on any other
	/// client — deserok's answer to that was not to accept it: *"we don't have to make guesses, we
	/// know what we need right? lets take our time, build sniffers and see if we can sniff it."*
	/// One interact with the sniffer armed produced it. A data id is the same in every language.
	///
	/// ⚠ Interaction only. It does NOT drive the "Store an item" menu that follows, because the
	/// contents load without it — and a menu we opened and left is rude, so anything still on screen
	/// gets dismissed once the cabinet is loaded.
	/// </summary>
	private void TickOpening() {
		if (UIState.Instance()->Cabinet.IsCabinetLoaded()) {
			CloseCabinetWindow();
			DresserLog.Step("  armoire opened");
			this.state = this.dissolve.Count > 0 ? State.Unpacking : State.Restoring;
			this.waited = 0;
			this.Status = $"Moving {this.queue.Count} piece(s) to your Armoire...";
			return;
		}

		// ⚠⚠⚠ THE MENU FIRST, ALWAYS. The previous version checked "have I interacted enough times"
		// before checking "is the menu already up", and fired three interacts inside 370 milliseconds —
		// before the game had drawn anything at all. The whole budget was gone before the first
		// response, and then it gave up one tick after finally answering the menu.
		//
		// ⭐ Same shape as the packer's confirming loop, and for the same reason: answer what is in
		// front of you before deciding to knock again.
		if (this.menuAnswers < MaxOpenAttempts && FireMenuEntry(0)) {
			this.menuAnswers++;
			DresserLog.Step($"  chose 'Store an item' (attempt {this.menuAnswers})");
			this.settle = OpenSettle;
			return;
		}

		// ⚠ Already asked and nothing on screen to answer — the server is still thinking. The step
		// timeout is what decides this has failed, not an attempt counter that cannot see the game.
		if (this.menuAnswers > 0) return;

		if (this.opens >= MaxOpenAttempts) {
			DresserProbe.Visible("after interacting with the Armoire");
			this.Fail("Could not open your Armoire — open it once yourself and try again.");
			return;
		}

		if (FindArmoire() is not { } armoire) {
			this.Fail("No Armoire nearby — stand next to one and try again.");
			return;
		}

		this.opens++;
		DresserLog.Step($"  interacting with the Armoire (attempt {this.opens})");

		TargetSystem.Instance()->InteractWithObject(
			(FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)armoire.Address, true);

		// ⚠⚠ A whole second, not a fifth of one. Opening a piece of furniture is a round trip to the
		// server and then a window animation; the old Settle was tuned for firing a button on a window
		// that is already there.
		this.settle = OpenSettle;
	}

	/// <summary>
	/// The nearest Armoire we could operate. ⚠ Null when there is none in reach.
	///
	/// ⭐ The same three gates the Interact key uses, and for the same reasons: the kind, the game's
	/// own targetable verdict, and a name. Copied in shape rather than called into, because that
	/// feature is about the key somebody pressed and this is not.
	/// </summary>
	private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindArmoire() {
		var player = Plugin.Objects.LocalPlayer;
		if (player is null) return null;

		foreach (var obj in Plugin.Objects) {
			if (obj is null) continue;
			if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj) continue;
			if (!obj.IsTargetable) continue;
			if (obj.BaseId != ArmoireDataId) continue;

			return obj;
		}

		return null;
	}

	/// <summary>
	/// The Armoire furnishing.
	///
	/// ⭐ Measured 2026-09-05 by interacting with one while the sniffer was armed:
	/// <c>InteractWithObject("Armoire" kind=EventObj dataId=2005630)</c>. The Glamour Dresser, from
	/// the same recording, is 2009439 — noted because the next thing that wants a furnishing will
	/// want that one.
	/// </summary>
	private const uint ArmoireDataId = 2005630;

	/// <summary>⚠ A few tries, then say so. Interacting forever at furniture is not a plan.</summary>
	private const int MaxOpenAttempts = 3;

	/// <summary>⚠ About a second. Furniture opening is a server round trip plus an animation.</summary>
	private const int OpenSettle = 60;

	private int opens;

	/// <summary>How many times the Armoire's own menu has been answered this run.</summary>
	private int menuAnswers;

	/// <summary>Choose an entry of the Armoire's menu. ⚠ Only ever called from Opening.</summary>
	private static bool FireMenuEntry(int index) {
		var addon = Plugin.GameGui.GetAddonByName("SelectString", 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return false;

		var unit = (AtkUnitBase*)addon.Address;
		var values = stackalloc AtkValue[1];
		values[0].Type = AtkValueType.Int;
		values[0].Int = index;

		unit->FireCallback(1, values, true);
		return true;
	}

	/// <summary>
	/// Put back what our own interaction opened.
	///
	/// ⭐⭐⭐ THE WINDOW IS CALLED "Cabinet", AND IT CLOSES ON [-1]. Both facts were unknowns I had
	/// written into a comment as unavoidable — deserok's answer: *"we shouldn't have unknowns, we
	/// have sniffers and probes, you shouldn't have a situation where you can't identify a window.
	/// Tell me what to do to close all unknowns for you."* One scripted recording, every answer:
	/// <code>
	///   OPEN  SelectString                        the interact raises the menu
	///   CALLBACK SelectString [Int 0]             "Store an item"
	///   OPEN  Cabinet                             the store window, and what loads the contents
	///   CALLBACK Cabinet [Int -1]                 the X button
	///   CLOSE Cabinet
	/// </code>
	///
	/// ⚠ We only ever wanted the CONTENTS loaded, not the window — deserok again: *"we don't need
	/// to replicate 'put things in it with the window', what we're doing is ensuring the armoire is
	/// always loaded before attempting."* So this opens it, takes the loading as the prize, and shuts
	/// it again.
	/// </summary>
	private static void CloseCabinetWindow() {
		foreach (var name in new[] { "Cabinet", "SelectString" }) {
			var addon = Plugin.GameGui.GetAddonByName(name, 1);
			if (addon.Address == nint.Zero || !addon.IsVisible) continue;

			var unit = (AtkUnitBase*)addon.Address;
			var values = stackalloc AtkValue[1];
			values[0].Type = AtkValueType.Int;
			values[0].Int = -1;

			unit->FireCallback(1, values, true);
		}
	}

	/// <summary>
	/// Take a whole outfit apart, so its pieces can go to the Armoire.
	///
	/// ⭐⭐⭐ THE DIRECT API, NOT THE MENU. <c>RestorePrismBoxSetItem(index, restoreBits)</c> does
	/// what the right-click "Restore Item" flow does, without the context menu — which matters
	/// because that menu's Restore entry was index 2 on one outfit and index 3 on another, and
	/// hard-coding a moving index is precisely the mistake that made ghost outfits in the packer.
	///
	/// ⭐⭐ The bits are the FILLED slots and nothing more. Recorded from deserok unpacking two by
	/// hand: 124 for a five-piece, 56 for a three-piece, each exactly that outfit's slots. An earlier
	/// attempt at this API passed all eleven bits and was refused, which now reads as the game
	/// declining to hand back pieces the outfit never had.
	///
	/// ⚠ Two bytes, little-endian, because eleven slots do not fit in one. ⚠ UNPROVEN in that
	/// order — if it refuses, the byte layout is the first thing to doubt, and the log says which
	/// outfit and which mask so a second guess is cheap.
	///
	/// ⭐ Verified by counting, not by the return value: an outfit that dissolves takes its dresser
	/// entry with it, so the used count must fall by exactly one. Nothing here trusts a bool.
	/// </summary>
	private void TickUnpack() {
		var mirage = MirageManager.Instance();
		if (mirage is null || !mirage->PrismBoxLoaded) {
			this.Stop("lost sight of the dresser contents");
			return;
		}

		// Still waiting for the last one to actually leave.
		if (this.dissolving is { } pending) {
			if (Used(mirage) < this.usedBeforeDissolve) {
				DresserLog.Step($"  unpacked {pending.Name}");
				this.Unpacked++;
				this.dissolving = null;
				this.waited = 0;
				this.settle = this.Pace();
				return;
			}

			// ⚠⚠ The bool is not the answer. RestorePrismBoxSetItem returns true whether or not
			// anything moves — the third time this codebase has met that exact lie, after StoreNewOutfit
			// and StoreCabinetItem. The dresser's entry count is the truth, so that is what is watched.
			if (++this.layoutWait < DissolveTimeout) return;

			this.layoutWait = 0;
			DresserLog.Step($"  {pending.Name}: nothing came back");
			this.failed.Add($"{pending.Name} (could not be unpacked)");
			this.dissolving = null;
			this.waited = 0;
			return;
		}

		if (this.dissolve.Count == 0) {
			this.state = State.Restoring;
			this.waited = 0;
			return;
		}

		// ⚠⚠ An outfit comes apart all at once, so the bags need room for ALL of it. Half an
		// outfit in the bags and half refused would be a mess with no obvious owner.
		var next = this.dissolve[0];
		if (DresserPacker.FreeBagSlots() < next.Pieces + SlotsToLeaveFree) {
			DresserLog.Step($"  {next.Name} needs {next.Pieces} slots; storing what we have first");
			this.state = State.Restoring;
			this.waited = 0;
			return;
		}

		this.dissolve.RemoveAt(0);

		// ⚠ The index is re-read here. Dissolving an outfit removes an entry, which moves every
		// entry after it — the scan's index is a name, not an address.
		var ids = mirage->PrismBoxItemIds;
		var index = -1;
		for (var i = 0; i < ids.Length; i++) {
			if (ids[i] != next.SetItemId) continue;
			index = i;
			break;
		}

		if (index < 0) {
			DresserLog.Step($"  {next.Name}: no longer in the dresser, skipping");
			return;
		}

		this.yesno = 0;
		this.dissolving = next;
		this.Dissolve(mirage, next);
	}

	/// <summary>
	/// Fire the unpack with whichever byte layout we are currently trying.
	///
	/// ⭐⭐ THE MASK, PACKED LITTLE-ENDIAN, and it is measured rather than argued. Two readings were
	/// tried against a live dresser — this one, and eleven bytes with a flag each — and this is the
	/// one that works: six outfits came apart on it in a single run, including a nine-piece Vanguard
	/// set with mask 2044. The loser is deleted rather than kept as a fallback, because a fallback
	/// nobody can trigger is only a second thing to maintain.
	///
	/// ⭐ It is the same number the UI sends: the recorded confirm callback carries 124 for a
	/// five-piece and 56 for a three-piece, exactly what the scan computes from the filled slots. This
	/// API and that button are two doors onto one function.
	/// </summary>
	private void Dissolve(
		MirageManager* mirage, (uint Index, uint SetItemId, string Name, ushort Mask, int Pieces) outfit) {
		var ids = mirage->PrismBoxItemIds;
		var index = -1;
		for (var i = 0; i < ids.Length; i++) {
			if (ids[i] != outfit.SetItemId) continue;
			index = i;
			break;
		}

		if (index < 0) {
			DresserLog.Step($"  {outfit.Name}: no longer in the dresser");
			this.dissolving = null;
			return;
		}

		var bits = stackalloc byte[SetSlots];
		for (var i = 0; i < SetSlots; i++) bits[i] = 0;

		bits[0] = (byte)(outfit.Mask & 0xFF);
		bits[1] = (byte)((outfit.Mask >> 8) & 0xFF);

		this.usedBeforeDissolve = Used(mirage);
		DresserLog.Step($"  unpacking {outfit.Name} at index {index}, mask {outfit.Mask}, "
			+ $"({outfit.Pieces} piece(s))");

		var ok = MirageManager.MemberFunctionPointers.RestorePrismBoxSetItem(
			mirage, (uint)index, bits);

		DresserLog.Trace($"  RestorePrismBoxSetItem -> {ok}");

		this.waited = 0;
		this.settle = this.Pace();
	}

	/// <summary>Eleven, matching MirageStoreSetItem's columns.</summary>
	private const int SetSlots = 11;

	private int layoutWait;

	/// <summary>
	/// ⚠ Five seconds. Two was not enough: a nine-piece outfit comes apart into nine separate
	/// arrivals, and calling that a failure while the game is still working produced a "could not be
	/// unpacked" for a set that had in fact come apart perfectly well.
	/// </summary>
	private const int DissolveTimeout = 300;

	private static int Used(MirageManager* mirage) {
		var ids = mirage->PrismBoxItemIds;
		var used = 0;
		for (var i = 0; i < ids.Length; i++) {
			if (ids[i] != 0) used++;
		}

		return used;
	}

	private readonly List<(uint Index, uint SetItemId, string Name, ushort Mask, int Pieces)> dissolve
		= new();

	private (uint Index, uint SetItemId, string Name, ushort Mask, int Pieces)? dissolving;
	private int usedBeforeDissolve;

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

		// ⚠⚠⚠ THREE DIFFERENT ENDINGS, AND THEY USED TO BE ONE. "batch.Count >= room" is true
		// when the batch is full AND when room is zero with an empty batch — so a run with full bags
		// finished instantly and announced "Nothing was moved to your Armoire" while thirty-five
		// pieces sat in the queue. deserok saw it stop on an outfit and reasonably concluded the
		// outfit was the problem; the outfit was a coincidence.
		if (this.batch.Count == 0 && this.queue.Count == 0) {
			this.Finish();
			return;
		}

		// Carrying as much as this trip can hold, or nothing more to gather — go and store it.
		if (this.batch.Count > 0 && (this.batch.Count >= room || this.queue.Count == 0)) {
			DresserLog.Step($"  storing {this.batch.Count} piece(s)");
			this.state = State.Storing;
			this.waited = 0;
			return;
		}

		// ⚠ Nothing gathered and nowhere to put anything. Storing frees bag slots, so an empty batch
		// means nothing is coming to rescue this — it is a stop, and it says so rather than claiming
		// there was nothing to do.
		if (room < 1) {
			this.Stop($"your bags are too full to carry any more; {this.Stored} moved so far");
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
		this.yesno = 0;
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
			// ⚠ Back to the outfits if any are left — one may have been deferred for bag space that
			// this batch has just freed.
			this.state = this.dissolve.Count > 0 ? State.Unpacking : State.Restoring;
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

		// ⭐⭐⭐ OUT OF THE ARMOURY FIRST. Five pieces failed a whole run with "the game did not
		// respond in time", and they were a coat and three bracelets — which is to say ArmoryBody and
		// ArmoryWrist. The Armoire's own window names its source as "Inventory", and a restore lands
		// wherever the game likes: equipment goes to the matching armoury category when there is room
		// there. So the piece arrived perfectly well, in a place the Armoire will not reach into.
		//
		// ⚠ The move is harmless whether or not that reading is right, which is why it is worth
		// doing rather than proving first: a piece in your bags can be stored either way, and if the
		// failures vanish the theory was correct.
		if (!Locate(piece.ItemId, out var where, out var slot)) {
			DresserLog.Step($"  {piece.Name}: not in your bags or armoury any more");
			this.batch.RemoveAt(0);
			this.waited = 0;
			return;
		}

		if (IsArmoury(where)) {
			if (!FreeBagSlot(out var destination, out var destinationSlot)) {
				DresserLog.Step($"  {piece.Name}: in your {where} and no bag slot to move it to");
				this.failed.Add($"{piece.Name} (no room to move it out of your armoury chest)");
				this.batch.RemoveAt(0);
				this.waited = 0;
				return;
			}

			DresserLog.Step($"  moving {piece.Name} from {where} to your bags");
			InventoryManager.Instance()->MoveItemSlot(
				where, slot, destination, destinationSlot, true);

			this.waited = 0;
			this.settle = this.Pace();
			return;
		}

		// ⚠ Only ask once per settle window; the confirmation above is what decides it worked.
		this.yesno = 0;
		UIState.Instance()->Cabinet.StoreCabinetItem(piece.CabinetRow);
		DresserLog.Trace($"  store: {piece.Name} (cabinet {piece.CabinetRow})");

		// ⚠ Paced like the restores, and for the same reasons. A store is a request to the server
		// too, and forty-seven of them in a burst is the same signature the restores were.
		this.settle = this.Pace();
	}

	/// <summary>Abandon whatever step stalled, and keep going with the rest.</summary>
	private void GiveUpOnCurrent(string why) {
		// ⚠⚠ The outfit being dissolved counts too, and leaving it out is why the first run hung
		// silently forever instead of reporting anything: nothing cleared it, so the wait never ended
		// and no timeout could reach past it.
		if (this.dissolving is { } outfit) {
			this.failed.Add($"{outfit.Name} ({why})");
			DresserLog.Step($"  SKIPPED {outfit.Name}: {why}");
			this.dissolving = null;
			this.layoutWait = 0;
			this.waited = 0;
			return;
		}

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

		// ⚠⚠ LEAD WITH WHAT HAPPENED. This used to open "Nothing was moved to your Armoire" and
		// then add "11 outfit(s) taken apart", which contradicts itself in the space of one sentence
		// — eleven outfits came apart, which is real work, and the run reported it as nothing.
		//
		// ⭐ Unpacking with nothing stored is a specific and explainable state, not a failure: the
		// pieces are in your bags. Saying so is the difference between "it broke" and "it got as far
		// as it could".
		if (this.Stored > 0 && this.Unpacked > 0) {
			this.Status = $"Took apart {Plural(this.Unpacked, "outfit")} and moved "
			            + $"{Plural(this.Stored, "piece")} to your Armoire — "
			            + $"{Plural(this.Stored + this.Unpacked, "dresser slot")} freed.";
		}
		else if (this.Stored > 0) {
			this.Status = $"Moved {Plural(this.Stored, "piece")} to your Armoire — "
			            + $"{Plural(this.Stored, "dresser slot")} freed.";
		}
		else if (this.Unpacked > 0) {
			this.Status = $"Took apart {Plural(this.Unpacked, "outfit")}; "
			            + "their pieces are in your bags.";
		}
		else {
			this.Status = "Nothing was moved to your Armoire.";
		}

		if (this.failed.Count > 0)
			this.Status += $" {this.failed.Count} could not be moved — see the list.";

		Plugin.Chat.Print($"Dresser: {this.Status}");
		DresserLog.Step($"=== ARMOIRE DONE: {this.Status} ===");

		foreach (var entry in this.failed) DresserLog.Step($"  failed: {entry}");
	}

	/// <summary>"1 outfit", "3 outfits". ⚠ Never "outfit(s)" — that reads as unfinished software.</summary>
	private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

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
	/// <summary>Where a piece is right now, bags or armoury. ⚠ False when it is neither.</summary>
	private static bool Locate(uint itemId, out InventoryType where, out ushort slot) {
		where = default;
		slot = 0;

		var manager = InventoryManager.Instance();
		if (manager is null) return false;

		foreach (var bag in Anywhere) {
			var page = manager->GetInventoryContainer(bag);
			if (page is null || !page->IsLoaded) continue;

			for (var i = 0; i < page->Size; i++) {
				var item = page->GetInventorySlot(i);
				if (item is null || DresserCache.PureItemId(item->ItemId) != itemId) continue;

				where = bag;
				slot = (ushort)i;
				return true;
			}
		}

		return false;
	}

	/// <summary>⚠ The first empty ordinary bag slot. Never the armoury — that is what we are leaving.</summary>
	private static bool FreeBagSlot(out InventoryType where, out ushort slot) {
		where = default;
		slot = 0;

		var manager = InventoryManager.Instance();
		if (manager is null) return false;

		foreach (var bag in Bags) {
			var page = manager->GetInventoryContainer(bag);
			if (page is null || !page->IsLoaded) continue;

			for (var i = 0; i < page->Size; i++) {
				var item = page->GetInventorySlot(i);
				if (item is not null && item->ItemId != 0) continue;

				where = bag;
				slot = (ushort)i;
				return true;
			}
		}

		return false;
	}

	private static bool IsArmoury(InventoryType type) {
		foreach (var bag in Bags) {
			if (bag == type) return false;
		}

		return true;
	}

	/// <summary>⚠ The four ordinary bags, which is the only place the Armoire will look.</summary>
	private static readonly InventoryType[] Bags = {
		InventoryType.Inventory1, InventoryType.Inventory2,
		InventoryType.Inventory3, InventoryType.Inventory4,
	};

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
