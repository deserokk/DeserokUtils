using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace CastWatch;

/// <summary>
/// Hooks ActionManager.UseAction and records whether the ONE currently-armed action was accepted.
///
/// Why a hook and not a poll: an instant cast (Swiftcast Raise, any oGCD like Aurora) never
/// produces an observable cast bar, so IsCasting is false for exactly the case this exists to
/// catch. The hook is the only thing that sees it.
///
/// Why ONE armed action and no list: the game runs a single macro at a time -- starting another
/// cancels the first -- so a second slot could never be reached. See README.
///
/// Cost when disarmed: two comparisons per UseAction call. UseAction fires a few times a second
/// at most, so an idle watcher is free.
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

	/// <summary>True once the watched action was attempted at all, whatever the outcome.</summary>
	public bool SawAttempt { get; private set; }
	/// <summary>What UseAction returned for the last attempt of the watched action.</summary>
	public bool LastResult { get; private set; }

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

		this.hook = Plugin.Interop.HookFromAddress<UseActionDelegate>(addr, this.Detour);
		this.hook.Enable();
		Plugin.Log.Information($"CastWatch: hooked ActionManager.UseAction at 0x{addr:X}");
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
				uint adjusted = actionType == ActionType.Action
					? ActionManager.Instance()->GetAdjustedActionId(actionId)
					: actionId;
				bool match = actionType == ActionType.Action
					&& (actionId == this.WatchedId || adjusted == this.WatchedId);

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
					+ $" target={who} returned {result}"
					+ (match ? $"  <== MATCHES {this.WatchedName}" : ""));

				if (match) {
					this.LastResult = result;
					this.SawAttempt = true;
					if (result)
						this.Fired = true;
				}
			}
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "CastWatch: exception in UseAction detour");
		}

		return result;
	}

	public void Arm(uint id, string name) {
		// Arming REPLACES any previous arm. That is what makes a double-press clean: every run of
		// a macro starts from a known state instead of inheriting the last one's result.
		this.Armed = true;
		this.WatchedId = id;
		this.WatchedName = name;
		this.Fired = false;
		this.SawAttempt = false;
		this.LastResult = false;
		this.armedAt = DateTime.UtcNow;
	}

	public void Disarm() {
		this.Armed = false;
		this.Fired = false;
		this.SawAttempt = false;
		this.LastResult = false;
		this.WatchedId = 0;
		this.WatchedName = string.Empty;
		this.armedAt = DateTime.MinValue;
	}

	public void Dispose() {
		this.hook?.Disable();
		this.hook?.Dispose();
	}
}
