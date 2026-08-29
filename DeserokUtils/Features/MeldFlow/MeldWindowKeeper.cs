using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.MeldFlow;

/// <summary>
/// Keeps the materia meld window open when melding for someone else, the way it already stays open
/// when melding your own gear.
///
/// ## The problem
///
/// Melding your own gear leaves the window up: pick the next item, pick the next materia, carry on.
/// Requesting a meld from another player closes it after every single materia, so the requester
/// repeats right-click, Request Materia Melding, find the piece, pick the materia, Request. Twenty
/// times for a set. The person accepting can have auto-accept and feel nothing; the entire cost
/// lands on whoever is asking.
///
/// ## What the recording showed
///
/// A temporary addon-flow recording caught the whole flow. Three facts came out of it, none of them
/// guessed:
///
///  - The window is <c>MateriaAttach</c>; <c>MateriaAttachDialog</c> is the confirmation prompt.
///  - The game already remembers the window's POSITION across opens : it reopened at the same
///    x=795 y=434 every time : so nothing here needs to save or restore coordinates.
///  - The window always opens on the Inventory tab, and the selected tab is NOT recoverable from
///    the addon: by the time it closes, its AtkValues have been repurposed for the meld result
///    view. It lives on the AGENT instead, as <c>Category</c>, and the agent outlives the window.
///
/// ## How it decides whether to reopen
///
/// ⚠⚠ Reopening on every close would trap the window open forever : Escape would close it and we
/// would put it straight back. The recording gives a clean way to tell the two apart: a completed
/// meld always shows <c>MateriaAttachDialog</c> opening and closing shortly BEFORE
/// <c>MateriaAttach</c> finalizes, and a manual Escape shows no dialog at all. So a reopen requires
/// a recently confirmed dialog, and pressing Escape genuinely closes the window.
///
/// ⚠ This reopens a window. It does not select gear, choose materia, or press Request : each meld
/// is still entirely deserok's stated line: *"I can maybe make something that puts an icon over
/// their heads for you, but I will never make something that presses the LB for you."*
/// </summary>
internal sealed unsafe class MeldWindowKeeper: IDisposable {
	public string TabTitle => "MeldWindow";

	/// <summary>
	/// How recently the confirmation dialog must have been seen for a close to count as "a meld just
	/// finished" rather than "the window was dismissed".
	///
	/// ⭐ Measured, not guessed: in the recording the gap between the dialog closing and the window
	/// finalizing was 72ms and 1ms. Three seconds is far wider than anything observed, which is the
	/// right side to err on : the cost of being too generous is one unwanted reopen, and the cost of
	/// being too tight is the feature silently not working.
	/// </summary>
	private static readonly TimeSpan DialogWindow = TimeSpan.FromSeconds(3);

	private DateTime lastDialogAt = DateTime.MinValue;

	/// <summary>
	/// The tab, sampled continuously while the window is open.
	///
	/// ⚠⚠ It CANNOT be read when the window closes. The trace showed category=None at PreFinalize:
	/// the agent has already cleared it by then, exactly as the addon has already repurposed its
	/// AtkValues. The only place the selected tab is true is while the thing is actually on screen,
	/// so it is cached every tick and the cached value is what gets restored.
	/// </summary>
	private AgentMateriaAttach.FilterCategory lastCategory = AgentMateriaAttach.FilterCategory.None;

	/// <summary>Set when a close looks like a finished meld. Cleared once the reopen is attempted.</summary>
	/// <summary>Gives up if the agent never releases, so a failed reopen cannot arm itself forever.</summary>
	private int openDialogs;

	private bool meldFinished;

	/// <summary>Ticks left to write the tab back after Show(), letting the window build first.</summary>
	private int restoreTicks;

	/// <summary>
	/// The tab to put back, frozen at the moment the command is sent.
	///
	/// ⚠⚠ Separate from lastCategory ON PURPOSE. The tick loop keeps lastCategory fresh by sampling
	/// the agent every frame, so when the reopened window came up on Inventory the sampler
	/// overwrote the saved ArmouryWeapon a few frames before the restore ran, and the restore
	/// faithfully put Inventory back over Inventory. A value being restored cannot live in the same
	/// field as a value being continuously updated.
	/// </summary>
	private AgentMateriaAttach.FilterCategory categoryToRestore = AgentMateriaAttach.FilterCategory.None;

	public MeldWindowKeeper() {
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "MateriaAttachDialog", this.OnDialog);
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MateriaAttachDialog", this.OnDialogClosing);
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MateriaAttach", this.OnWindowClosing);
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "MateriaAttach", this.OnWindowOpened);
		Plugin.Framework.Update += this.OnUpdate;

	}

	public void Dispose() {
		Plugin.AddonLifecycle.UnregisterListener(this.OnDialog);
		Plugin.AddonLifecycle.UnregisterListener(this.OnWindowClosing);
		Plugin.AddonLifecycle.UnregisterListener(this.OnWindowOpened);
		Plugin.AddonLifecycle.UnregisterListener(this.OnDialogClosing);
		Plugin.Framework.Update -= this.OnUpdate;
	}

	/// <summary>
	/// MateriaAttachDialog's role, read from its first AtkValue.
	///
	/// ⭐⭐ Recorded across all four cases, because ONE addon serves them all and the title is not
	/// enough to tell them apart : three of these say "Materia Meld Request".
	///
	///   0 = your own self-meld confirmation
	///   2 = your outgoing request, the Submit Request panel
	///   3 = your outgoing request, the Remove Request panel waiting for the crafter
	///   4 = someone else's incoming request, asking you to accept
	///
	/// ⚠⚠ This distinction is the entire feature. deserok already had auto-accept via YesAlready and
	/// it broke his own melding : *"It made my self melds break, so I had to toggle it on for other
	/// people melds then off for self."* A generic yes-clicker sees one addon name and answers all
	/// four. Gating on 4 means his self melds and his own outgoing requests are untouched by
	/// construction, rather than by a toggle he has to remember to flip.
	/// </summary>
	private const int IncomingRequest = 4;

	private bool acceptPending;

	private void OnDialog(AddonEvent type, AddonArgs args) {
		this.lastDialogAt = DateTime.UtcNow;
		this.openDialogs++;

		if (!Plugin.Config.MeldAutoAccept || args.Addon.IsNull)
			return;

		var addon = (AtkUnitBase*)args.Addon.Address;
		if (addon == null || addon->AtkValues == null || addon->AtkValuesCount == 0)
			return;

		if (addon->AtkValues[0].Int != IncomingRequest)
			return;

		// ⚠ Never answered inside PostSetup. The addon is still being built, and deserok runs
		// YesAlready alongside this : two things replying to the same prompt in the same frame is
		// how you get the doubled and stacked dialogs already seen in his logs.
		this.acceptPending = true;
	}

	/// <summary>
	/// ⭐ The payload is recorded, not invented: pressing Meld on an incoming request sends three
	/// zeroed ints with close=true. Same rule as the tab switch.
	/// </summary>
	private void AcceptIncoming() {
		var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("MateriaAttachDialog").Address;
		if (addon == null || !addon->IsVisible || addon->AtkValues == null || addon->AtkValuesCount == 0)
			return;

		// Re-checked on this frame. The prompt we saw a frame ago may have been answered already,
		// by the player or by another plugin, and the addon reused for something else entirely.
		if (addon->AtkValues[0].Int != IncomingRequest)
			return;

		var values = stackalloc AtkValue[3];
		values[0].SetInt(0);
		values[1].SetInt(0);
		values[2].SetInt(0);
		addon->FireCallback(3, values, true);
	}

	/// <summary>
	/// ⚠⚠ THIS is the moment to reopen, not when the main window closes.
	///
	/// Requesting a meld leaves a second MateriaAttachDialog up - the panel with "Remove Request" -
	/// which sits there for as long as the crafter takes to accept, holding the agent active the
	/// whole time. Recorded gaps between the main window closing and that panel closing were 6.7s
	/// and 18.9s in two consecutive melds, so it is the other player's reaction time and no timeout
	/// could ever be right. An earlier build retried Show() for ten seconds, gave up nine seconds
	/// early, and reported agentActive=True throughout.
	///
	/// ⭐ Dialogs are COUNTED rather than timed, because two of them are open at once: the confirm
	/// sub-dialog closes within milliseconds of the main window, and the waiting panel closes much
	/// later. Only the count reaching zero means the request is genuinely finished.
	/// </summary>
	/// <summary>
	/// The main window closing only NOTES that a meld is in flight. The reopen waits for the request
	/// to clear - see OnDialogClosing.
	/// </summary>
	private void OnWindowClosing(AddonEvent type, AddonArgs args) {
		if (!Plugin.Config.MeldWindowKeepOpen)
			return;

		if (DateTime.UtcNow - this.lastDialogAt > DialogWindow)
			return;

		this.meldFinished = true;
	}

	private void OnDialogClosing(AddonEvent type, AddonArgs args) {
		if (this.openDialogs > 0)
			this.openDialogs--;

		if (!this.meldFinished || this.openDialogs > 0)
			return;

		this.meldFinished = false;
		this.Reopen();
	}

	/// <summary>
	/// ⚠⚠ The ONLY trustworthy sign the reopen worked. The previous attempt asked the agent
	/// IsAddonShown() one tick after the close and got true - from the window that was still being
	/// destroyed - so it declared success 14ms in and stopped retrying while nothing was on screen.
	/// A PostSetup is the game telling us a window was actually built.
	/// </summary>
	/// <summary>
	/// Remembers who the meld was being requested from, so a reopen cannot be aimed at a stranger.
	///
	/// ⚠⚠ Bare /meldrequest targets WHOEVER IS CURRENTLY TARGETED, and a meld can sit waiting for
	/// twenty seconds, so firing it blind could send a meld request to a total stranger the player
	/// happened to click. The crafter is captured when the window first opens and deliberately
	/// re-targeted before the command goes out, so the command can only ever reach the person the
	/// meld was already being requested from.
	/// </summary>
	private ulong crafterId;


	private void OnWindowOpened(AddonEvent type, AddonArgs args) {
		// Whoever is targeted when the meld window appears is the crafter being asked.
		var target = Plugin.Targets.Target;
		if (target is { ObjectKind: ObjectKind.Pc })
			this.crafterId = target.GameObjectId;
	}

	/// <summary>
	/// ⭐⭐ THE OPENER IS A TEXT COMMAND. TextCommand[136] is <c>/meldrequest</c>: "Sends a materia
	/// meld request to the specified PC. Sends request to current target when no PC is specified."
	///
	/// This replaced a dead end. AgentMateriaAttach.Show() looked like the obvious way in and does
	/// nothing whatsoever : the agent snapshot came back byte-identical before and after the call,
	/// with dataNull=true, because the agent carries no meld context until something sets it up.
	/// Two sheets were searched for an opener (MainCommand, Addon) before deserok found the command
	/// himself; TextCommand was the sheet that had the answer, and it is the same sheet this plugin
	/// already reads for the draw and sheathe emotes.
	///
	/// ⭐ Sent through GameCommands.Queue, the path DrawSheathe already uses, rather than a second
	/// command mechanism invented here.
	/// </summary>
	private void Reopen() {
		if (this.crafterId == 0) {
			Plugin.Log.Information("[MeldWindow] no crafter recorded; not reopening.");
			return;
		}

		var crafter = Plugin.Objects.SearchById(this.crafterId);
		if (crafter == null) {
			Plugin.Log.Information("[MeldWindow] crafter is no longer nearby; not reopening.");
			return;
		}

		// ⭐ Targets rather than focuses, per deserok: focus "doesn't clean itself up", and melding is
		// always done standing still somewhere safe, so borrowing the main target costs nothing. It
		// is also left ON the crafter afterwards rather than restored - mid-session that is exactly
		// where the target wants to be anyway, so putting something else back would be the rude
		// version.
		Plugin.Targets.Target = crafter;
		GameCommands.Queue("/meldrequest");
		this.categoryToRestore = this.lastCategory;
		this.restoreTicks = 30;
	}

	/// <summary>
	/// Switches the meld window's gear tab by firing the control's own callback.
	///
	/// ⚠⚠ Writing AgentMateriaAttach.Category does NOT work, despite being where the tab is stored.
	/// The log read "tab restored to ArmouryWeapon" while the window sat on Inventory: the field is
	/// where the control records its answer, and nothing re-reads it. The window has to be told.
	///
	/// ⭐ The payload is recorded, not deduced. Clicking through the tabs by hand produced exactly
	/// one shape per switch : two ints, [0]=0 and [1]=the FilterCategory index : with the follow-up
	/// [0]=6 callbacks being the list refreshing itself afterwards. close=true is what the game
	/// itself passes here and does not close anything, so it is mirrored rather than second-guessed.
	/// </summary>
	private static void SelectTab(AgentMateriaAttach.FilterCategory category) {
		// GetAddonByName hands back Dalamud's readonly AtkUnitBasePtr wrapper; FireCallback needs the
		// real struct, so go through .Address. Same pattern Fanfare's PopupSuppressor uses.
		var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("MateriaAttach").Address;
		if (addon == null || !addon->IsVisible)
			return;

		var values = stackalloc AtkValue[2];
		values[0].SetInt(0);
		values[1].SetInt((int)category);
		addon->FireCallback(2, values, true);
	}

	private void OnUpdate(IFramework framework) {
		if (this.acceptPending) {
			this.acceptPending = false;
			this.AcceptIncoming();
		}

		var agent = AgentMateriaAttach.Instance();
		if (agent == null)
			return;

		// ⚠ Sampling PAUSES while a restore is in flight. Otherwise the reopened window's default
		// Inventory tab gets captured as "the tab deserok was on" before we have put the real one
		// back.
		if (this.restoreTicks == 0) {
			if (agent->Category != AgentMateriaAttach.FilterCategory.None)
				this.lastCategory = agent->Category;

			return;
		}

		// ⚠ Counted down over frames rather than applied at once: the command has to travel through
		// the shell and build a window before there is anything to set the tab on.
		if (--this.restoreTicks > 0)
			return;

		if (this.categoryToRestore == AgentMateriaAttach.FilterCategory.None)
			return;

		SelectTab(this.categoryToRestore);
		this.lastCategory = this.categoryToRestore;

	}

	public void DrawTab() {
		ImGui.TextWrapped(
			"Melding your own gear keeps the window open. Melding for someone else closes it after "
			+ "every materia. This puts it back, on the tab you were using.");
		ImGui.Spacing();

		bool enabled = Plugin.Config.MeldWindowKeepOpen;
		if (ImGui.Checkbox("Reopen the meld window after each meld", ref enabled)) {
			Plugin.Config.MeldWindowKeepOpen = enabled;
			Plugin.Config.Save();
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Closing it yourself still closes it. Only a completed meld reopens it.");

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		bool autoAccept = Plugin.Config.MeldAutoAccept;
		if (ImGui.Checkbox("Accept incoming meld requests automatically", ref autoAccept)) {
			Plugin.Config.MeldAutoAccept = autoAccept;
			Plugin.Config.Save();
		}

		ImGui.TextDisabled("Only other people's requests. Your own melds are never answered for you.");
	}
}
