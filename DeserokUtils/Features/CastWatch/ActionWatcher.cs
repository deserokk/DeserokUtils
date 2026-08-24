using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeserokUtils.Features.CastWatch;

/// <summary>
/// Hooks ActionManager.UseAction and records whether the ONE currently-armed action was accepted.
///
/// Why a hook and not a poll: an instant cast (Swiftcast Raise, any oGCD like Aurora) never
/// produces an observable cast bar, so IsCasting is false for exactly the case this exists to
/// catch. The hook is the only thing that sees it.
///
/// Why ONE armed action and no list: the game runs a single macro at a time -- starting another
/// cancels the first -- so a second slot could never be reached. See README.
/// </summary>
internal sealed unsafe class ActionWatcher: IDisposable {
	/// <summary>
	/// Signature taken verbatim from FFXIVClientStructs.xml shipped in the local Dalamud dev
	/// folder, NOT from memory:
	///   ActionManager.UseAction(ActionType, UInt32, UInt64, UInt32, UseActionMode, UInt32, Boolean*)
	/// If a game patch changes it, this fails to compile rather than silently mis-reading arguments.
	/// </summary>
	private delegate bool UseActionDelegate(
		ActionManager* actionManager,
		ActionType actionType,
		uint actionId,
		ulong targetId,
		uint extraParam,
		ActionManager.UseActionMode mode,
		uint comboRouteId,
		bool* outOptAreaTargeted);

	private readonly Hook<UseActionDelegate>? hook;

	/// <summary>How long an arm survives before it expires on its own.</summary>
	public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(10);

	public bool Armed { get; private set; }
	public uint WatchedId { get; private set; }
	public string WatchedName { get; private set; } = string.Empty;
	public bool Fired { get; private set; }

	/// <summary>
	/// Action or Item, and it is NOT optional to track.
	///
	/// ⚠⚠ The id spaces are separate: action 4571 and item 4571 are unrelated things. Matching on
	/// id alone would have made a watch on any action silently fire on some arbitrary item sharing
	/// its number -- rare, undebuggable, and wrong in the direction that sends a callout you did
	/// not earn.
	/// </summary>
	public ActionType WatchedType { get; private set; } = ActionType.Action;

	/// <summary>True once the watched action was attempted at all, whatever the outcome.</summary>
	public bool SawAttempt { get; private set; }
	/// <summary>What UseAction returned for the LAST attempt. See Attempts before trusting it.</summary>
	public bool LastResult { get; private set; }

	/// <summary>
	/// How many times the watched action was attempted since arming.
	///
	/// ⚠ Not decoration. A "ghetto queue" macro repeats the same /ac a dozen times to beat
	/// animation lock, so the FIRST attempt succeeds and the rest fail against the cooldown.
	/// Reporting only the last result would read fired=True returned=False and look like a bug in
	/// the plugin rather than the shape of the macro.
	/// </summary>
	public int Attempts { get; private set; }

	/// <summary>
	/// The target of the attempt that actually succeeded -- NOT the last one attempted, and not
	/// the current target at check time. Zero if nothing succeeded.
	/// ⚠ This is the target the CLIENT REQUESTED, not confirmation of where the action resolved.
	/// </summary>
	public ulong FiredTargetId { get; private set; }

	/// <summary>Who the placeholders pointed at when the watch was armed. See WatchContext.</summary>
	public WatchContext? Context { get; private set; }

	/// <summary>Which targets count as a hit. Any, unless /watch was given a filter flag.</summary>
	public TargetFilter Filter { get; private set; } = TargetFilter.Any;

	/// <summary>
	/// Successful uses rejected by the filter. Counted apart from Attempts so that "it went off, to
	/// the wrong person" never reads as "it never went off".
	/// </summary>
	public int FilteredOut { get; private set; }

	private DateTime armedAt = DateTime.MinValue;

	/// <summary>True when the arm is live and has not aged out.</summary>
	public bool ArmIsLive => this.Armed && DateTime.UtcNow - this.armedAt < Expiry;

	public bool Available => this.hook is not null;

	public ActionWatcher() {
		nint addr = (nint)ActionManager.MemberFunctionPointers.UseAction;

		// A hook that silently fails to install is the worst outcome here: /ifwatch would report
		// "did not fire" forever and look like a tuning problem. Say so, loudly, once.
		if (addr == nint.Zero) {
			Plugin.Log.Error("CastWatch: could not resolve ActionManager.UseAction. /watch will not work.");
			return;
		}

		// ⚠⚠ CREATED, NOT ENABLED. The hook goes live only while a watch is armed -- see Arm/Disarm.
		//
		// UseAction is one of the hottest functions in the client: every GCD, every oGCD, every item,
		// from a macro spamming a thirteen-line fallback. This watcher is armed for ten seconds at a
		// time, a few times an hour, inside specific macros. Detouring every action in the game for
		// the other 99.9% of the session bought nothing -- the detour called the original and fell
		// straight through a disarmed check.
		//
		// ⭐ Resolving the address at construction is still right: a hook that cannot install must
		// say so at load, not at the moment a macro depends on it.
		this.hook = Plugin.Interop.HookFromAddress<UseActionDelegate>(addr, this.Detour);
		Plugin.Log.Information($"CastWatch: resolved ActionManager.UseAction at 0x{addr:X} (hook enabled only while armed)");
	}

	/// <summary>
	/// Whether an observed action is the one being watched.
	///
	/// ⭐ ONE copy of this rule, because the second copy is what broke it. The upgrade-chain
	/// match lived only here in the detour, while the hardcast check in CastWatchFeature compared
	/// raw id against raw id -- so the level-sync fix worked for instants and oGCDs and silently
	/// did not for anything with a cast bar, which is the case a callout is most wanted for.
	///
	/// ⚠ <paramref name="adjusted"/> and <paramref name="watchedAdjusted"/> come back out because
	/// the diagnostic prints them. They are not a second answer -- the bool is the answer.
	/// </summary>
	internal bool MatchesWatch(ActionType actionType, uint actionId, out uint adjusted, out uint watchedAdjusted) {
		// ⚠ HQ items arrive as id + 1,000,000. Without normalising, watching a Phoenix Down
		// would match the NQ stack and quietly ignore an HQ one -- a gap that only shows up
		// for whoever happens to be carrying the HQ version.
		adjusted = actionType == ActionType.Action
			? ActionManager.Instance()->GetAdjustedActionId(actionId)
			: NormalizeItemId(actionId);
		// ⚠⚠ BOTH DIRECTIONS, and only one of them used to exist. Adjusting the OBSERVED id
		// covers "you watched Sheltron and the game cast Holy Sheltron". It does nothing for the
		// reverse -- you watched Holy Sheltron, got synced to 75, and the game cast plain
		// Sheltron -- which is the common case, because people write macros naming the ability
		// they have at cap and then run roulettes all week.
		//
		// ⭐ deserok found this: "it's the watch that's vulnerable, ifwatch looking for the capped
		// spell and not the lower versions." The failure is silent -- the action goes off, the
		// callout never fires, and nothing anywhere says why.
		//
		// ⚠ Which direction GetAdjustedActionId actually resolves is NOT assumed. Both sides are
		// adjusted and all four combinations compared, so it is correct whichever way the game
		// happens to map it, and the detour's diagnostic prints every id so real use settles it.
		watchedAdjusted = this.WatchedType == ActionType.Action && this.WatchedId != 0
			? ActionManager.Instance()->GetAdjustedActionId(this.WatchedId)
			: this.WatchedId;

		return actionType == this.WatchedType
			&& (actionId == this.WatchedId || adjusted == this.WatchedId
				|| actionId == watchedAdjusted || adjusted == watchedAdjusted);
	}

	private bool Detour(
		ActionManager* actionManager,
		ActionType actionType,
		uint actionId,
		ulong targetId,
		uint extraParam,
		ActionManager.UseActionMode mode,
		uint comboRouteId,
		bool* outOptAreaTargeted) {

		bool result = this.hook!.Original(actionManager, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

		// Guard, but report. An exception thrown from a detour takes the game with it, so this
		// catches -- and therefore it must also log, or a broken watcher looks like a silent one.
		try {
			if (this.ArmIsLive) {
				bool match = this.MatchesWatch(actionType, actionId, out uint adjusted, out uint watchedAdjusted);

				// ⚠ DIAGNOSTIC. Every UseAction seen while armed is reported, matched or not,
				// together with the ORIGINAL's return value. Without this, "it passed anyway"
				// cannot be told apart from "the hook never ran" or "the hook matched the wrong
				// id" -- three different bugs with one symptom.
				// ⚠ targetId is reported so the NEXT question is answered by the same test rather
				// than a second one: does UseAction see the target you picked, or the target after
				// the game redirects it? Aurora self-redirects on an invalid target, so if the id
				// here is your enemy rather than you, a "not self" filter would be reading the
				// wrong thing and the whole target-class idea needs a different signal.
				ulong selfId = Plugin.Objects.LocalPlayer?.GameObjectId ?? 0;
				string who = targetId == selfId ? "SELF"
					: targetId is 0 or 0xE0000000 ? "none"
					: $"0x{targetId:X}";

				Plugin.Diag($"UseAction type={actionType} id={actionId}"
					+ (adjusted != actionId ? $" (adj {adjusted})" : "")
					+ $" vs watch {this.WatchedId}"
					+ (watchedAdjusted != this.WatchedId ? $" (adj {watchedAdjusted})" : "")
					+ $" target={who} returned {result}"
					+ (match ? $"  <== MATCHES {this.WatchedName}" : ""));

				// ⚠ UNCONDITIONAL, not behind Diag, and only when the level-sync path is what saved it.
				// This is the case that was silently broken, so the one line proving it now works should
				// not require diagnostics to have been switched on first.
				if (match && actionId != this.WatchedId && adjusted != this.WatchedId) {
					Plugin.Log.Information(
						$"CastWatch: matched {this.WatchedName} via the upgrade chain -- watched "
						+ $"{this.WatchedId}, cast {actionId}. Trait upgrade or level sync.");
				}

				if (match) {
					this.LastResult = result;
					this.SawAttempt = true;
					this.Attempts++;

					if (result && !this.Fired) {
						bool allowed = this.Context?.Passes(this.Filter, targetId, selfId) ?? true;
						if (allowed) {
							// FIRST allowed success wins and keeps its target. Later attempts in a
							// repeat-macro fail against the cooldown; letting them overwrite would
							// discard the one piece of information worth having.
							this.Fired = true;
							this.FiredTargetId = targetId;
						}
						else {
							// ⚠ Counted SEPARATELY. "It went off, to the wrong person" and "it never
							// went off" are different facts, and a callout suppressed for the first
							// reason with no way to tell which is exactly the silence that costs an
							// evening.
							this.FilteredOut++;
							Plugin.Diag($"filtered out: {this.WatchedName} went to {who}, filter is {this.Filter}");
						}
					}
				}
			}
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "CastWatch: exception in UseAction detour");
		}

		return result;
	}

	/// <summary>HQ items are the same item at +1,000,000. Same thing for our purposes.</summary>
	public static uint NormalizeItemId(uint id) => id >= 1_000_000 ? id - 1_000_000 : id;

	public void Arm(uint id, string name, ActionType type, WatchContext context, TargetFilter filter) {
		this.WatchedType = type;
		this.Filter = filter;
		this.FilteredOut = 0;
		this.Context = context;
		// Arming REPLACES any previous arm. That is what makes a double-press clean: every run of
		// a macro starts from a known state instead of inheriting the last one's result.
		this.Armed = true;
		this.WatchedId = id;
		this.WatchedName = name;
		this.Fired = false;
		this.SawAttempt = false;
		this.LastResult = false;
		this.Attempts = 0;
		this.FiredTargetId = 0;
		this.armedAt = DateTime.UtcNow;

		// Live only from here until Disarm.
		this.hook?.Enable();
	}

	public void Disarm() {
		this.Armed = false;

		// ⚠ Off with the arm. The arm can also age out on its own (see ArmIsLive/Expiry) without
		// Disarm being called, so the detour still checks -- this removes the common case, it does
		// not replace the guard.
		this.hook?.Disable();

		this.Fired = false;
		this.SawAttempt = false;
		this.LastResult = false;
		this.Attempts = 0;
		this.FiredTargetId = 0;
		this.Context = null;
		this.Filter = TargetFilter.Any;
		this.FilteredOut = 0;
		this.WatchedType = ActionType.Action;
		this.WatchedId = 0;
		this.WatchedName = string.Empty;
		this.armedAt = DateTime.MinValue;
	}

	public void Dispose() {
		this.hook?.Disable();
		this.hook?.Dispose();
	}
}
