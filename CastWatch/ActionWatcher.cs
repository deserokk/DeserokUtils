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
			if (result && this.ArmIsLive && actionType == ActionType.Action) {
				// Match the adjusted id too, so a watch on a base action still fires when the
				// hotbar sends its upgraded/combo form.
				uint adjusted = ActionManager.Instance()->GetAdjustedActionId(actionId);
				if (actionId == this.WatchedId || adjusted == this.WatchedId) {
					this.Fired = true;
					Plugin.Log.Debug($"CastWatch: {this.WatchedName} accepted (id {actionId}, adjusted {adjusted})");
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
		this.armedAt = DateTime.UtcNow;
	}

	public void Disarm() {
		this.Armed = false;
		this.Fired = false;
		this.WatchedId = 0;
		this.WatchedName = string.Empty;
		this.armedAt = DateTime.MinValue;
	}

	public void Dispose() {
		this.hook?.Disable();
		this.hook?.Dispose();
	}
}
