using System;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.FcBuffs;

/// <summary>Where an activation attempt currently is. One attempt at a time, ever.</summary>
internal enum ActivationStep {
	Idle,
	Opening,        // asked the agent to show the FC window
	SelectingTab,   // window is up, but on whatever tab was last used
	Reading,        // FreeCompanyAction is up; find and verify the row
	Executing,      // the row opened a context menu; pick "Execute Action"
	Confirming,     // picked; waiting for SelectYesno
	Settling,       // confirmed; waiting to see the status appear
	Closing,        // put the FC window back if we were the one who opened it
	Done,
	Failed,
}

/// <summary>
/// Performs ONE activation, one step per tick.
///
/// ⚠⚠ A step machine rather than a function, because every step waits on the game: an addon does
/// not exist the frame after it is asked for, and the confirmation dialog does not exist the frame
/// after the row is fired. Written as straight-line code with sleeps it would either race the UI or
/// block the render thread, and the first is the one that fails intermittently and unreproducibly.
///
/// ⚠⚠ Replays a RECORDED payload. The two callbacks below were captured from deserok clicking the
/// real buttons on 2026-08-07, not derived:
///     FreeCompany        FireCallback [Int=0, UInt=4]              (select the Actions tab)
///     FreeCompanyAction  FireCallback [Int=1, UInt=row]            (pick the row -> opens a menu)
///     ContextMenu        FireCallback [Int=0, Int=0, UInt=0, _, _] (item 0: Execute Action)
///     SelectYesno        FireCallback [Int=0]                      (yes)
///
/// ⚠ The game ALSO fires a ContextMenu callback carrying cursor coordinates when the menu opens.
/// That one is the game's, not ours -- only the selection above gets replayed.
/// Nothing here was guessed, and if any of it stops working the recorder is how it gets re-derived.
///
/// ⭐ The tab value is 4, and the tabs on screen read Topics, Members, Rank, Actions, Activity,
/// Info -- which would make Actions 3. The obvious guess was wrong, and it was wrong in the
/// direction that silently opens a different panel rather than failing.
/// </summary>
internal sealed unsafe class FcActionActivator {
	/// <summary>⚠ Every step has one. A machine that can wait forever is a machine that will.</summary>
	private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(8);

	/// <summary>
	/// Ticks to wait between steps.
	///
	/// ⚠ Not for stealth -- the UI genuinely needs frames to build an addon, and firing at one that
	/// is present but half-constructed is how a callback lands somewhere unintended.
	/// </summary>
	private const int SettleTicks = 30;

	public ActivationStep Step { get; private set; } = ActivationStep.Idle;
	public string WantedAction { get; private set; } = string.Empty;
	public string FailureReason { get; private set; } = string.Empty;

	/// <summary>Buffs still to do after the current one. Worked through without reopening anything.</summary>
	private readonly Queue<string> queue = new();

	/// <summary>What actually went up this run, for the caller to report on.</summary>
	public List<string> Completed { get; } = new();

	private DateTime stepStarted;
	private int waited;
	private int firedRow = -1;

	/// <summary>
	/// Whether WE opened the FC window, and therefore owe it a close.
	///
	/// ⚠ Not unconditional. /fcbuffs now is often run with the window already open by hand, and
	/// closing something the user opened is the plugin reaching past what it was asked to do.
	/// </summary>
	private bool weOpenedIt;

	public bool Busy => this.Step is not (ActivationStep.Idle or ActivationStep.Done or ActivationStep.Failed);

	/// <summary>
	/// Starts one window session covering every buff in <paramref name="wanted"/>.
	///
	/// ⭐ deserok's shape, and it is better than one-window-per-buff for more than tidiness: closing
	/// and reopening between buffs meant the next Show() raced the previous window's teardown, and
	/// the addon pointer went null between being checked and being used. One session cannot race
	/// itself.
	/// </summary>
	public void Begin(IEnumerable<string> wanted) {
		this.queue.Clear();
		this.Completed.Clear();
		foreach (string w in wanted)
			this.queue.Enqueue(w);

		if (this.queue.Count == 0)
			return;

		this.WantedAction = this.queue.Dequeue();
		this.FailureReason = string.Empty;
		this.firedRow = -1;
		this.Enter(ActivationStep.Opening);
		Plugin.Log.Information(
			$"FcBuffs: activation BEGIN for \"{this.WantedAction}\""
			+ (this.queue.Count > 0 ? $" (+{this.queue.Count} more this session)" : ""));
	}

	public void Reset() {
		this.Step = ActivationStep.Idle;
		this.waited = 0;
	}

	private void Enter(ActivationStep step) {
		this.Step = step;
		this.stepStarted = DateTime.UtcNow;
		this.waited = 0;
	}

	private void Fail(string reason) {
		this.FailureReason = reason;
		this.Step = ActivationStep.Failed;
		Plugin.Log.Warning($"FcBuffs: activation FAILED -- {reason}");

		// ⚠ Tidy up after ourselves. A half-finished sequence leaves a context menu sitting open at
		// the cursor -- which is not dangerous, but it is the plugin leaving its mess on screen and
		// the next attempt would be starting from a state nobody designed for.
		foreach (string name in new[] { "ContextMenu", "SelectYesno" }) {
			var addon = Addon(name);
			if (addon is not null)
				addon->FireCloseCallback();
		}

		// ⚠ And the window itself, for the same reason the success path closes it: left open it
		// keeps a condition set that stops every later attempt, so a single failure would otherwise
		// wedge the feature until deserok closed it by hand.
		if (this.weOpenedIt)
			CloseWindow();
	}

	private static string DescribeMenu(AtkUnitBase* menu) =>
		menu is null ? "(null)" : FcActionRecorder.DescribeValues(menu->AtkValuesCount, menu->AtkValues);

	private static AtkUnitBase* Addon(string name) =>
		(AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;

	public void Tick() {
		if (!this.Busy)
			return;

		if (DateTime.UtcNow - this.stepStarted > StepTimeout) {
			this.Fail($"step {this.Step} timed out");
			return;
		}

		switch (this.Step) {
			case ActivationStep.Opening: this.TickOpening(); break;
			case ActivationStep.SelectingTab: this.TickSelectingTab(); break;
			case ActivationStep.Reading: this.TickReading(); break;
			case ActivationStep.Executing: this.TickExecuting(); break;
			case ActivationStep.Confirming: this.TickConfirming(); break;
			case ActivationStep.Settling: this.TickSettling(); break;
			case ActivationStep.Closing: this.TickClosing(); break;
		}
	}

	private void TickOpening() {
		// The action list is a child panel of the FC window, so the window comes first.
		if (Addon("FreeCompanyAction") is not null) {
			this.Enter(ActivationStep.Reading);
			return;
		}

		if (this.waited++ == 0) {
			var agent = AgentFreeCompany.Instance();
			if (agent is null) {
				this.Fail("the FreeCompany agent is unavailable");
				return;
			}

			this.weOpenedIt = Addon("FreeCompany") is null;
			agent->Show();
			Plugin.Diag("FcBuffs: asked AgentFreeCompany to show");
		}

		// ⚠⚠ The FC window reopens on WHATEVER TAB WAS LAST USED, so arriving on Actions is a
		// property of what deserok happened to do last rather than anything the plugin controls.
		// Open it once for the chest and every later refresh would land on the wrong panel -- so the
		// tab is always selected explicitly, never assumed.
		if (this.waited > SettleTicks && Addon("FreeCompany") is not null)
			this.Enter(ActivationStep.SelectingTab);
	}

	private void TickSelectingTab() {
		// It may already be on Actions, in which case there is nothing to press.
		if (Addon("FreeCompanyAction") is not null) {
			this.Enter(ActivationStep.Reading);
			return;
		}

		var fc = Addon("FreeCompany");
		if (fc is null) {
			this.Fail("the FC window closed while selecting the Actions tab");
			return;
		}

		if (this.waited++ != SettleTicks)
			return;

		// ⚠⚠ Dismiss the panel that is currently showing FIRST. A real tab click fires
		// FreeCompanyTopics [Int=-2] and only then the tab switch -- the panel tears ITSELF down.
		// Replaying just the tab switch leaves the old panel drawn underneath the new one, which is
		// how the FC window ended up showing Topics and Actions at the same time.
		//
		// ⭐ It was in the recording all along; I replayed the callback I recognised and skipped the
		// one that looked like noise.
		foreach (string name in FcBuffReader.LoadedAddons()) {
			if (!name.StartsWith("FreeCompany", StringComparison.Ordinal)
				|| name is "FreeCompany" or "FreeCompanyAction")
				continue;

			var panel = Addon(name);
			if (panel is null)
				continue;

			var dismiss = stackalloc AtkValue[1];
			dismiss[0].Type = AtkValueType.Int;
			dismiss[0].Int = -2;
			panel->FireCallback(1, dismiss, false);
			Plugin.Diag($"FcBuffs: dismissed panel {name}");
		}

		if (Plugin.Config.FcBuffsDryRun) {
			Plugin.Log.Information("FcBuffs DRY RUN: would fire FreeCompany [Int=0, UInt=4] to select Actions");
			// ⚠ Still pressed even in a dry run. Selecting a tab spends nothing and consumes
			// nothing, and without it the dry run cannot reach the row it exists to check -- a
			// rehearsal that stops before the interesting part rehearses nothing.
		}

		var values = stackalloc AtkValue[2];
		values[0].Type = AtkValueType.Int;
		values[0].Int = 0;
		values[1].Type = AtkValueType.UInt;
		values[1].UInt = 4;
		fc->FireCallback(2, values, true);

		Plugin.Log.Information("FcBuffs: selected the Actions tab");
	}

	private void TickReading() {
		if (this.waited++ < SettleTicks)
			return;

		// Highest tier in stock -- see FcBuffReader.BestRowFor.
		var best = FcBuffReader.BestRowFor(this.WantedAction);
		if (best is null) {
			this.Fail($"no \"{this.WantedAction}\" in the inactive list");
			return;
		}

		var (row, tier, chosen) = best.Value;

		// ⭐ THE CHECK THAT MAKES THE DERIVED INDEXING SAFE. Base and stride were measured, not
		// documented; re-reading the row and confirming it still names the wanted family turns a
		// patch that moves them into a refusal instead of a wrongly consumed action.
		string? text = FcBuffReader.ReadListEntry(row);
		if (text is null || FcBuffReader.NormaliseName(text) != FcBuffReader.NormaliseName(this.WantedAction)) {
			this.Fail($"row {row} reads \"{text ?? "null"}\", expected \"{this.WantedAction}\"");
			return;
		}

		Plugin.Diag($"FcBuffs: picked tier {tier} \"{chosen}\" at row {row}");

		var addon = Addon("FreeCompanyAction");
		if (addon is null) {
			this.Fail("FreeCompanyAction vanished before the row could be fired");
			return;
		}

		if (Plugin.Config.FcBuffsDryRun) {
			Plugin.Log.Information(
				$"FcBuffs DRY RUN: would fire FreeCompanyAction [Int=1, UInt={row}] for \"{text}\", then SelectYesno [Int=0]");
			Plugin.Chat.Print($"[FcBuffs] dry run -- would activate \"{text}\" at row {row}. Nothing was pressed.");
			this.Step = ActivationStep.Done;
			return;
		}

		var values = stackalloc AtkValue[2];
		values[0].Type = AtkValueType.Int;
		values[0].Int = 1;
		values[1].Type = AtkValueType.UInt;
		values[1].UInt = (uint)row;
		addon->FireCallback(2, values, true);

		this.firedRow = row;
		Plugin.Log.Information($"FcBuffs: fired row {row} (\"{text}\")");
		this.Enter(ActivationStep.Executing);
	}

	/// <summary>
	/// Picking a row opens a CONTEXT MENU at the cursor -- Execute Action, then Discard Action --
	/// rather than going straight to a confirmation.
	///
	/// ⚠⚠ This step did not exist in the first implementation, because the recorder's allowlist did
	/// not match "ContextMenu" and so the step was absent from the recording. It surfaced as
	/// "step Confirming timed out" with a menu left open under the cursor.
	/// </summary>
	private void TickExecuting() {
		var menu = Addon("ContextMenu");
		if (menu is null)
			return;   // not up yet; the step timeout is the backstop

		if (this.waited++ < SettleTicks)
			return;

		// ⚠⚠ Item 1 is Discard Action, which destroys the buff outright. Verify the order before
		// firing an index at it -- this is the only step in the feature that can lose something.
		string? first = FcBuffReader.ContextMenuFirstItem(menu);
		if (first is null || !first.Equals("Execute Action", StringComparison.OrdinalIgnoreCase)) {
			// ⭐ Print what the menu ACTUALLY holds at the moment of refusal. A guard that only says
			// "not what I expected" sends you hunting for a state you must then reproduce by hand;
			// one that prints the state it saw has already done the hunting -- and that is exactly
			// how this check's own blind spot got found.
			Plugin.Log.Warning($"FcBuffs: context menu AtkValues = [{DescribeMenu(menu)}]");
			this.Fail($"context menu item 0 is \"{first ?? "unreadable"}\", not Execute Action -- refusing to pick an index");
			return;
		}

		// Recorded verbatim from a real selection: [Int=0, Int=0, UInt=0, Undefined, Undefined].
		// The first value is the event kind, the second is the item index.
		var values = stackalloc AtkValue[5];
		values[0].Type = AtkValueType.Int;
		values[0].Int = 0;
		values[1].Type = AtkValueType.Int;
		values[1].Int = 0;
		values[2].Type = AtkValueType.UInt;
		values[2].UInt = 0;
		values[3].Type = AtkValueType.Undefined;
		values[3].Int = 0;
		values[4].Type = AtkValueType.Undefined;
		values[4].Int = 0;
		menu->FireCallback(5, values, true);

		Plugin.Log.Information("FcBuffs: picked Execute Action");
		this.Enter(ActivationStep.Confirming);
	}

	private void TickConfirming() {
		var yesno = Addon("SelectYesno");
		if (yesno is null)
			return;   // not up yet; the step timeout is the backstop

		if (this.waited++ < SettleTicks)
			return;

		// Recorded verbatim: SelectYesno takes a single Int, 0 for yes.
		var values = stackalloc AtkValue[1];
		values[0].Type = AtkValueType.Int;
		values[0].Int = 0;
		yesno->FireCallback(1, values, true);

		Plugin.Log.Information("FcBuffs: confirmed SelectYesno");
		this.Enter(ActivationStep.Settling);
	}

	private void TickSettling() {
		if (this.waited++ < SettleTicks * 2)
			return;

		// ⭐ Success is the STATUS appearing, not the callback returning. The callback only says the
		// client sent something; the buff being up is the thing that was actually wanted, and it is
		// the same distinction CastWatch draws between "accepted" and "resolved".
		bool up = FcBuffReader.ActiveFamilies()
			.Contains(FcBuffReader.NormaliseName(this.WantedAction));

		if (up) {
			Plugin.Log.Information($"FcBuffs: \"{this.WantedAction}\" is now active");
			this.Completed.Add(this.WantedAction);

			// ⭐ Straight back to Reading with the window still open and the tab still selected.
			// ⚠ Reading re-reads the list from scratch every time, which it must: activating one
			// consumes a row and shifts everything after it. A row index from before this activation
			// would now point at a different action.
			if (this.queue.Count > 0) {
				this.WantedAction = this.queue.Dequeue();
				this.firedRow = -1;
				Plugin.Log.Information($"FcBuffs: continuing with \"{this.WantedAction}\"");
				this.Enter(ActivationStep.Reading);
				return;
			}

			this.Enter(ActivationStep.Closing);
			return;
		}

		this.Fail($"fired row {this.firedRow} but \"{this.WantedAction}\" never appeared on the player");
	}

	/// <summary>
	/// Puts the FC window back.
	///
	/// ⚠⚠ NOT just tidiness. Leaving it open kept an Occupied condition set, which reset the settle
	/// clock on every check -- so the FIRST buff refreshed and the second never became eligible, for
	/// as long as the window stayed up. The plugin was blocking itself with its own side effect, and
	/// from outside that is indistinguishable from "it only does one".
	/// </summary>
	private void TickClosing() {
		if (!this.weOpenedIt) {
			this.Step = ActivationStep.Done;
			return;
		}

		// ⚠⚠ Verify it is GONE, do not assume Hide() finished. Declaring success the instant the call
		// returned is what let the next attempt's Show() race this teardown -- the addon was found,
		// then vanished between being checked and being used. Waiting on the observable end state is
		// the same discipline as checking the row text before firing at it.
		if (this.waited++ == 0) {
			CloseWindow();
			return;
		}

		if (Addon("FreeCompany") is not null)
			return;   // still going; the step timeout is the backstop

		Plugin.Diag("FcBuffs: closed the FC window we opened");
		this.Step = ActivationStep.Done;
	}

	/// <summary>
	/// Closes the FC window through the agent that owns it.
	///
	/// ⚠⚠ NOT FireCloseCallback on the FreeCompany addon. That closed the ACTIONS PANEL and left the
	/// parent frame standing -- an empty window with the tabs still listed and nothing in the body.
	/// The addon is a shell hosting a child, so closing "the window" that way closes the wrong half.
	///
	/// ⭐ Show() opened it, so Hide() closes it. Pairing the call with the one that opened it is the
	/// version that cannot be half-right.
	/// </summary>
	private static void CloseWindow() {
		var agent = AgentFreeCompany.Instance();
		if (agent is not null)
			agent->Hide();
	}
}
