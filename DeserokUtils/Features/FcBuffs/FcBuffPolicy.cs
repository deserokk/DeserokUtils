using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.ClientState.Conditions;

namespace DeserokUtils.Features.FcBuffs;

/// <summary>
/// Decides WHETHER and WHAT to activate. The activator decides how.
///
/// ⚠⚠ Everything here keys off PRESENCE, never duration. The status list's RemainingTime is a fixed
/// 30 for FC buffs and the agent's timers need an age subtracted -- so a design that asked "how long
/// is left" would have had two ways to be wrong and no way to notice. "Is it on" has one answer and
/// the game states it directly.
/// </summary>
internal sealed class FcBuffPolicy {
	/// <summary>
	/// How long everything must stay quiet before the status list is believed.
	///
	/// ⚠⚠ NOT politeness -- correctness. The status list is empty or partial right after a login and
	/// across every loading screen, so a naive comparison sees every zone change as "the buff just
	/// dropped": a false alert AND a spurious activation, at the exact moments they are least
	/// welcome. This is the same shape as FateWatch's anchor surviving a zone change, and the fix is
	/// the same one: do not trust a reading taken where it cannot be true.
	/// </summary>
	private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);

	/// <summary>⚠ One attempt per buff per this long. A retry loop is what turns one press a day into traffic.</summary>
	private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(10);

	private DateTime? calmSince;
	private readonly Dictionary<string, DateTime> lastAttempt = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// What was up last time the world was trustworthy. Null until the first settled reading, which
	/// is what stops the very first tick after a login from reading as a drop.
	/// </summary>
	private HashSet<string>? lastSeenActive;

	/// <summary>True when the game is in a state where opening a window is reasonable.</summary>
	public bool Settled { get; private set; }

	/// <summary>Buffs that dropped since the last settled reading. Consumed by the caller.</summary>
	public List<string> JustDropped { get; } = new();

	private static readonly ConditionFlag[] Busy = {
		ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
		ConditionFlag.InCombat, ConditionFlag.Casting, ConditionFlag.Casting87,
		ConditionFlag.Occupied, ConditionFlag.Occupied30, ConditionFlag.Occupied33,
		ConditionFlag.Occupied38, ConditionFlag.Occupied39,
		ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
		ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.OccupiedSummoningBell,
		ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95,
		ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78,
		ConditionFlag.Crafting, ConditionFlag.ExecutingCraftingAction,
		ConditionFlag.Gathering, ConditionFlag.ExecutingGatheringAction,
		ConditionFlag.TradeOpen, ConditionFlag.CreatingCharacter, ConditionFlag.Unconscious,
	};

	/// <summary>
	/// Whether we are on the home world.
	///
	/// ⚠⚠ FC BUFFS DO NOT APPLY WHILE VISITING ANOTHER WORLD -- the statuses simply go away. To a
	/// presence-only design that is indistinguishable from "they ran out", so the plugin cheerfully
	/// tried to refresh them, over and over, for the whole trip.
	///
	/// ⭐ It costs nothing, checked with deserok: the FC window cannot even be OPENED while away, so
	/// every attempt died at the Opening step and no stock was ever spent. What it produced was a
	/// timeout error in chat every ten minutes -- a plugin loudly reporting a broken step, for a
	/// situation that is completely normal and about which nothing was wrong.
	///
	/// ⚠ Which is its own kind of bad: an error that fires when nothing is broken teaches you to
	/// ignore the errors that mean something.
	///
	/// ⭐ Grouped with the loading-screen check rather than treated as its own refusal, on purpose:
	/// being away from home is not a place where the status list means what it usually means. So the
	/// whole picture is dropped, exactly like a zone transition -- which also stops ARRIVING on
	/// another world from reading as "the buff just dropped" and firing a toast at you.
	/// </summary>
	public static bool OnHomeWorld() {
		var me = Plugin.Objects.LocalPlayer;
		return me is not null
			&& me.CurrentWorld.RowId != 0
			&& me.CurrentWorld.RowId == me.HomeWorld.RowId;
	}

	/// <summary>Whether this place is on the safe list, by name rather than by number.</summary>
	public static bool InSafePlace() {
		var (_, place) = FcBuffReader.CurrentPlace();
		return place.Length > 0
			&& Plugin.Config.FcBuffSafePlaces.Contains(place, StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Re-reads the world. Call once a tick; it maintains the settle clock and the drop transitions.
	/// </summary>
	public void Observe() {
		this.JustDropped.Clear();

		bool quiet = Plugin.ClientState.IsLoggedIn
			&& Plugin.Objects.LocalPlayer is not null
			&& Plugin.ClientState.TerritoryType != 0
			&& OnHomeWorld()
			&& !Plugin.Condition.Any(Busy);

		if (!quiet && this.Settled)
			Plugin.Diag("FcBuffs: no longer settled -- "
				+ (!Plugin.ClientState.IsLoggedIn ? "not logged in"
					: Plugin.Objects.LocalPlayer is null ? "no local player"
					: Plugin.ClientState.TerritoryType == 0 ? "between areas"
					: !OnHomeWorld() ? "away from the home world (FC buffs do not apply)"
					: "a busy condition flag is set"));

		if (!quiet) {
			// ⚠ Drop the whole picture, do not merely pause. A reading taken before a loading screen
			// says nothing about the world after it, and keeping it is how a stale fact survives into
			// a place it was never true -- the Ul'dah-anchor bug, again.
			this.calmSince = null;
			this.Settled = false;
			this.lastSeenActive = null;
			return;
		}

		this.calmSince ??= DateTime.UtcNow;
		this.Settled = DateTime.UtcNow - this.calmSince.Value >= SettleTime;
		if (!this.Settled) {
			// ⭐ Says WHY it is waiting. "Nothing happened" and "waiting on the settle clock" look
			// identical from outside, and telling them apart by guesswork cost a round trip once.
			Plugin.Diag($"FcBuffs: settling, {(DateTime.UtcNow - this.calmSince.Value).TotalSeconds:0.#}s of {SettleTime.TotalSeconds:0}s");
			return;
		}

		var active = FcBuffReader.ActiveFamilies();

		if (this.lastSeenActive is not null) {
			foreach (string gone in this.lastSeenActive.Where(n => !active.Contains(n)))
				this.JustDropped.Add(gone);
		}

		this.lastSeenActive = active;
		this.lastObserved = active;
	}

	/// <summary>
	/// The active set from the most recent <see cref="Observe"/>.
	///
	/// ⚠ Reused rather than recomputed. Observe and AllToActivate ran back to back and each scanned
	/// the status list independently to build the identical set -- the same duplicated-work mistake
	/// as calling ActiveFcBuffs inside a LINQ predicate, one level up.
	/// </summary>
	private HashSet<string> lastObserved = new();

	/// <summary>
	/// Every wanted buff that is missing and has not been tried recently. Empty when there is
	/// nothing to do.
	/// </summary>
	public List<string> AllToActivate() {
		var result = new List<string>();
		if (!this.Settled)
			return result;

		var active = this.lastObserved;

		foreach (string wanted in Plugin.Config.FcBuffActions) {
			if (active.Contains(FcBuffReader.NormaliseName(wanted)))
				continue;

			if (this.lastAttempt.TryGetValue(wanted, out var when)
				&& DateTime.UtcNow - when < RetryCooldown)
				continue;

			result.Add(wanted);
		}
		return result;
	}

	public void RecordAttempt(string action) => this.lastAttempt[action] = DateTime.UtcNow;
}
