using System;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DeserokUtils.Features.DrawSheathe;

internal static class EmoteUnlock {

	private static Dictionary<string, (ushort Id, string Name)>? byCommand;

	private static Dictionary<string, (ushort Id, string Name)> Map() {
		if (byCommand is not null)
			return byCommand;

		var map = new Dictionary<string, (ushort, string)>(StringComparer.OrdinalIgnoreCase);
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
		if (sheet is not null) {
			foreach (var row in sheet) {
				if (row.RowId > ushort.MaxValue)
					continue;
				string name = row.Name.ExtractText();
				if (name.Length == 0)
					continue;

				string command = "", alias = "";
				try {
					var tc = row.TextCommand.ValueNullable;
					if (tc is not null) {
						command = tc.Value.Command.ExtractText();
						alias = tc.Value.Alias.ExtractText();
					}
				}
				catch {

					continue;
				}

				foreach (string key in new[] { command, alias }) {
					if (key.Length > 0 && !map.ContainsKey(key))
						map[key] = ((ushort)row.RowId, name);
				}
			}
		}

		Plugin.Log.Information($"DrawSheathe: emote command map built, {map.Count} entries.");
		return byCommand = map;
	}

	public static unsafe string? LockedBecause(string configuredCommand) {

		string line = configuredCommand.Trim();
		if (line.Length == 0)
			return null;
		int space = line.IndexOf(' ');
		string verb = space < 0 ? line : line[..space];

		if (!Map().TryGetValue(verb, out var emote))
			return null;

		var ui = UIState.Instance();
		if (ui is null)
			return null;

		return ui->IsEmoteUnlocked(emote.Id)
			? null
			: $"you do not have the {emote.Name} emote";
	}
}
