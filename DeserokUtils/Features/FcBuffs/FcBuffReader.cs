using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DeserokUtils.Features.FcBuffs;

internal readonly record struct FcAction(
	uint RowId, string Name, uint Cost, uint RankRequired, bool Purchasable, byte Order);

internal readonly record struct PlayerStatus(uint StatusId, string Name, float RemainingSeconds);

internal static class FcBuffReader {

	private static List<FcAction>? knownActions;

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

	public static uint LiveRemaining(uint snapshot, uint age) => snapshot > age ? snapshot - age : 0;

	public static unsafe byte FreeCompanyRank() {
		var proxy = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyFreeCompany.Instance();
		return proxy is null ? (byte)0 : proxy->Rank;
	}

	private const int ListStringArray = 58;
	private const int InactiveListBase = 8;
	private const int InactiveListStride = 2;

	private const int InactiveCountEntry = 7;

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

	public static string? ReadListEntry(int row) =>
		row < 0 ? null : ReadStringArray(ListStringArray, InactiveListBase + (row * InactiveListStride));

	public static int? InactiveCount() {
		string? text = ReadStringArray(ListStringArray, InactiveCountEntry);
		if (string.IsNullOrEmpty(text))
			return null;

		int slash = text.IndexOf('/');
		string head = slash > 0 ? text[..slash] : text;
		return int.TryParse(head.Trim(), out int n) ? n : null;
	}

	public static List<(int Row, int Tier, string Text)> RowsHolding(string family) {
		var rows = new List<(int, int, string)>();
		string wanted = NormaliseName(family);

		int? count = InactiveCount();
		if (count is null) {

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

	public static (int Row, int Tier, string Text)? BestRowFor(string family) {
		var rows = RowsHolding(family);
		if (rows.Count == 0)
			return null;

		return rows.OrderByDescending(r => r.Tier).ThenBy(r => r.Row).First();
	}

	public static unsafe string? ContextMenuFirstItem(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* menu) {
		const int ItemBase = 8;

		if (menu is null || menu->AtkValues is null)
			return null;

		int count = (int)menu->AtkValues[0].UInt;
		if (count <= 0 || menu->AtkValuesCount < ItemBase + count)
			return null;

		return menu->AtkValues[ItemBase].String.ToString();
	}

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

	private static uint lastTerritory = uint.MaxValue;
	private static string lastPlaceName = string.Empty;

	public static (uint Territory, string PlaceName) CurrentPlace() {
		uint territory = Plugin.ClientState.TerritoryType;
		if (territory == lastTerritory)
			return (territory, lastPlaceName);

		var row = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRowOrDefault(territory);
		lastPlaceName = row?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
		lastTerritory = territory;
		return (territory, lastPlaceName);
	}

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

	internal static string NormaliseName(string name) {
		string s = name.Trim();
		foreach (string suffix in new[] { " III", " II", " IV", " I" }) {
			if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				return s[..^suffix.Length].Trim().ToLowerInvariant();
		}
		return s.ToLowerInvariant();
	}

	internal static int TierOf(string name) {
		string s = name.Trim();
		if (s.EndsWith(" IV", StringComparison.OrdinalIgnoreCase)) return 4;
		if (s.EndsWith(" III", StringComparison.OrdinalIgnoreCase)) return 3;
		if (s.EndsWith(" II", StringComparison.OrdinalIgnoreCase)) return 2;
		return 1;
	}

	private static Dictionary<uint, string>? statusFamilies;

	private static Dictionary<uint, string> StatusFamilies() {
		if (statusFamilies is not null)
			return statusFamilies;

		var families = KnownActions().Select(a => NormaliseName(a.Name)).ToHashSet();
		var map = new Dictionary<uint, string>();

		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>();
		if (sheet is not null) {

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

	public static List<PlayerStatus> ActiveStatuses() {
		var known = KnownActions().Select(a => NormaliseName(a.Name)).ToHashSet();
		return PlayerStatuses().Where(s => known.Contains(NormaliseName(s.Name))).ToList();
	}

	public static List<(FcAction Action, PlayerStatus Status)> ActiveFcBuffs() {
		var actions = KnownActions();
		var statuses = PlayerStatuses();

		return (from st in statuses
				join ac in actions
					on NormaliseName(st.Name) equals NormaliseName(ac.Name)
				select (ac, st)).ToList();
	}
}
