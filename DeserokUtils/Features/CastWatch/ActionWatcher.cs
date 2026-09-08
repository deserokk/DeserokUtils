using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeserokUtils.Features.CastWatch;

internal sealed unsafe class ActionWatcher: IDisposable {

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

	public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(10);

	public bool Armed { get; private set; }
	public uint WatchedId { get; private set; }
	public string WatchedName { get; private set; } = string.Empty;
	public bool Fired { get; private set; }

	public ActionType WatchedType { get; private set; } = ActionType.Action;

	public bool SawAttempt { get; private set; }

	public bool LastResult { get; private set; }

	public int Attempts { get; private set; }

	public ulong FiredTargetId { get; private set; }

	public WatchContext? Context { get; private set; }

	public TargetFilter Filter { get; private set; } = TargetFilter.Any;

	public int FilteredOut { get; private set; }

	private DateTime armedAt = DateTime.MinValue;

	public bool ArmIsLive => this.Armed && DateTime.UtcNow - this.armedAt < Expiry;

	public bool Available => this.hook is not null;

	public ActionWatcher() {
		nint addr = (nint)ActionManager.MemberFunctionPointers.UseAction;

		if (addr == nint.Zero) {
			Plugin.Log.Error("CastWatch: could not resolve ActionManager.UseAction. /watch will not work.");
			return;
		}

		this.hook = Plugin.Interop.HookFromAddress<UseActionDelegate>(addr, this.Detour);
		Plugin.Log.Information($"CastWatch: resolved ActionManager.UseAction at 0x{addr:X} (hook enabled only while armed)");
	}

	internal bool MatchesWatch(ActionType actionType, uint actionId, out uint adjusted, out uint watchedAdjusted) {

		adjusted = actionType == ActionType.Action
			? ActionManager.Instance()->GetAdjustedActionId(actionId)
			: NormalizeItemId(actionId);

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

		try {
			if (this.ArmIsLive) {
				bool match = this.MatchesWatch(actionType, actionId, out uint adjusted, out uint watchedAdjusted);

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

							this.Fired = true;
							this.FiredTargetId = targetId;
						}
						else {

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

	public static uint NormalizeItemId(uint id) => id >= 1_000_000 ? id - 1_000_000 : id;

	public void Arm(uint id, string name, ActionType type, WatchContext context, TargetFilter filter) {
		this.WatchedType = type;
		this.Filter = filter;
		this.FilteredOut = 0;
		this.Context = context;

		this.Armed = true;
		this.WatchedId = id;
		this.WatchedName = name;
		this.Fired = false;
		this.SawAttempt = false;
		this.LastResult = false;
		this.Attempts = 0;
		this.FiredTargetId = 0;
		this.armedAt = DateTime.UtcNow;

		this.hook?.Enable();
	}

	public void Disarm() {
		this.Armed = false;

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
