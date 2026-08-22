using System;
using System.Collections.Generic;

namespace DeserokUtils.Features.IfMouseover;

/// <summary>
/// Action name -&gt; id, memoised, kept as two maps because <b>one name can be two actions</b>.
///
/// ⚠ CastWatch has its own copy of this lookup and walks the whole Action sheet on every call. That
/// is fine there -- /watch runs once when a macro arms -- and it is NOT fine here, because /ifmo
/// runs on every press of every macro that uses it. Hence dictionaries built once.
///
/// ⚠ Deliberately NOT extracted out of CastWatch. Unifying them means editing shipped, working code
/// for tidiness, and the two have genuinely different needs. ⚠⚠ Note that CastWatch therefore does
/// NOT have the PvP handling below -- `/watch` on a colliding name will arm the non-PvP row. Left as
/// a known gap rather than fixed blind, because /watch's hook compares whatever id the game actually
/// sent, so the failure mode there is different and needs its own measurement.
///
/// ## ⚠⚠ 103 action names exist as BOTH a PvP and a non-PvP action
///
/// Measured from the Action sheet, not assumed. And they are not variants of each other -- Guardian
/// is the clearest case:
///
/// <code>
/// row 29066  PvP   range 20  targets a party member  30s   "Rush to a target party member's side"
/// row 36920  PvE   range  0  SELF ONLY              120s   "Reduces damage taken by 40%"
/// </code>
///
/// ⚠ Picking the wrong one does not fail loudly. It validates the mouseover against the wrong
/// action's rules and then reports a confident, wrong answer -- the same failure shape as the
/// forgotten quotes, where the diagnostics look healthy and nothing happens.
///
/// ⚠⚠ First-wins across the whole sheet is NOT a safe tiebreak here. For 102 of the 103 the non-PvP
/// row has the lower id, so first-wins silently means "always the PvE one" -- and Guardian is the
/// single exception, which is exactly the action that exposed the gap. Getting the right answer by
/// accident on the one case somebody tested is worse than getting it wrong everywhere.
/// </summary>
internal static class ActionLookup {
	private static Dictionary<string, uint>? pveByName;
	private static Dictionary<string, uint>? pvpByName;

	/// <summary>
	/// ⚠ Player actions only, same restriction CastWatch uses, so a name cannot silently bind to some
	/// internal ability that shares it.
	///
	/// ⚠ Built on FIRST USE, not at load. The sheet walk is ~thousands of string extractions and
	/// nobody who never types /ifmo should pay for it -- the per-frame audit in DeserokUtils.md is
	/// what this is avoiding, one class earlier than usual.
	/// </summary>
	private static void Build() {
		if (pveByName is not null)
			return;

		var pve = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
		var pvp = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
		if (sheet is not null) {
			foreach (var row in sheet) {
				if (!row.IsPlayerAction)
					continue;
				string name = row.Name.ExtractText();
				if (name.Length == 0)
					continue;

				// ⚠ First wins WITHIN each map. Duplicate names exist inside a single map too --
				// Onslaught has two non-PvP rows at different ranges -- and the earlier row is the one
				// the hotbar shows; last-wins would silently prefer some later variant.
				var target = row.IsPvP ? pvp : pve;
				if (!target.ContainsKey(name))
					target[name] = row.RowId;
			}
		}

		pveByName = pve;
		pvpByName = pvp;
		Plugin.Log.Information($"IfMouseover: action name maps built, {pve.Count} non-PvP / {pvp.Count} PvP.");
	}

	/// <summary>What a name resolved to, and whether the choice was contested.</summary>
	/// <param name="Ambiguous">
	/// The name exists in both maps, so the PvP decision actually mattered. Traced when true, because
	/// this is the case where a silent wrong answer is possible.
	/// </param>
	public readonly record struct Resolved(uint Id, bool Pvp, bool Ambiguous);

	/// <summary>
	/// ⭐ <paramref name="preferPvp"/> comes from two places, either of which is sufficient: an
	/// explicit <c>/pvpac</c> verb, or the client reporting that you are in PvP.
	///
	/// ⚠ Falls back to the other map when the preferred one has no such name, so this is never worse
	/// than the single-map version it replaced -- a PvE-only action named in a PvP zone still
	/// resolves, and the game refuses it exactly as it would have.
	/// </summary>
	public static Resolved? Resolve(string name, bool preferPvp) {
		Build();
		name = name.Trim();

		bool inPvp = pvpByName!.TryGetValue(name, out uint pvpId);
		bool inPve = pveByName!.TryGetValue(name, out uint pveId);
		bool both = inPvp && inPve;

		if (preferPvp && inPvp)
			return new Resolved(pvpId, true, both);
		if (!preferPvp && inPve)
			return new Resolved(pveId, false, both);
		if (inPvp)
			return new Resolved(pvpId, true, both);
		if (inPve)
			return new Resolved(pveId, false, both);
		return null;
	}

	/// <summary>
	/// ⭐ Whether the client says you are in PvP, which is the default half of the preference above.
	///
	/// ⚠ Uses <c>IsPvP</c> and NOT <c>IsPvPExcludingDen</c>, deliberately. The Wolves' Den Pier is
	/// flagged as a PvP zone by the game -- the only such zone with an ordinary overworld
	/// TerritoryIntendedUse -- and your hotbar swaps to the PvP bar while you stand in it. So the Den
	/// is precisely a place where a bare /ac means the PvP action, and excluding it would break the
	/// one zone people practise in.
	/// </summary>
	public static bool InPvp => Plugin.ClientState.IsPvP;

	/// <summary>
	/// The action commands that take an action name plus a placeholder, from the TextCommand sheet.
	///
	/// ⚠⚠ <c>/pvpaction</c> being missing here is the gap deserok found: <c>/ifmo /pvpac "Guardian"
	/// {mo}</c> parsed no name at all, so it silently dropped to the weak presence check instead of
	/// validating range and line of sight.
	///
	/// ⚠ Order is not load-bearing but the trailing space is: without it <c>/ac </c> would match
	/// inside <c>/action</c>. Every entry keeps it.
	///
	/// ⚠ <c>/gaction</c> is deliberately absent -- general actions live in their own sheet and do not
	/// resolve to an Action row, so accepting the verb would mean parsing a name we then cannot look
	/// up. Better to not claim the verb than to claim it and degrade.
	/// </summary>
	private static readonly (string Verb, bool Pvp)[] Verbs = {
		("/pvpaction ", true),
		("/pvpac ", true),
		("/blueaction ", false),
		("/action ", false),
		("/ac ", false),
	};

	/// <summary>Where the action name sits in the line, how it was quoted, and which verb introduced it.</summary>
	public readonly record struct ActionSpan(string Name, int Start, int Length, bool Quoted, bool PvpVerb);

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
		foreach (var (verb, pvp) in Verbs) {
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
				return new ActionSpan(line[(at + 1)..close], at, close - at + 1, true, pvp);
			}

			// Unquoted: everything up to the placeholder or the end. "/ac Clemency {mo}" -> "Clemency"
			int brace = line.IndexOf('{', at);
			int angle = line.IndexOf('<', at);
			int cut = brace >= 0 && angle >= 0 ? Math.Min(brace, angle) : Math.Max(brace, angle);
			int end = cut >= 0 ? cut : line.Length;
			string name = line[at..end].TrimEnd();
			return name.Length > 0 ? new ActionSpan(name, at, name.Length, false, pvp) : null;
		}

		return null;
	}
}
