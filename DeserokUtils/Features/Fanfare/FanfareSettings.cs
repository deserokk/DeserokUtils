using System;
using System.Collections.Generic;
using System.Linq;

using DeserokUtils.Features.Fanfare.Notify;

using Newtonsoft.Json;

namespace DeserokUtils.Features.Fanfare;

public enum NotifyAnchor {
	TopLeft,
	TopCentre,
	TopRight,
	BottomLeft,
	BottomCentre,
	BottomRight,
}

[Serializable]
public sealed class FanfareSettings {

	public bool Enabled { get; set; }

	public bool Imported { get; set; }

	public string UnlockMessage { get; set; } = "Achievement Unlocked";

	public string RareUnlockMessage { get; set; } = "Rare Achievement Unlocked";

	public float RareThreshold { get; set; } = 1f;

	public bool ShowRarity { get; set; } = true;

	public PresetStyle Style { get; set; } = PresetStyle.XboxOne;

	public bool SuppressDefaultPopup { get; set; } = true;

	public NotifyAnchor Anchor { get; set; } = NotifyAnchor.BottomCentre;

	public float OffsetX { get; set; }

	public float OffsetY { get; set; }

	public float Scale { get; set; } = 1.5f;

	public float MaxWidth { get; set; } = 620f;

	public float DisplayTimeOverride { get; set; }

	public float TransitionOverride { get; set; }

	public bool ShowDescription { get; set; } = true;

	public bool ShowPoints { get; set; } = true;

	public bool ShowReward { get; set; } = true;

	[JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
	public Dictionary<PresetStyle, string> PresetSounds { get; set; } = new() {
		[PresetStyle.XboxOne] = DefaultChime,
	};

	internal const string DefaultChime = "Sounds/NormalChime.wav";

	internal const string DefaultRareChime = "Sounds/RareChime.wav";

	[JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
	public Dictionary<PresetStyle, string> PresetRareSounds { get; set; } = new() {
		[PresetStyle.XboxOne] = DefaultRareChime,
	};

	internal string SoundFor(PresetStyle style)
		=> this.PresetSounds.TryGetValue(style, out var path) ? path : string.Empty;

	internal string RareSoundOverrideFor(PresetStyle style)
		=> this.PresetRareSounds.TryGetValue(style, out var path) ? path : string.Empty;

	internal string RareSoundFor(PresetStyle style) {
		if (this.PresetRareSounds.TryGetValue(style, out var rare) && !string.IsNullOrWhiteSpace(rare))
			return rare;
		return this.SoundFor(style);
	}

	internal void SetSoundFor(PresetStyle style, string path, bool rare) {
		var target = rare ? this.PresetRareSounds : this.PresetSounds;
		if (string.IsNullOrWhiteSpace(path))
			target.Remove(style);
		else
			target[style] = path;
	}

	public float SoundVolume { get; set; } = 0.05f;

	public bool Verbose { get; set; }

	public void Save() => Plugin.Config.Save();

}
