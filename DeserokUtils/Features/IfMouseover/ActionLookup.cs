using System;
using System.Collections.Generic;

namespace DeserokUtils.Features.IfMouseover;

internal static class ActionLookup {
	private static Dictionary<string, uint>? pveByName;
	private static Dictionary<string, uint>? pvpByName;

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

				var target = row.IsPvP ? pvp : pve;
				if (!target.ContainsKey(name))
					target[name] = row.RowId;
			}
		}

		pveByName = pve;
		pvpByName = pvp;
		Plugin.Log.Information($"IfMouseover: action name maps built, {pve.Count} non-PvP / {pvp.Count} PvP.");
	}

	public readonly record struct Resolved(uint Id, bool Pvp, bool Ambiguous);

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

	public static string? Suggest(string name) {
		Build();
		var names = new List<string>(pveByName!.Keys);
		names.AddRange(pvpByName!.Keys);
		return NameSuggest.Closest(name, names);
	}

	public static bool InPvp => Plugin.ClientState.IsPvP;

	private static readonly (string Verb, bool Pvp)[] Verbs = {
		("/pvpaction ", true),
		("/pvpac ", true),
		("/blueaction ", false),
		("/action ", false),
		("/ac ", false),
	};

	public readonly record struct ActionSpan(string Name, int Start, int Length, bool Quoted, bool PvpVerb);

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

			if (line[at] is '"' or '\'') {
				char quote = line[at];
				int close = line.IndexOf(quote, at + 1);
				if (close <= at + 1)
					return null;
				return new ActionSpan(line[(at + 1)..close], at, close - at + 1, true, pvp);
			}

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
