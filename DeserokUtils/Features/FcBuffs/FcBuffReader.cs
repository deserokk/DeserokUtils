using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DeserokUtils.Features.FcBuffs;

/// <summary>One row of the CompanyAction sheet -- an FC buff that can be switched on.</summary>
internal readonly record struct FcAction(
	uint RowId, string Name, uint Cost, uint RankRequired, bool Purchasable, byte Order);

/// <summary>A status currently sitting on the player.</summary>
internal readonly record struct PlayerStatus(uint StatusId, string Name, float RemainingSeconds);

/// <summary>
/// Everything this feature READS. Kept apart from the feature itself because "is the buff on" and
/// "for how long" come from different sources -- the status list and the agent timers -- and they
/// disagree in ways that matter.
/// </summary>
internal static class FcBuffReader {
	/// <summary>
	/// The FC actions the game knows about, from the CompanyAction sheet.
	///
	/// ⭐ Read out of the running game rather than typed in. The names are what the buffs are matched
	/// by, and a name from memory or a wiki can be stale, localised, or simply a different thing --
	/// which is exactly how FateWatch shipped with two FATEs that never existed.
	/// </summary>
	/// <summary>
	/// ⚠⚠ CACHED, and not as a micro-optimisation. Sheet reads are per-row string extractions, and
	/// every one of these was being redone sixty times a second by the tab's draw call -- which is
	/// what tanked the framerate the moment the tab was opened. Excel sheets do not change during a
	/// session, so re-reading them per frame buys exactly nothing.
	/// </summary>
	private static List<FcAction>? knownActions;

	/// <summary>⚠ Memoised for the same reason: see <see cref="ResolveTerritories"/>.</summary>
	private static readonly Dictionary<string, List<uint>> territoryCache = new(StringComparer.OrdinalIgnoreCase);

	public static List<FcAction> KnownActions() {
		if (knownActions is not null)
			return knownActions;

		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.CompanyAction>();
		if (sheet is null)
			return new List<FcAction>();

		var list = new List<FcAction>();
		foreach (var row in sheet) {
			string name = row.Name.ExtractText();
			if (name.Length == 0)
				continue;
			list.Add(new FcAction(row.RowId, name, row.Cost, row.FCRank.RowId, row.Purchasable, row.Order));
		}

		knownActions = list;
		return list;
	}

	/// <summary>
	/// Every status on the local player, named via the Status sheet.
	///
	/// ⚠⚠ RemainingSeconds IS MEANINGLESS FOR FC BUFFS. Measured 2026-08-07: The Heat of Battle
	/// (365) and Reduced Rates (364) both read exactly 30s across three probes seventeen seconds
	/// apart, while the agent timers -- counting down correctly the whole time -- said ninety-seven
	/// minutes were left. A real 30-second countdown would have expired during the probe. It did not
	/// move, so it is not a countdown.
	///
	/// ⭐ So this source answers WHICH buffs are on and nothing else. Duration comes from
	/// <see cref="RawTimers"/>. Reading a countdown off this list would have produced a plugin that
	/// announced an imminent expiry every thirty seconds, forever.
	/// </summary>
	public static List<PlayerStatus> PlayerStatuses() {
		var list = new List<PlayerStatus>();
		var me = Plugin.Objects.LocalPlayer;
		if (me is null)
			return list;

		foreach (var s in me.StatusList) {
			if (s is null || s.StatusId == 0)
				continue;
			string name = s.GameData.ValueNullable?.Name.ExtractText() ?? string.Empty;
			list.Add(new PlayerStatus(s.StatusId, name, s.RemainingTime));
		}
		return list;
	}

	/// <summary>
	/// The three FC action timers held by the FreeCompany agent.
	///
	/// ⚠⚠ TimeRemainingAtUpdate IS NOT TIME REMAINING. The struct is literally a snapshot plus an
	/// age: the game writes the remaining values when the agent last updated, then counts
	/// TimeSinceUpdate up from there. Reading the array on its own gives a number that is correct
	/// once and then never moves again -- a value that looks live, is not, and gets more wrong the
	/// longer the FC window stays shut.
	///
	/// So the live value is (snapshot - age), which is what <see cref="LiveRemaining"/> returns.
	///
	/// ⭐ CONFIRMED 2026-08-07, and the subtraction is not academic. Across three probes: the
	/// snapshot sat unchanged at 5988 while the age climbed 158 -> 169, then the FC window was
	/// opened and the snapshot was rewritten to 5816 with the age reset to 3. Raw, that is a
	/// 172-second cliff at the exact moment the window opened. Subtracted, it runs 5830 -> 5819 ->
	/// 5813 -- continuous straight through the update, which is what says the model is right.
	///
	/// ⭐ Units are SECONDS. The age advanced 11 over 11.36 s of wall clock.
	///
	/// ⚠ THREE slots, and nothing here says which ACTION is in which slot -- the struct carries no
	/// action id. Slot 0 is not "Heat of Battle"; it is whatever is in slot 0. Identifying the buff
	/// is what the player's status list is for. Do not join these two by position.
	///
	/// ⚠ An empty slot reads 0, so the count of non-zero slots is how many actions are running --
	/// which agreed with the status list (two of each) but is a cross-check, never the identity.
	///
	/// ⚠⚠ THREE SLOTS IS NOT A LIMIT OF THREE. The game only permits TWO active company actions
	/// (deserok, 2026-08-07, who owns the FC). What the third slot is for is unknown and left that
	/// way rather than guessed at. The array length is a fact about how the client allocates, and
	/// reading a game rule out of it is the same mistake as deriving the FATE rotation length from
	/// how many entries happened to be in TrackedFates -- a container's size quietly became a fact
	/// about the world, and was wrong.
	///
	/// ⭐ Nothing here depends on either number, and that is deliberate: presence comes from the
	/// status list, so the cap can be two, three or five without this code caring.
	/// </summary>
	public static unsafe (uint Age, uint[] Snapshot)? RawTimers() {
		var agent = AgentFreeCompany.Instance();
		if (agent is null)
			return null;

		var span = agent->ActionTimeRemaining.TimeRemainingAtUpdate;
		var snapshot = new uint[span.Length];
		for (int i = 0; i < span.Length; i++)
			snapshot[i] = span[i];

		return (agent->ActionTimeRemaining.TimeSinceUpdate, snapshot);
	}

	/// <summary>
	/// Snapshot minus age, floored at zero. See the warning on <see cref="RawTimers"/> for why the
	/// subtraction is not optional.
	/// </summary>
	public static uint LiveRemaining(uint snapshot, uint age) => snapshot > age ? snapshot - age : 0;

	/// <summary>
	/// The FC's rank, which is what decides how much of the action list is even shown.
	/// Zero if the proxy has no data yet.
	/// </summary>
	public static unsafe byte FreeCompanyRank() {
		var proxy = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyFreeCompany.Instance();
		return proxy is null ? (byte)0 : proxy->Rank;
	}

	// ⚠⚠ PredictedListOrder lived here and was DELETED 2026-08-07, the same hour a screenshot
	// falsified it. It reconstructed the window's list from the CompanyAction sheet -- unique rows,
	// one per action, ordered by the sheet's Order column. The real window is an INVENTORY: the FC
	// owns thirteen action items out of a possible fifteen, and four of them are the same buff. A
	// list of unique rows can never index a list with duplicates in it.
	//
	// ⭐ Removed rather than left to be corrected later. A wrong predictor that prints a confident,
	// plausible, checkable-looking table is worse than no predictor, because the way it gets used is
	// to trust it.
	//
	// ⚠⚠ AND THE INVENTORY MOVES. Stock is bought and consumed, so the row a buff sits at is not a
	// property of the buff -- it is a property of this minute. The list went 13 -> 14 entries during
	// development. Any index MUST be read immediately before it is used and never cached, because a
	// stale index does not fail: it activates whatever is at that row now.

	/// <summary>
	/// Where the FreeCompanyAction list's text lives, measured 2026-08-07.
	///
	/// ⚠⚠ ALL THREE NUMBERS ARE OBSERVATIONS, NOT CONTRACTS. Array 58, the inactive list starting at
	/// entry 8, stride 2 -- derived from a single dump cross-referenced against six recorded clicks
	/// (rows 0-4 landed on entries 8,10,12,14,16). Nothing in the game data states any of them, and
	/// a patch can move all three.
	///
	/// ⭐ Which is why nothing acts on these alone: <see cref="ReadListEntry"/> is always used to
	/// confirm the row holds the expected NAME before that row is fired at. A wrong stride then
	/// becomes a refusal and a log line, instead of quietly consuming the wrong buff.
	/// </summary>
	private const int ListStringArray = 58;
	private const int InactiveListBase = 8;
	private const int InactiveListStride = 2;

	/// <summary>
	/// Entry 7 holds the inactive list's own count, as the "14/15" the window prints.
	///
	/// ⭐⭐ THIS IS THE END OF THE LIST, and it is the only honest one. Scanning until an empty entry
	/// over-counts, because the array keeps stale rows past the live end and a ghost row names a real
	/// buff -- indistinguishable by content, which is why the earlier "stop at the first blank"
	/// scan read 8 Reduced Rates against 7 on screen. The game states the length; it does not have
	/// to be inferred.
	///
	/// ⚠ The same shape sits in the ACTIVE section: entry 3 still named Reduced Rates II long after
	/// it expired, and what gave it away was entry 4 -- its duration -- being blank. Every part of
	/// this array remembers; only the counts say what is real.
	/// </summary>
	private const int InactiveCountEntry = 7;

	/// <summary>Reads one entry of a UI string array. Null when out of range or unreadable.</summary>
	public static unsafe string? ReadStringArray(int array, int index) {
		var stage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance();
		if (stage is null || stage->AtkArrayDataHolder is null || index < 0)
			return null;

		var holder = stage->AtkArrayDataHolder;
		if (array < 0 || array >= holder->StringArrayCount)
			return null;

		var arr = holder->StringArrays[array];
		if (arr is null)
			return null;

		var span = arr->Span;
		if (index >= Math.Min(arr->Size, span.Length))
			return null;

		return span[index].ToString();
	}

	/// <summary>The name shown at a given row of the INACTIVE action list, or null.</summary>
	public static string? ReadListEntry(int row) =>
		row < 0 ? null : ReadStringArray(ListStringArray, InactiveListBase + (row * InactiveListStride));

	/// <summary>
	/// How many rows of the inactive list are real, from the game's own "14/15" counter.
	/// Null when the window is shut or the entry does not parse.
	/// </summary>
	public static int? InactiveCount() {
		string? text = ReadStringArray(ListStringArray, InactiveCountEntry);
		if (string.IsNullOrEmpty(text))
			return null;

		int slash = text.IndexOf('/');
		string head = slash > 0 ? text[..slash] : text;
		return int.TryParse(head.Trim(), out int n) ? n : null;
	}

	/// <summary>Rows of the inactive list holding the given buff, in list order.</summary>
	public static List<(int Row, int Tier, string Text)> RowsHolding(string family) {
		var rows = new List<(int, int, string)>();
		string wanted = NormaliseName(family);

		int? count = InactiveCount();
		if (count is null) {
			// ⚠ Refuse rather than fall back to scanning until a blank. That fallback is what
			// over-counted, and a stock number that is quietly wrong feeds the "that was the last
			// one" warning -- which would then fire one activation too late, every time.
			Plugin.Diag("FcBuffs: inactive count unreadable (FC action window shut?) -- reporting no stock.");
			return rows;
		}

		for (int row = 0; row < count.Value; row++) {
			string? text = ReadListEntry(row);
			if (string.IsNullOrEmpty(text))
				continue;
			if (NormaliseName(text) == wanted)
				rows.Add((row, TierOf(text), text));
		}
		return rows;
	}

	/// <summary>
	/// The best row to spend for a family: highest tier in stock, earliest row among equals.
	///
	/// ⭐ "Highest available" rather than a configured tier, per deserok -- and it stays correct
	/// through the expansion plan, where the IIIs get loaded into the pool and simply start winning.
	/// </summary>
	public static (int Row, int Tier, string Text)? BestRowFor(string family) {
		var rows = RowsHolding(family);
		if (rows.Count == 0)
			return null;

		return rows.OrderByDescending(r => r.Tier).ThenBy(r => r.Row).First();
	}

	/// <summary>
	/// Item 0 of the open context menu, read from the addon's OWN AtkValues. What the caller checks
	/// the menu's order against before firing an index at it.
	///
	/// ⚠⚠ THE ONE DESTRUCTIVE MISTAKE AVAILABLE IN THIS FEATURE. Item 0 executes; item 1 DISCARDS,
	/// which destroys a bought action outright. Firing an index into a menu whose order is assumed
	/// is the single thing here that can lose something -- everything else fails by doing nothing.
	///
	/// ⭐ And "the obvious value is right" has been wrong at every step of this feature: eventParam
	/// was not the row, the Actions tab was 4 and not 3, the list carried ghost rows, and the filter
	/// hid two whole steps. The order is checked because the track record says to check it.
	///
	/// ⚠ Returns null when the menu text cannot be read at all, so an unreadable menu refuses
	/// rather than proceeds.
	///
	/// ⚠⚠ Layout measured 2026-08-07: value 0 is the ITEM COUNT, and the labels start at value 8.
	/// Values past base+count are STALE -- a live 2-item menu still carried "View Company Profile",
	/// "Search for Item" and "Try On" from menus opened earlier in the session.
	///
	/// ⭐⭐ THAT IS THE THIRD TIME THIS EXACT SHAPE HAS APPEARED: the inactive action list, the active
	/// action slots, and now this menu. Every one is a count field beside a buffer that never clears,
	/// and every one punishes reading the buffer without the count. It is the engine's habit, not a
	/// series of coincidences -- treat any UI array here as unbounded junk until a count says
	/// otherwise.
	///
	/// ⚠ The previous version searched the UI STRING arrays for this text and never found it,
	/// because menu labels do not live there. What it did find was array 0 -- the chat log -- where
	/// it matched the word "Action" inside this feature's own error messages.
	/// </summary>
	public static unsafe string? ContextMenuFirstItem(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* menu) {
		const int ItemBase = 8;

		if (menu is null || menu->AtkValues is null)
			return null;

		int count = (int)menu->AtkValues[0].UInt;
		if (count <= 0 || menu->AtkValuesCount < ItemBase + count)
			return null;

		return menu->AtkValues[ItemBase].String.ToString();
	}

	// ⚠ DumpMenuStrings lived here and was DELETED the moment the AtkValues dump made it pointless.
	// It searched every UI string array for "Action" to find the menu's labels, which are not in a
	// string array at all -- so its only hits were array 0, the CHAT LOG, matching this feature's own
	// error messages. A diagnostic that reports your own error text back to you is worse than none.

	/// <summary>
	/// Hunts every string array the UI holds for entries that name a company action, and reports
	/// where they sit.
	///
	/// ⭐ This is how the row index gets a MEANING. The callback carries an index and nothing else,
	/// and the list it indexes is an inventory with duplicates -- four copies of the same buff -- so
	/// the index cannot be derived from the sheet. The window's backing string array is the only
	/// thing that knows which row is which.
	///
	/// ⚠ Searched rather than looked up by a known array id. Guessing the array is the same class of
	/// mistake as guessing the addon name, and a search that finds nothing is at least honest about
	/// finding nothing.
	/// </summary>
	public static unsafe List<(int Array, int Index, string Text)> FindActionStrings() {
		var hits = new List<(int, int, string)>();
		var stage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance();
		if (stage is null || stage->AtkArrayDataHolder is null)
			return hits;

		var wanted = KnownActions().Select(a => NormaliseName(a.Name)).ToHashSet();
		var holder = stage->AtkArrayDataHolder;

		for (int a = 0; a < holder->StringArrayCount; a++) {
			var arr = holder->StringArrays[a];
			if (arr is null)
				continue;

			var span = arr->Span;
			int size = Math.Min(arr->Size, span.Length);
			for (int i = 0; i < size; i++) {
				string text = span[i].ToString() ?? string.Empty;
				if (text.Length > 0 && wanted.Contains(NormaliseName(text)))
					hits.Add((a, i, text));
			}
		}
		return hits;
	}

	/// <summary>
	/// Every addon the game currently has loaded, by name.
	///
	/// ⭐ The discovery step for "what is the FC window actually called". Guessing at addon names is
	/// the same mistake as guessing at FATE names, and it fails the same silent way: a listener
	/// registered against a name that does not exist never fires and never complains.
	/// </summary>
	public static unsafe List<string> LoadedAddons() {
		var names = new List<string>();
		var stage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance();
		if (stage is null || stage->RaptureAtkUnitManager is null)
			return names;

		ref var units = ref stage->RaptureAtkUnitManager->AllLoadedUnitsList;
		var entries = units.Entries;
		int count = Math.Min((int)units.Count, entries.Length);

		for (int i = 0; i < count; i++) {
			var unit = entries[i].Value;
			if (unit is null)
				continue;
			string name = unit->NameString;
			if (!string.IsNullOrEmpty(name))
				names.Add(name);
		}
		return names;
	}

	/// <summary>
	/// Where the player is, as the game names it. Used to seed and to extend the "safe to open a
	/// menu here" list without anybody typing a territory id.
	/// </summary>
	private static uint lastTerritory = uint.MaxValue;
	private static string lastPlaceName = string.Empty;

	/// <summary>⚠ Cached on the territory id -- this is called from the draw loop, once per frame.</summary>
	public static (uint Territory, string PlaceName) CurrentPlace() {
		uint territory = Plugin.ClientState.TerritoryType;
		if (territory == lastTerritory)
			return (territory, lastPlaceName);

		var row = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRowOrDefault(territory);
		lastPlaceName = row?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
		lastTerritory = territory;
		return (territory, lastPlaceName);
	}

	/// <summary>
	/// Resolve a place name to every territory id carrying it.
	///
	/// ⭐ This is why the config stores NAMES, not numbers. A hardcoded id list is a page of figures
	/// nobody can check by eye and that goes stale on the next expansion -- and the one thing known
	/// about the next expansion is that it is coming. The sheet is the authority for the number; the
	/// name is the part a human can actually verify.
	///
	/// ⚠ Returns every match, because a city is routinely several territories -- Ul'dah alone is
	/// Steps of Nald and Steps of Thal, and being in "the wrong half" of a city is not a state
	/// anybody thinks in.
	/// </summary>
	/// <summary>
	/// ⚠⚠ MEMOISED, and this one was the actual framerate bug. A miss walks the whole TerritoryType
	/// sheet extracting a place name per row, and the tab called it once per safe place -- twenty-two
	/// of them -- on every single frame. The answer is fixed game data, so it is computed once per
	/// name and kept.
	/// </summary>
	public static List<uint> ResolveTerritories(string placeName) {
		if (string.IsNullOrWhiteSpace(placeName))
			return new List<uint>();

		if (territoryCache.TryGetValue(placeName, out var cached))
			return cached;

		var found = new List<uint>();
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
		if (sheet is not null) {
			foreach (var row in sheet) {
				string name = row.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
				if (name.Length > 0 && string.Equals(name, placeName, StringComparison.OrdinalIgnoreCase))
					found.Add(row.RowId);
			}
		}

		territoryCache[placeName] = found;
		return found;
	}

	/// <summary>
	/// Strips a trailing roman numeral, so an action and the status it applies can be compared.
	///
	/// ⚠⚠ THE TIERS DO NOT SHARE A NAME. The FC owns "The Heat of Battle II"; the status it puts on
	/// the player is "The Heat of Battle", with no numeral (measured 2026-08-07 -- the window said
	/// II while the status list said neither II nor III). Exact-match name joining therefore fails
	/// for every buff actually in use.
	///
	/// ⚠ It only looked like it worked because the CompanyAction sheet ALSO contains the un-numbered
	/// rank-1 rows, so the status matched one of those and the probe printed a cheerful "matches a
	/// company action". The join was landing on a different action than the one that was running.
	///
	/// ⭐ Collapsing the tiers is not a loss here: the question being asked is "is this buff up",
	/// and for that, which tier is running does not matter.
	/// </summary>
	internal static string NormaliseName(string name) {
		string s = name.Trim();
		foreach (string suffix in new[] { " III", " II", " IV", " I" }) {
			if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				return s[..^suffix.Length].Trim().ToLowerInvariant();
		}
		return s.ToLowerInvariant();
	}

	/// <summary>
	/// The tier a company action name carries. No numeral is tier 1.
	///
	/// ⭐ Tiers are what the config does NOT store. deserok has never once chosen to use a I over a
	/// II, and the IIIs he is saving get loaded on demand rather than held in the pool -- so "which
	/// tier" is not a preference, it is just "the best one currently in stock". Asking would be
	/// asking a question with one answer.
	/// </summary>
	internal static int TierOf(string name) {
		string s = name.Trim();
		if (s.EndsWith(" IV", StringComparison.OrdinalIgnoreCase)) return 4;
		if (s.EndsWith(" III", StringComparison.OrdinalIgnoreCase)) return 3;
		if (s.EndsWith(" II", StringComparison.OrdinalIgnoreCase)) return 2;
		return 1;
	}

	/// <summary>
	/// Which buff families are currently up, normalised.
	///
	/// ⚠⚠ ONE ENTRY PER STATUS, which the old shape got wrong. Joining statuses to actions by
	/// normalised name matches every tier -- so a single "The Heat of Battle" on the player produced
	/// three results (I, II and III) and the tab cheerfully reported three buffs running. The player
	/// has one status; the sheet has three rows that could have caused it. Those are different
	/// counts and only the first one is a fact about the world.
	/// </summary>
	/// <summary>
	/// Status id -> buff family, built once from the Status sheet.
	///
	/// ⭐ So the hot path never touches a sheet or builds a string. The old version extracted a name
	/// for every status on the player, normalised it, and compared it against a set of action names
	/// -- thirty sheet lookups and thirty allocations, twice per poll, to answer a question that is
	/// really just "is this id one of ours". Ids are stable; resolve them once.
	/// </summary>
	private static Dictionary<uint, string>? statusFamilies;

	private static Dictionary<uint, string> StatusFamilies() {
		if (statusFamilies is not null)
			return statusFamilies;

		var families = KnownActions().Select(a => NormaliseName(a.Name)).ToHashSet();
		var map = new Dictionary<uint, string>();

		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>();
		if (sheet is not null) {
			// ⚠ One pass over the whole Status sheet, at startup, once. Expensive to do repeatedly
			// and free to do never again.
			foreach (var row in sheet) {
				string name = row.Name.ExtractText();
				if (name.Length == 0)
					continue;
				string family = NormaliseName(name);
				if (families.Contains(family))
					map[row.RowId] = family;
			}
		}

		statusFamilies = map;
		Plugin.Log.Information($"FcBuffs: resolved {map.Count} status id(s) to company-action families");
		return map;
	}

	/// <summary>Which buff families are up. Id lookups only -- no sheet reads, no allocations.</summary>
	public static HashSet<string> ActiveFamilies() {
		var result = new HashSet<string>();
		var me = Plugin.Objects.LocalPlayer;
		if (me is null)
			return result;

		var map = StatusFamilies();
		foreach (var s in me.StatusList) {
			if (s is not null && s.StatusId != 0 && map.TryGetValue(s.StatusId, out string? family))
				result.Add(family);
		}
		return result;
	}

	/// <summary>The FC-buff statuses actually on the player -- one per status, for display.</summary>
	public static List<PlayerStatus> ActiveStatuses() {
		var known = KnownActions().Select(a => NormaliseName(a.Name)).ToHashSet();
		return PlayerStatuses().Where(s => known.Contains(NormaliseName(s.Name))).ToList();
	}

	/// <summary>
	/// Statuses on the player that correspond to a known FC action.
	///
	/// ⚠ Name matching, not id matching, and that is a compromise rather than a design. Nothing in
	/// the game data links a CompanyAction row to the Status row it applies, so the join is on the
	/// text -- normalised, per <see cref="NormaliseName"/>. The probe prints both sides so a
	/// mismatch shows up as a mismatch rather than as "the buff is off".
	///
	/// ⚠ Because tiers collapse, several action rows can match one status. Callers wanting "is this
	/// up" are fine; anything wanting "which exact row is running" is not answerable this way.
	/// </summary>
	public static List<(FcAction Action, PlayerStatus Status)> ActiveFcBuffs() {
		var actions = KnownActions();
		var statuses = PlayerStatuses();

		return (from st in statuses
				join ac in actions
					on NormaliseName(st.Name) equals NormaliseName(ac.Name)
				select (ac, st)).ToList();
	}
}
