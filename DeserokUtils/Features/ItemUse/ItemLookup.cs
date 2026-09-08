using System;
using System.Collections.Generic;

namespace DeserokUtils.Features.ItemUse;

internal static class ItemLookup {

	public const uint HqOffset = 1_000_000;

	private static Dictionary<string, uint>? usableByName;

	private static readonly object gate = new();

	public static void Warm() => Build();

	private static void Build() {
		if (usableByName is not null)
			return;

		lock (gate) {
		if (usableByName is not null)
			return;

		var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
		if (sheet is not null) {
			foreach (var row in sheet) {
				if (row.ItemAction.RowId == 0)
					continue;
				string name = row.Name.ExtractText();
				if (name.Length == 0)
					continue;

				if (!map.ContainsKey(name))
					map[name] = row.RowId;
			}
		}

		usableByName = map;
		Plugin.Log.Information($"ItemUse: item name map built, {map.Count} usable items.");
		}
	}

	public static uint? Resolve(string name) {
		Build();
		return usableByName!.TryGetValue(name.Trim(), out uint id) ? id : null;
	}

	public static string? Suggest(string name) {
		Build();
		return NameSuggest.Closest(name, usableByName!.Keys);
	}

	public static string SuggestionFor(string name) {
		string? near = Suggest(name);
		return near is null ? string.Empty : $" Did you mean \"{near}\"?";
	}

	private static readonly string[] Verbs = { "/dsuitem ", "/useitem ", "/item " };

	public readonly record struct ItemSpan(string Name, int Start, int Length, bool Quoted);

	public static ItemSpan? ItemNameIn(string line) {
		foreach (string verb in Verbs) {
			int verbAt = line.IndexOf(verb, StringComparison.OrdinalIgnoreCase);
			if (verbAt < 0 || line[..verbAt].Trim().Length > 0)
				continue;

			int at = verbAt + verb.Length;
			while (at < line.Length && line[at] == ' ')
				at++;
			if (at >= line.Length)
				return null;

			if (line[at] is '"' or '\'') {
				char quote = line[at];
				int close = line.IndexOf(quote, at + 1);
				if (close <= at + 1)
					return null;
				return new ItemSpan(line[(at + 1)..close], at, close - at + 1, true);
			}

			int brace = line.IndexOf('{', at);
			int angle = line.IndexOf('<', at);
			int cut = brace >= 0 && angle >= 0 ? Math.Min(brace, angle) : Math.Max(brace, angle);
			int end = cut >= 0 ? cut : line.Length;
			string name = line[at..end].TrimEnd();
			return name.Length > 0 ? new ItemSpan(name, at, name.Length, false) : null;
		}

		return null;
	}
}
