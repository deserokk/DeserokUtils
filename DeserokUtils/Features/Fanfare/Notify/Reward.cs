using System;
using System.Linq;

using Dalamud.Game.ClientState.Objects.Enums;

using Lumina.Excel.Sheets;

namespace DeserokUtils.Features.Fanfare.Notify;

internal static class Reward {

	private static readonly string[] SpeakableCategories = [
		"Mount", "Minion", "Orchestrion Roll", "Triple Triad Card", "Framer's Kit",
	];

	private const string GenericLabel = "Reward Unlocked";

	internal static (string Label, string Name)? Resolve(Achievement row) {
		if (row.Title.RowId != 0 && row.Title.ValueNullable is { } title) {
			var text = TitleFor(title);
			if (!string.IsNullOrWhiteSpace(text))
				return ("Title Unlocked", text);
		}

		if (row.Item.RowId == 0 || row.Item.ValueNullable is not { } item)
			return null;

		var name = item.Name.ExtractText();
		if (string.IsNullOrWhiteSpace(name))
			return null;

		var category = item.ItemUICategory.ValueNullable?.Name.ExtractText() ?? string.Empty;

		if (SpeakableCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
			return ($"{category} Unlocked", name);

		if (Plugin.Config.Fanfare.Verbose && category.Length > 0)
			Plugin.Log.Information(
				$"Fanfare: reward category \"{category}\" is not in SpeakableCategories ({name}).");

		return (GenericLabel, name);
	}

	private static string TitleFor(Title title) {
		var masculine = title.Masculine.ExtractText();
		var feminine = title.Feminine.ExtractText();

		if (string.IsNullOrWhiteSpace(feminine))
			return masculine;

		var player = Plugin.Objects.LocalPlayer;
		if (player is null)
			return masculine;

		var customize = player.Customize;
		var index = (int)CustomizeIndex.Gender;

		return customize.Length > index && customize[index] == 1 ? feminine : masculine;
	}
}
