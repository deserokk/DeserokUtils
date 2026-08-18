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

	/// <summary>Where the action name sits in the line, and whether the user quoted it.</summary>
	public readonly record struct ActionSpan(string Name, int Start, int Length, bool Quoted);

	/// <summary>
	/// Pull the action name out of a macro line like <c>/ac "Nascent Flash" {mo}</c>.
	///
	/// ⚠ Returns null rather than guessing when the line is not an action command. The caller then
	/// degrades to a presence check and SAYS so -- a wrong action id would validate against the wrong
	/// action's rules, which fails in the direction of "it silently targeted the wrong way".
	///
	/// ⚠⚠ THE SPAN IS RETURNED BECAUSE THIS PARSER IS MORE FORGIVING THAN THE GAME. It happily reads
	/// an unquoted multi-word name -- `/ac Heart of Corundum {mo}` -- and resolves it, but `/ac`
	/// itself REQUIRES quotes there. So without the caller re-quoting, everything would work
	/// perfectly except the last step: a confident, correct decision in the log, followed by nothing
	/// happening in the game. That is a worse failure than not parsing it at all, because the
	/// diagnostics would look healthy.
	/// </summary>
	public static ActionSpan? ActionNameIn(string line) {
		foreach (string verb in new[] { "/action ", "/ac " }) {
			int verbAt = line.IndexOf(verb, StringComparison.OrdinalIgnoreCase);
			if (verbAt < 0 || line[..verbAt].Trim().Length > 0)
				continue;

			int at = verbAt + verb.Length;
			while (at < line.Length && line[at] == ' ')
				at++;
			if (at >= line.Length)
				return null;

			// FFXIV macros need quotes around multi-word action names, so both forms must work --
			// the same accommodation /watch makes.
			if (line[at] is '"' or '\'') {
				char quote = line[at];
				int close = line.IndexOf(quote, at + 1);
				if (close <= at + 1)
					return null;
				return new ActionSpan(line[(at + 1)..close], at, close - at + 1, true);
			}

			// Unquoted: everything up to the placeholder or the end. "/ac Clemency {mo}" -> "Clemency"
			int brace = line.IndexOf('{', at);
			int angle = line.IndexOf('<', at);
			int cut = brace >= 0 && angle >= 0 ? Math.Min(brace, angle) : Math.Max(brace, angle);
			int end = cut >= 0 ? cut : line.Length;
			string name = line[at..end].TrimEnd();
			return name.Length > 0 ? new ActionSpan(name, at, name.Length, false) : null;
		}

		return null;
	}
}
