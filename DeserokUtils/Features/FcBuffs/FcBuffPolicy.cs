using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.ClientState.Conditions;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DeserokUtils.Features.FcBuffs;

internal sealed class FcBuffPolicy {

	private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);

	private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(10);

	private DateTime? calmSince;
	private readonly Dictionary<string, DateTime> lastAttempt = new(StringComparer.OrdinalIgnoreCase);

	private HashSet<string>? lastSeenActive;

	public bool Settled { get; private set; }

	public List<string> JustDropped { get; } = new();

	public static bool InDuty()
		=> Plugin.Condition[ConditionFlag.BoundByDuty]
			|| Plugin.Condition[ConditionFlag.BoundByDuty56]
			|| Plugin.Condition[ConditionFlag.BoundByDuty95];

	public (string Label, bool Met)[] Conditions() => new[] {
		("Not in a duty", !InDuty()),
		("On your home world", OnHomeWorld()),
		("In a sanctuary", InSafePlace()),
		("An FC buff is missing", this.AllToActivate().Count > 0),
	};

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

	public static bool OnHomeWorld() {
		var me = Plugin.Objects.LocalPlayer;
		return me is not null
			&& me.CurrentWorld.RowId != 0
			&& me.CurrentWorld.RowId == me.HomeWorld.RowId;
	}

	public static unsafe bool InSafePlace() {
		var info = TerritoryInfo.Instance();
		return info is not null && info->InSanctuary;
	}

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

			this.calmSince = null;
			this.Settled = false;
			this.lastSeenActive = null;
			return;
		}

		this.calmSince ??= DateTime.UtcNow;
		this.Settled = DateTime.UtcNow - this.calmSince.Value >= SettleTime;
		if (!this.Settled) {

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

	private HashSet<string> lastObserved = new();

	public List<string> AllToActivate() {
		var result = new List<string>();
		if (!this.Settled)
			return result;

		var active = this.lastObserved;

		int free = MaxActiveBuffs - active.Count;
		if (free <= 0)
			return result;

		foreach (string wanted in Plugin.Config.FcBuffActions) {
			if (wanted.Length == 0 || active.Contains(FcBuffReader.NormaliseName(wanted)))
				continue;

			if (this.lastAttempt.TryGetValue(wanted, out var when)
				&& DateTime.UtcNow - when < RetryCooldown)
				continue;

			result.Add(wanted);
			if (result.Count >= free)
				break;
		}

		return result;
	}

	private const int MaxActiveBuffs = 2;

	public static int MaxActive => MaxActiveBuffs;

	public void RecordAttempt(string action) => this.lastAttempt[action] = DateTime.UtcNow;
}
