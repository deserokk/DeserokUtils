using System;
using System.Linq;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

using Lumina.Excel.Sheets;

using Achievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;
using AchievementSheet = Lumina.Excel.Sheets.Achievement;

namespace DeserokUtils.Features.Fanfare.Achievements;

internal static unsafe class AchievementQuery {
	internal static void ListIncomplete(string? filter, Notify.Rarity rarity) {
		if (string.IsNullOrWhiteSpace(filter)) {
			Plugin.Chat.PrintError("[Fanfare] give me something to search for, e.g. /fanfare todo mapping the realm");
			return;
		}

		var achievement = Achievement.Instance();

		if (achievement is null || !achievement->IsLoaded()) {
			Plugin.Chat.PrintError(
				"[Fanfare] achievement data is not loaded yet. Open your Achievements window once, then try again.");
			return;
		}

		var matches = Plugin.Data.GetExcelSheet<AchievementSheet>()
			.Where(a => a.Name.ExtractText().Contains(filter, StringComparison.OrdinalIgnoreCase))
			.Where(a => !achievement->IsComplete((int)a.RowId))
			.Select(a => (a.RowId, Name: a.Name.ExtractText(), Percent: rarity.PercentOwned(a.RowId)))
			.Where(a => !string.IsNullOrWhiteSpace(a.Name))

			.OrderByDescending(a => a.Percent ?? -1f)
			.ToList();

		if (matches.Count == 0) {
			Plugin.Chat.Print($"[Fanfare] nothing outstanding matching '{filter}'. All done.");
			return;
		}

		Plugin.Chat.Print($"[Fanfare] {matches.Count} not yet earned matching '{filter}' (easiest first):");

		foreach (var (id, name, percent) in matches.Take(20)) {
			var share = percent is float p ? $"{p:0.#}%" : "  ?  ";
			var star = percent is float q && q <= Plugin.Config.Fanfare.RareThreshold ? " ★rare" : string.Empty;
			Plugin.Chat.Print($"   [{id}] {share,6}  {name}{star}");
		}

		if (matches.Count > 20)
			Plugin.Chat.Print($"   ...and {matches.Count - 20} more. Full list in dalamud.log.");

		Plugin.Log.Information($"todo '{filter}': " +
			string.Join(", ", matches.Select(m => $"[{m.RowId}] {m.Name} {m.Percent}")));
	}
}
