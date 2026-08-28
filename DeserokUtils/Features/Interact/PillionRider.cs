using System.Numerics;

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace DeserokUtils.Features.Interact;

/// <summary>
/// Hop on a party member's mount, without the right-click menu.
///
/// ## Why this is part of the interact key
///
/// ⚠⚠ A REGRESSION WE CAUSED, and it is worth writing down how. deserok's interact macro was nine
/// lines, not one:
/// <code>
/// /merror off
/// /dsuinteract
/// /ridepillion &lt;2&gt;   ... through &lt;8&gt;
/// </code>
/// Moving him to a direct keybind in v1.14.0 replaced line 2 and silently dropped lines 3 to 9. A
/// keybind runs the command it is bound to; the macro was doing more than the command inside it. He
/// had shown that macro at the start of the same session that broke it.
///
/// What it was worth: *"pillions in the base game are annoying... Normally it requires right
/// clicking, then selecting 'ride pillion' out of however many drop down options, every single time.
/// When grinding fates together it ends up making us sit there for a few awkward seconds as someone
/// right clicks and navigates."*
///
/// ⭐ The placeholders were party MEMBERS, not seats, which the TextCommand sheet settles outright:
/// *"/ridepillion &lt;2&gt; 1 (Ride in seat 1 of party member 2.)"*. The macro tried each member in
/// turn and <c>/merror off</c> hid the seven that missed.
///
/// ## ⭐⭐ MEASURED 2026-08-28, because the signature fit two opposite readings
///
/// There is no Action row for ride pillion, so UseAction is not the route.
/// <c>BattleChara.RidePillion(uint)</c> is, and it could equally have meant
/// <c>mountOwner-&gt;RidePillion(seat)</c> or <c>localPlayer-&gt;RidePillion(entityId)</c>. Hooking it
/// and riding pillion once the normal way answered it in one line:
/// <code>
/// RidePillion called on "Peachi Bunni" entityId=269435655 isLocalPlayer=False mounted=True | value=0
/// </code>
/// So it is called on the MOUNT OWNER, and the number is a ZERO-BASED SEAT -- the command's "seat 1"
/// is native 0. The recorder was deleted once it had said this; the line above is what it was for.
///
/// ⭐ And seat availability never has to be computed. The command's own help promises *"if the
/// desired seat is already selected, you will be placed in the next available seat"*, so passing 0
/// makes the game walk the seats itself. That is a whole class of bookkeeping the Mount sheet's
/// ExtraSeats column would otherwise have required.
/// </summary>
internal static class PillionRider {
	/// <summary>
	/// ⚠ Generous, and it is not the real gate. The game enforces pillion range itself and refuses;
	/// this only stops us picking somebody across the zone as "the nearest" and eating that refusal.
	/// </summary>
	private const float Reach = 15f;

	/// <summary>Seat 0. The game advances to the next free one on its own.</summary>
	private const uint FirstSeat = 0;

	/// <summary>True if a mount was joined.</summary>
	public static unsafe bool TryRide() {
		var me = Plugin.Objects.LocalPlayer;
		if (me is null)
			return false;

		// ⚠ Both of these would make the call meaningless rather than harmful, but a refusal message
		// in the log every time you press the key near a friend is its own kind of broken.
		var self = (Character*)me.Address;
		if (self->IsMounted()) {
			Plugin.Diag("Interact: not pillioning -- already mounted.");
			return false;
		}
		if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.RidingPillion]) {
			Plugin.Diag("Interact: not pillioning -- already riding pillion.");
			return false;
		}

		Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
		float bestDistance = float.MaxValue;

		foreach (var candidate in Plugin.Objects) {
			if (candidate.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc)
				continue;
			if (candidate.Address == me.Address || !InParty(candidate))
				continue;

			float distance = Vector3.Distance(candidate.Position, me.Position);
			if (distance > Reach)
				continue;

			// ⭐ The game's own answer to "is there a mount to get on", rather than reading the Mount
			// sheet and reasoning about seats.
			if (!((Character*)candidate.Address)->IsMounted()) {
				Plugin.Diag($"Interact: \"{candidate.Name}\" is in range but not mounted.");
				continue;
			}

			if (distance < bestDistance) {
				best = candidate;
				bestDistance = distance;
			}
		}

		if (best is null)
			return false;

		((BattleChara*)best.Address)->RidePillion(FirstSeat);
		Plugin.Log.Information($"Interact: riding pillion on \"{best.Name}\" at {bestDistance:0.#}y");
		Plugin.Diag($"Interact: riding pillion on \"{best.Name}\" at {bestDistance:0.#}y");
		return true;
	}

	/// <summary>
	/// ⚠ PARTY ONLY, matching what the macro could express and what the right-click menu offers. Also
	/// the polite reading: climbing onto a stranger's chocobo uninvited is not a quality-of-life
	/// feature.
	/// </summary>
	private static bool InParty(Dalamud.Game.ClientState.Objects.Types.IGameObject who) {
		for (int i = 0; i < Plugin.Party.Length; i++) {
			var member = Plugin.Party[i];
			if (member is not null && member.EntityId == who.EntityId)
				return true;
		}
		return false;
	}
}
