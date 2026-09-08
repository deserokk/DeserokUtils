using System;

using Dalamud.Interface.Textures;

using Lumina.Excel.Sheets;

namespace DeserokUtils.Features.Fanfare.Notify;

internal sealed class Notification {
	internal required string UnlockMessage { get; init; }
	internal required string Title { get; init; }
	internal required string Description { get; init; }
	internal required byte Points { get; init; }

	internal float? PercentOwned { get; init; }

	internal bool TooNew { get; init; }

	internal bool IsRare { get; init; }

	internal ISharedImmediateTexture? Icon { get; init; }

	internal float? MeasuredWidth { get; set; }

	internal int MeasuredUnder { get; set; }

	internal string DescriptionLine { get; set; } = string.Empty;

	internal string UnlockLine { get; set; } = string.Empty;

	internal string TitleLine { get; set; } = string.Empty;

	internal string RewardLabel { get; init; } = string.Empty;

	internal string RewardName { get; init; } = string.Empty;

	internal string RewardLabelLine { get; set; } = string.Empty;

	internal string RewardNameLine { get; set; } = string.Empty;

	internal bool HasDescriptionSlide
		=> Plugin.Config.Fanfare.ShowDescription && !string.IsNullOrWhiteSpace(this.Description);

	internal bool HasRewardSlide => !string.IsNullOrWhiteSpace(this.RewardName);

	internal int SlideCount
		=> 1 + (this.HasDescriptionSlide ? 1 : 0) + (this.HasRewardSlide ? 1 : 0);

	internal string RarityLine { get; set; } = string.Empty;

	internal DateTime StartedAt { get; set; } = DateTime.UtcNow;

	internal float Elapsed => (float)(DateTime.UtcNow - this.StartedAt).TotalSeconds;

	internal static Notification? FromAchievement(uint achievementId, Rarity rarity) {
		var sheet = Plugin.Data.GetExcelSheet<Achievement>();
		if (!sheet.TryGetRow(achievementId, out var row))
			return null;

		var title = row.Name.ExtractText();
		if (string.IsNullOrWhiteSpace(title))
			return null;

		var percent = rarity.PercentOwned(achievementId);

		var isRare = percent is float p && p <= Plugin.Config.Fanfare.RareThreshold;

		var reward = Plugin.Config.Fanfare.ShowReward ? Reward.Resolve(row) : null;

		return new Notification {
			UnlockMessage = isRare ? Plugin.Config.Fanfare.RareUnlockMessage : Plugin.Config.Fanfare.UnlockMessage,
			Title = title,
			Description = row.Description.ExtractText(),
			Points = row.Points,
			PercentOwned = percent,
			TooNew = rarity.TooNew(achievementId),
			IsRare = isRare,
			Icon = row.Icon != 0
				? Plugin.Textures.GetFromGameIcon(new GameIconLookup(row.Icon))
				: null,
			RewardLabel = reward?.Label ?? string.Empty,
			RewardName = reward?.Name ?? string.Empty,
		};
	}
}
