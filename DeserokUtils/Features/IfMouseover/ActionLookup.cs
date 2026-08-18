using System;
using System.Collections.Generic;

namespace DeserokUtils.Features.IfMouseover;

/// <summary>
/// Action name -&gt; id, memoised.
///
/// ⚠ CastWatch has its own copy of this lookup and walks the whole Action sheet on every call. That
/// is fine there -- /watch runs once when a macro arms -- and it is NOT fine here, because /ifmo
/// runs on every press of every macro that uses it. Hence a dictionary built once.
///
/// ⚠ Deliberately NOT extracted out of CastWatch tonight. Unifying them means editing shipped,
/// working code for tidiness at 2am, and the two have genuinely different needs. Worth doing when
/// the Macros tab is properly assembled; noted here so it is a decision rather than an oversight.
/// </summary>
internal static class ActionLookup {
	private static Dictionary<string, uint>? byName;

	/// <summary>
	/// ⚠ Player actions only, same restriction CastWatch uses, so a name cannot silently bind to some
	/// internal ability that shares it.
	///
	/// ⚠ Built on FIRST USE, not at load. The sheet walk is ~thousands of string extractions and
	/// nobody who never types /ifmo should pay for it -- the per-frame audit in DeserokUtils.md is
	/// what this is avoiding, one class earlier than usual.
	/// </summary>
	private static Dictionary<string, uint> Map() {
		if (byName is not null)
			return byName;

		var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
		if (sheet is not null) {
			foreach (var row in sheet) {
				if (!row.IsPlayerAction)
					continue;
				string name = row.Name.ExtractText();
				// ⚠ First wins. Duplicate names across rows exist and the earlier row is the one the
				// hotbar shows; last-wins would silently prefer some later variant.
				if (name.Length > 0 && !map.ContainsKey(name))
					map[name] = row.RowId;
			}
		}

		Plugin.Log.Information($"IfMouseover: action name map built, {map.Count} entries.");
		return byName = map;
	}

	public static uint? Resolve(string name) =>
		Map().TryGetValue(name.Trim(), out uint id) ? id : null;

	/// <summary>
	/// Pull the action name out of a macro line like <c>/ac "Nascent Flash" {mo}</c>.
	///
	/// ⚠ Returns null rather than guessing when the line is not an action command. The caller then
	/// degrades to a presence check and SAYS so -- a wrong action id would validate against the wrong
	/// action's rules, which fails in the direction of "it silently targeted the wrong way".
	/// </summary>
	public static string? ActionNameIn(string line) {
		string trimmed = line.TrimStart();
		foreach (string verb in new[] { "/action ", "/ac " }) {
			if (!trimmed.StartsWith(verb, StringComparison.OrdinalIgnoreCase))
				continue;

			string rest = trimmed[verb.Length..].Trim();
			if (rest.Length == 0)
				return null;

			// FFXIV macros need quotes around multi-word action names, so both forms must work --
			// the same accommodation /watch makes.
			if (rest[0] is '"' or '\'') {
				char quote = rest[0];
				int close = rest.IndexOf(quote, 1);
				return close > 1 ? rest[1..close] : null;
			}

			// Unquoted: everything up to the placeholder or the end. "/ac Clemency {mo}" -> "Clemency"
			int brace = rest.IndexOf('{');
			int angle = rest.IndexOf('<');
			int cut = brace >= 0 && angle >= 0 ? Math.Min(brace, angle) : Math.Max(brace, angle);
			string name = (cut >= 0 ? rest[..cut] : rest).Trim();
			return name.Length > 0 ? name : null;
		}

		return null;
	}
}
