using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace DeserokUtils.Features.Interact;

internal sealed unsafe class InteractSniffer: IDisposable {

	private delegate ulong InteractDelegate(TargetSystem* self, GameObject* obj, bool checkLineOfSight);

	private delegate void OpenDelegate(TargetSystem* self, GameObject* obj);

	private readonly Hook<InteractDelegate>? interactHook;
	private readonly Hook<OpenDelegate>? openHook;

	private DateTime expiresAt = DateTime.MinValue;
	private int seen;

	public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(10);

	public bool Available => this.interactHook is not null || this.openHook is not null;
	public bool Armed { get; private set; }
	public TimeSpan Remaining => this.Armed ? this.expiresAt - DateTime.UtcNow : TimeSpan.Zero;

	public InteractSniffer() {
		nint interact = (nint)TargetSystem.MemberFunctionPointers.InteractWithObject;
		nint open = (nint)TargetSystem.MemberFunctionPointers.OpenObjectInteraction;

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

	private static string Describe(GameObject* obj) {
		if (obj is null)
			return "obj=NULL";
		return $"\"{obj->NameString}\" kind={obj->ObjectKind} dataId={obj->BaseId} entityId=0x{obj->EntityId:X}";
	}

	private void Record(string line) {
		this.seen++;

		SniffLog.Write($"INTERACT {line}");
		Plugin.Chat.Print($"[Interact] {line}");
	}

	public void Dispose() {
		this.interactHook?.Dispose();
		this.openHook?.Dispose();
	}
}
