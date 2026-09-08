using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DeserokUtils.Features.DrawSheathe;

internal sealed unsafe class WeaponStateSniffer: IDisposable {

	private delegate bool SetUnsheathedDelegate(WeaponState* self, bool newState, bool sendPacket, bool isInstant);

	private delegate bool SetUnsheathed2Delegate(WeaponState* self, bool newState);

	private readonly Hook<SetUnsheathedDelegate>? setUnsheathed;
	private readonly Hook<SetUnsheathed2Delegate>? setUnsheathed2;

	private DateTime expiresAt = DateTime.MinValue;
	private int seen;

	public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(60);

	public bool Available => this.setUnsheathed is not null || this.setUnsheathed2 is not null;
	public bool Armed { get; private set; }
	public TimeSpan Remaining => this.Armed ? this.expiresAt - DateTime.UtcNow : TimeSpan.Zero;

	public WeaponStateSniffer() {
		nint one = (nint)WeaponState.MemberFunctionPointers.SetUnsheathed;
		nint two = (nint)WeaponState.MemberFunctionPointers.SetUnsheathed2;

		if (one != nint.Zero)
			this.setUnsheathed = Plugin.Interop.HookFromAddress<SetUnsheathedDelegate>(one, this.DetourSetUnsheathed);
		else
			Plugin.Log.Warning("DrawSheathe: could not resolve WeaponState.SetUnsheathed.");

		if (two != nint.Zero)
			this.setUnsheathed2 = Plugin.Interop.HookFromAddress<SetUnsheathed2Delegate>(two, this.DetourSetUnsheathed2);
		else
			Plugin.Log.Warning("DrawSheathe: could not resolve WeaponState.SetUnsheathed2.");

		Plugin.Log.Information(
			$"DrawSheathe: SetUnsheathed at 0x{one:X}, SetUnsheathed2 at 0x{two:X} (hooks enabled only while sniffing)");
	}

	public void Arm(TimeSpan duration) {
		this.expiresAt = DateTime.UtcNow + duration;
		this.seen = 0;
		if (this.Armed)
			return;
		this.Armed = true;
		this.setUnsheathed?.Enable();
		this.setUnsheathed2?.Enable();
	}

	public void Disarm() {
		if (!this.Armed)
			return;
		this.Armed = false;
		this.setUnsheathed?.Disable();
		this.setUnsheathed2?.Disable();
		Plugin.Chat.Print($"[DrawSheathe] sniffer off. {this.seen} call(s) recorded -- full detail is in /xllog.");
	}

	public void ExpireIfDue() {
		if (this.Armed && DateTime.UtcNow >= this.expiresAt)
			this.Disarm();
	}

	private bool DetourSetUnsheathed(WeaponState* self, bool newState, bool sendPacket, bool isInstant) {
		bool before = self is not null && self->IsUnsheathed;
		bool result = this.setUnsheathed!.Original(self, newState, sendPacket, isInstant);

		try {
			this.Record(
				$"SetUnsheathed(newState: {newState}, sendPacket: {sendPacket}, isInstant: {isInstant}) -> {result}",
				before, self);
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "DrawSheathe sniffer threw in SetUnsheathed.");
		}

		return result;
	}

	private bool DetourSetUnsheathed2(WeaponState* self, bool newState) {
		bool before = self is not null && self->IsUnsheathed;
		bool result = this.setUnsheathed2!.Original(self, newState);

		try {
			this.Record($"SetUnsheathed2(newState: {newState}) -> {result}", before, self);
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "DrawSheathe sniffer threw in SetUnsheathed2.");
		}

		return result;
	}

	private void Record(string call, bool before, WeaponState* self) {
		this.seen++;
		bool after = self is not null && self->IsUnsheathed;
		string moving = DrawSheatheFeature.PlayerIsMoving() switch {
			true => "yes",
			false => "no",
			null => "unreadable",
		};

		string line = $"{call} | IsUnsheathed {before} -> {after} | moving={moving}";
		Plugin.Log.Information($"DrawSheathe sniff: {line}");

		Plugin.Chat.Print($"[DrawSheathe] {line}");
	}

	public void Dispose() {
		this.setUnsheathed?.Dispose();
		this.setUnsheathed2?.Dispose();
	}
}
