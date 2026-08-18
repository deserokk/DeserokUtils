using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DeserokUtils.Features.DrawSheathe;

/// <summary>
/// Watch what the GAME does when you press the real draw/sheathe key, instead of arguing about it.
///
/// ⭐⭐ This exists because reasoning lost, and it won in one press. `SetUnsheathed(newState,
/// sendPacket: true, isInstant: false)` looked like an exact description of "draw normally, with the
/// animation" -- the parameter names are FFXIVClientStructs' own -- and it teleported the weapon
/// into the hand. The recording showed the real keybind passing `isInstant: TRUE` every time, which
/// is the call that animates. The flag does not mean what it is named, and nothing short of
/// watching the game do it would have said so.
///
/// ⚠ Kept rather than deleted once it had answered. It cost one file, it is inert unless armed, and
/// the next question of this shape -- "what does the client actually do when I press that?" -- is
/// one keypress away instead of one build away.
///
/// ⚠ Only two of WeaponState's functions have a resolvable address -- SetUnsheathed and
/// SetUnsheathed2. OnActorControlWeaponDrawn and Tick are declared but carry no signature, so they
/// cannot be hooked. That bounds what this can prove, and the bound is informative: if pressing the
/// key logs NEITHER call, the animation is not driven from WeaponState and looking there any
/// further is wasted.
///
/// ⚠ Armed, never permanent. Both functions are cold -- they fire when you draw or sheathe and at
/// no other time -- so an idle hook here would be far cheaper than the UseAction one CastWatch had
/// to fix. It is still armed on demand, because a diagnostic that installs itself at load is how
/// you end up with the per-frame audit again.
/// </summary>
internal sealed unsafe class WeaponStateSniffer: IDisposable {
	/// <summary>
	/// Signatures verbatim from FFXIVClientStructs, NOT from memory:
	///   WeaponState.SetUnsheathed(Boolean newState, Boolean sendPacket, Boolean isInstant) : Boolean
	/// A patch that changes them breaks the build instead of silently mis-reading arguments.
	/// </summary>
	private delegate bool SetUnsheathedDelegate(WeaponState* self, bool newState, bool sendPacket, bool isInstant);

	/// <summary>WeaponState.SetUnsheathed2(Boolean newState) : Boolean</summary>
	private delegate bool SetUnsheathed2Delegate(WeaponState* self, bool newState);

	private readonly Hook<SetUnsheathedDelegate>? setUnsheathed;
	private readonly Hook<SetUnsheathed2Delegate>? setUnsheathed2;

	private DateTime expiresAt = DateTime.MinValue;
	private int seen;

	/// <summary>How long an arm lasts if nothing disarms it sooner.</summary>
	public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(60);

	public bool Available => this.setUnsheathed is not null || this.setUnsheathed2 is not null;
	public bool Armed { get; private set; }
	public TimeSpan Remaining => this.Armed ? this.expiresAt - DateTime.UtcNow : TimeSpan.Zero;

	public WeaponStateSniffer() {
		nint one = (nint)WeaponState.MemberFunctionPointers.SetUnsheathed;
		nint two = (nint)WeaponState.MemberFunctionPointers.SetUnsheathed2;

		// ⭐ Resolve at construction even though nothing is enabled: a hook that CANNOT install must
		// say so at load, not at the moment somebody depends on it.
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

	/// <summary>
	/// Expire lazily rather than on a Tick.
	///
	/// ⚠ Which means an arm nobody follows up on survives until the next draw or the next time the
	/// tab is open. That is deliberate: adding a per-frame callback to carry a diagnostic's egg timer
	/// is exactly the shape of work this project has already had to strip out twice, and these two
	/// functions cost nothing while idle.
	/// </summary>
	public void ExpireIfDue() {
		if (this.Armed && DateTime.UtcNow >= this.expiresAt)
			this.Disarm();
	}

	// ── the detours ──────────────────────────────────────────────────────────────────────────

	private bool DetourSetUnsheathed(WeaponState* self, bool newState, bool sendPacket, bool isInstant) {
		bool before = self is not null && self->IsUnsheathed;
		bool result = this.setUnsheathed!.Original(self, newState, sendPacket, isInstant);

		// An exception out of a detour takes the game with it, so this catches -- and therefore it
		// must also log, or a broken sniffer is indistinguishable from a quiet one.
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

	/// <summary>
	/// ⚠ Reports the state BEFORE and AFTER around the original call, not just the arguments. "What
	/// was passed" and "what actually changed" are two different facts, and the whole reason this
	/// exists is that a call which looked right did not produce the effect its arguments described.
	/// </summary>
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
		// ⚠ Chat as well as the log, unlike the FcBuffs recorder. That one reconstructed a long
		// sequence afterwards; this answers one question about the key you just pressed, and reading
		// it while the animation is still fresh is the entire point.
		Plugin.Chat.Print($"[DrawSheathe] {line}");
	}

	public void Dispose() {
		this.setUnsheathed?.Dispose();
		this.setUnsheathed2?.Dispose();
	}
}
