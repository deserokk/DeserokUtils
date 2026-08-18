using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace DeserokUtils.Features.Interact;

/// <summary>
/// Watch what the Confirm key actually calls when you operate something in the world.
///
/// ## ⚠⚠ The problem this exists to solve is DESTRUCTIVE, not cosmetic
///
/// Num0 (Confirm) is console-shaped: it targets a lever on the first press and uses it on the
/// second, so you naturally press it repeatedly. **With any menu open, those presses land in the
/// menu instead** -- a cursor appears and activates whatever is under it. deserok keeps consumables
/// at the top of his inventory for quick access, so a Watcher's Tower key-and-wheel run with the
/// inventory open has eaten a raid-grade strength potion in a levelling roulette.
///
/// Everything else in this plugin fails by doing nothing. This one SPENDS something.
///
/// ⭐ A dedicated key cannot have that failure, because it is not the Confirm key. It never confirms
/// a dialogue, never advances text, never lands a cursor in a menu -- there is nothing to route.
///
/// ## What the recording has to answer
///
/// The game has TWO functions here and they are not the same thing:
/// <code>
/// TargetSystem.InteractWithObject(GameObject*, bool checkLineOfSight)
/// TargetSystem.OpenObjectInteraction(GameObject*)
/// </code>
/// Which one does Confirm use? Does the first press (target) differ from the second (use)? And --
/// the part that decides the whole design -- **what object does it pass**, so a command can pick the
/// same one instead of choosing between current target, soft target and nearest-in-range on a guess.
///
/// ⚠ Armed on demand. These are cold, but a diagnostic that installs itself at load is how the
/// per-frame audit happened.
/// </summary>
internal sealed unsafe class InteractSniffer: IDisposable {
	/// <summary>Signatures verbatim from FFXIVClientStructs, not from memory.</summary>
	private delegate ulong InteractDelegate(TargetSystem* self, GameObject* obj, bool checkLineOfSight);

	/// <summary>TargetSystem.OpenObjectInteraction(GameObject*)</summary>
	private delegate void OpenDelegate(TargetSystem* self, GameObject* obj);

	private readonly Hook<InteractDelegate>? interactHook;
	private readonly Hook<OpenDelegate>? openHook;

	private DateTime expiresAt = DateTime.MinValue;
	private int seen;

	/// <summary>Long by default -- a dungeon pull is minutes, not seconds.</summary>
	public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(10);

	public bool Available => this.interactHook is not null || this.openHook is not null;
	public bool Armed { get; private set; }
	public TimeSpan Remaining => this.Armed ? this.expiresAt - DateTime.UtcNow : TimeSpan.Zero;

	public InteractSniffer() {
		nint interact = (nint)TargetSystem.MemberFunctionPointers.InteractWithObject;
		nint open = (nint)TargetSystem.MemberFunctionPointers.OpenObjectInteraction;

		// ⭐ Resolve at construction even though nothing is enabled: a hook that CANNOT install must
		// say so at load, not at the moment somebody depends on it.
		if (interact != nint.Zero)
			this.interactHook = Plugin.Interop.HookFromAddress<InteractDelegate>(interact, this.DetourInteract);
		else
			Plugin.Log.Warning("Interact: could not resolve TargetSystem.InteractWithObject.");

		if (open != nint.Zero)
			this.openHook = Plugin.Interop.HookFromAddress<OpenDelegate>(open, this.DetourOpen);
		else
			Plugin.Log.Warning("Interact: could not resolve TargetSystem.OpenObjectInteraction.");

		Plugin.Log.Information(
			$"Interact: InteractWithObject at 0x{interact:X}, OpenObjectInteraction at 0x{open:X} "
			+ "(hooks enabled only while recording)");
	}

	public void Arm(TimeSpan duration) {
		this.expiresAt = DateTime.UtcNow + duration;
		this.seen = 0;
		if (this.Armed)
			return;
		this.Armed = true;
		this.interactHook?.Enable();
		this.openHook?.Enable();
	}

	public void Disarm() {
		if (!this.Armed)
			return;
		this.Armed = false;
		this.interactHook?.Disable();
		this.openHook?.Disable();
		Plugin.Chat.Print($"[Interact] recorder off. {this.seen} call(s) logged.");
	}

	public void ExpireIfDue() {
		if (this.Armed && DateTime.UtcNow >= this.expiresAt)
			this.Disarm();
	}

	// ── the detours ──────────────────────────────────────────────────────────────────────────

	private ulong DetourInteract(TargetSystem* self, GameObject* obj, bool checkLineOfSight) {
		string snapshot = Describe(obj);
		ulong result = this.interactHook!.Original(self, obj, checkLineOfSight);
		try {
			this.Record($"InteractWithObject({snapshot}, checkLineOfSight: {checkLineOfSight}) -> {result}");
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "Interact sniffer threw in InteractWithObject.");
		}
		return result;
	}

	private void DetourOpen(TargetSystem* self, GameObject* obj) {
		string snapshot = Describe(obj);
		this.openHook!.Original(self, obj);
		try {
			this.Record($"OpenObjectInteraction({snapshot})");
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "Interact sniffer threw in OpenObjectInteraction.");
		}
	}

	/// <summary>
	/// ⚠ Reports the OBJECT KIND and the data id as well as the name. The name alone will not
	/// distinguish "the lever" from "a lever" across a dungeon, and the design question is which
	/// class of thing a command should look for -- not which one deserok happened to stand next to.
	/// </summary>
	private static string Describe(GameObject* obj) {
		if (obj is null)
			return "obj=NULL";
		return $"\"{obj->NameString}\" kind={obj->ObjectKind} dataId={obj->BaseId} entityId=0x{obj->EntityId:X}";
	}

	private void Record(string line) {
		this.seen++;
		// ⚠ Unconditional, not Diag. Three features this week produced an empty dalamud.log on their
		// first test because the decisions only went to a channel that is off by default. A dungeon
		// run is a handful of interactions, not a flood.
		Plugin.Log.Information($"Interact sniff: {line}");
		Plugin.Chat.Print($"[Interact] {line}");
	}

	public void Dispose() {
		this.interactHook?.Dispose();
		this.openHook?.Dispose();
	}
}
