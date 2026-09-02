using System;
using System.Collections.Generic;

namespace DeserokUtils.Features.ItemUse;

/// <summary>
/// Item name -&gt; id, memoised, plus the parser that pulls a name out of an item command line.
///
/// ⚠ Restricted to rows with an <c>ItemAction</c>, the same restriction CastWatch's copy uses, or
/// every piece of gear and every crafting material becomes a candidate name -- and "Bronze Ingot"
/// resolving to something usable is a worse answer than not resolving at all.
///
/// ⚠ Built on FIRST USE and kept as a dictionary, not walked per press. Same reason as
/// <see cref="IfMouseover.ActionLookup"/>: CastWatch can afford a sheet walk because /watch runs
/// once when a macro arms, and this runs on every press of every macro that uses it -- and now also
/// from a UI hook, where a sheet walk per icon resolve would be felt as a stutter.
/// </summary>
internal static class ItemLookup {
	/// <summary>⚠ HQ items are the same item at +1,000,000, both to <c>UseAction</c> and to the
	/// CastWatch hook that watches for it. One constant, named, in both directions.</summary>
	public const uint HqOffset = 1_000_000;

	private static Dictionary<string, uint>? usableByName;

	/// <summary>⚠ Locked, unlike ActionLookup's, because this one has TWO callers on two threads: a
	/// macro press on the framework thread, and the warm-up below. Two threads walking the sheet at
	/// once is wasted work; two threads assigning the map is a torn read.</summary>
	private static readonly object gate = new();

	/// <summary>
	/// Build the map off the main thread at load.
	///
	/// ⚠⚠ Because the icon hook can be the FIRST caller, and it runs from the UI. Without this, the
	/// first hotbar refresh after login pays for a walk of the whole Item sheet mid-frame -- a stutter
	/// at exactly the moment everything else is also loading, on the machine least able to absorb it.
	/// Dalamud's sheets are safe to read from any thread; the lock covers the rest.
	/// </summary>
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
				// First wins, matching how CastWatch's walk behaves, so a watch and a use of the same
				// typed name can never disagree about which row they meant.
				if (!map.ContainsKey(name))
					map[name] = row.RowId;
			}
		}

		usableByName = map;
		Plugin.Log.Information($"ItemUse: item name map built, {map.Count} usable items.");
		}
	}

	/// <summary>The NQ row id for a usable item name, or null when nothing matched.</summary>
	public static uint? Resolve(string name) {
		Build();
		return usableByName!.TryGetValue(name.Trim(), out uint id) ? id : null;
	}

	/// <summary>The nearest real item name to one that matched nothing, for the error message.
	/// See <see cref="NameSuggest"/> for why this suggests rather than silently substitutes.</summary>
	public static string? Suggest(string name) {
		Build();
		return NameSuggest.Closest(name, usableByName!.Keys);
	}

	/// <summary>" + " a phrase to append to an error, or nothing when there is no near miss.</summary>
	public static string SuggestionFor(string name) {
		string? near = Suggest(name);
		return near is null ? string.Empty : $" Did you mean \"{near}\"?";
	}

	/// <summary>
	/// The verbs that take an item name plus an optional placeholder.
	///
	/// ⚠⚠ NONE OF THESE EXIST IN THE GAME. Measured from the TextCommand sheet on 2026-09-02: of 541
	/// text commands, not one uses an item -- there is no <c>/item</c>, no <c>/use</c>, nothing. So
	/// unlike <see cref="IfMouseover.ActionLookup.ActionNameIn"/>, whose job is to rewrite a line the
	/// GAME will then run, a line matching one of these is never handed back to the game at all. We
	/// perform the use ourselves.
	///
	/// ⭐ Which is also why quoting is optional here and the forgotten-quotes rewrite has no
	/// counterpart: nothing downstream re-parses the name, so <c>/item Phoenix Down &lt;mo&gt;</c> and
	/// <c>/item "Phoenix Down" &lt;mo&gt;</c> are the same line to us.
	///
	/// ⚠ The trailing space is load-bearing, exactly as it is in ActionLookup: without it
	/// <c>/item </c> would match inside <c>/itemsort</c>, which IS a real game command.
	///
	/// ⭐ <c>/item</c> and <c>/useitem</c> are still READ here even though the plugin no longer
	/// registers either -- the command is <c>/dsuitem</c>. That is not an inconsistency: a line inside
	/// <c>/ifmo</c> is parsed by us and never dispatched, so nothing can collide with it, and being
	/// forgiving about the verb somebody's fingers type costs nothing. What must not happen is
	/// CLAIMING a name in the shared command namespace, which is a different thing entirely.
	/// </summary>
	private static readonly string[] Verbs = { "/dsuitem ", "/useitem ", "/item " };

	/// <summary>Where the item name sits in the line, and how it was quoted.</summary>
	public readonly record struct ItemSpan(string Name, int Start, int Length, bool Quoted);

	/// <summary>
	/// Pull the item name out of a line like <c>/item "Phoenix Down" {mo|t}</c>.
	///
	/// ⚠ Returns null rather than guessing when the line is not an item command, so /ifmo can tell
	/// "this is an item line" from "this is something else" without a second parse disagreeing.
	/// </summary>
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

			// Unquoted: everything up to the placeholder or the end.
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
