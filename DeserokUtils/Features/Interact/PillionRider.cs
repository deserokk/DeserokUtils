using System;
using System.Numerics;

using Dalamud.Game.Text;

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace DeserokUtils.Features.Interact;

internal static class PillionRider {

	private const float Reach = 15f;

	private static int pendingRefusals;

	private static DateTime refusalsUntil = DateTime.MinValue;

	private static void Expect(int lines) {
		pendingRefusals = lines;
		refusalsUntil = DateTime.UtcNow.AddSeconds(2);
	}

	public static bool SwallowRefusal(XivChatType kind) {
		if (pendingRefusals <= 0 || DateTime.UtcNow > refusalsUntil)
			return false;

		if (kind is not (XivChatType.ErrorMessage or XivChatType.SystemError))
			return false;

		pendingRefusals--;
		return true;
	}

	public static unsafe bool TryRide() {
		var me = Plugin.Objects.LocalPlayer;
		if (me is null)
			return false;

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

		int slots = Math.Clamp(Plugin.Party.Length, 2, 8);

		Expect(slots - 1);

		for (int slot = 2; slot <= slots; slot++)
			GameCommands.Queue($"/ridepillion <{slot}>");

		Plugin.Log.Information(
			$"Interact: /ridepillion <2..{slots}> -- \"{best.Name}\" is mounted at {bestDistance:0.#}y");
		Plugin.Diag(
			$"Interact: /ridepillion <2..{slots}> -- \"{best.Name}\" is mounted at {bestDistance:0.#}y");
		return true;
	}

	private static bool InParty(Dalamud.Game.ClientState.Objects.Types.IGameObject who) {
		for (int i = 0; i < Plugin.Party.Length; i++) {
			var member = Plugin.Party[i];
			if (member is not null && member.EntityId == who.EntityId)
				return true;
		}

		return false;
	}
}
