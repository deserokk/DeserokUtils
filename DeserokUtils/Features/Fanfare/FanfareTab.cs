using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;

using DeserokUtils.Features.Fanfare.Notify;

namespace DeserokUtils.Features.Fanfare;

internal sealed class FanfareTab {
	private readonly Action<string?> preview;
	private readonly Action<bool> setHold;
	private readonly Rarity rarity;
	private readonly SoundPlayer sound;
	private readonly SoundLibrary library;
	private readonly FileDialogManager fileDialogs;

	private bool holding;

	internal FanfareTab(
		Action<string?> preview,
		Action<bool> setHold,
		Rarity rarity,
		SoundPlayer sound,
		SoundLibrary library,
		FileDialogManager fileDialogs) {

		this.preview = preview;
		this.setHold = setHold;
		this.rarity = rarity;
		this.sound = sound;
		this.library = library;
		this.fileDialogs = fileDialogs;
	}

	internal long LastDrawn { get; private set; }

	private static readonly string[] Labels = [
		"Anchor", "Size", "Max width", "Nudge sideways", "Nudge up/down",
		"On screen for", "Animation speed", "Unlock message",
		"Rare below", "Rare message", "Sound", "Rare sound", "Volume",
	];

	private static float LabelColumn {
		get {
			var widest = 0f;
			foreach (var label in Labels)
				widest = Math.Max(widest, ImGui.CalcTextSize(label).X);

			return widest + ImGui.CalcTextSize(" (?)").X + (ImGui.GetFontSize() * 1.4f);
		}
	}

	public void DropHold() {
		if (!this.holding)
			return;
		this.holding = false;
		this.setHold(false);
	}

	public void Draw() {
		this.LastDrawn = Environment.TickCount64;

		ImGui.PushItemWidth(-LabelColumn);

		var config = Plugin.Config.Fanfare;
		var preset = Preset.ForStyle(config.Style);
		var changed = false;

		if (ImGui.Button("Preview a random achievement"))
			this.preview(null);

		ImGui.SameLine();

		var hold = this.holding;
		if (ImGui.Checkbox("Hold on screen", ref hold)) {
			this.holding = hold;
			this.setHold(hold);
		}
		ImGui.Separator();
		ImGui.TextUnformatted("Placement");
		ImGui.Spacing();

		var anchor = (int)config.Anchor;
		if (ImGui.Combo("Anchor", ref anchor,
			"Top left\0Top centre\0Top right\0Bottom left\0Bottom centre\0Bottom right\0")) {
			config.Anchor = (NotifyAnchor)anchor;
			changed = true;
		}
		var scale = config.Scale;
		if (ImGui.SliderFloat("Size", ref scale, 0.75f, 3f, "%.2fx")) {
			config.Scale = scale;
			changed = true;
		}

		var maxWidth = config.MaxWidth;
		if (ImGui.SliderFloat("Max width", ref maxWidth, 300f, 1000f, "%.0f")) {
			config.MaxWidth = maxWidth;
			changed = true;
		}
		Hint("The card grows to fit long achievement names, up to this.");

		var offsetX = config.OffsetX;
		if (ImGui.SliderFloat("Nudge sideways", ref offsetX, -800f, 800f, "%.0f px")) {
			config.OffsetX = offsetX;
			changed = true;
		}

		var offsetY = config.OffsetY;
		if (ImGui.SliderFloat("Nudge up/down", ref offsetY, -800f, 800f, "%.0f px")) {
			config.OffsetY = offsetY;
			changed = true;
		}
		Hint("Negative is up. Anchored to the bottom, around -150 clears the hotbars.");

		if (ImGui.Button("Centre it")) {
			config.OffsetX = 0f;
			config.OffsetY = 0f;
			changed = true;
		}

		ImGui.Separator();
		ImGui.TextUnformatted("Timing");
		ImGui.Spacing();

		var display = config.DisplayTimeOverride > 0f ? config.DisplayTimeOverride : preset.DisplayTime;
		if (ImGui.SliderFloat("On screen for", ref display, 3f, 15f, "%.1f s")) {
			config.DisplayTimeOverride = display;
			changed = true;
		}
		var transition = config.TransitionOverride > 0f ? config.TransitionOverride : preset.Transition;
		if (ImGui.SliderFloat("Animation speed", ref transition, 0.05f, 0.4f, "%.2f s")) {
			config.TransitionOverride = transition;
			changed = true;
		}
		ImGui.Separator();
		ImGui.TextUnformatted("Content");
		ImGui.Spacing();

		var message = config.UnlockMessage;
		if (ImGui.InputText("Unlock message", ref message, 64)) {
			config.UnlockMessage = message;
			changed = true;
		}

		var showDescription = config.ShowDescription;
		if (ImGui.Checkbox("Show the description too", ref showDescription)) {
			config.ShowDescription = showDescription;
			changed = true;
		}

		var suppress = config.SuppressDefaultPopup;
		if (ImGui.Checkbox("Hide the game's own popup", ref suppress)) {
			config.SuppressDefaultPopup = suppress;
			changed = true;
		}
		var showPoints = config.ShowPoints;
		if (ImGui.Checkbox("Show point value", ref showPoints)) {
			config.ShowPoints = showPoints;
			changed = true;
		}

		var showReward = config.ShowReward;
		if (ImGui.Checkbox("Announce the reward", ref showReward)) {
			config.ShowReward = showReward;
			changed = true;
		}
		Hint("Adds a third slide naming the title, mount or minion the achievement gave you. Only " +
			"appears when there is one.");

		ImGui.Separator();
		ImGui.TextUnformatted("Rarity");
		ImGui.Spacing();

		if (!this.rarity.Loaded) {
			ImGui.TextDisabled("Rarity data failed to load; every achievement is treated as ordinary.");
		} else {
			var threshold = config.RareThreshold;
			if (ImGui.SliderFloat("Rare below", ref threshold, 0.1f, 20f, "%.1f%% owned")) {
				config.RareThreshold = threshold;
				changed = true;
			}

			var qualifying = this.rarity.CountAtOrBelow(config.RareThreshold);
			ImGui.TextDisabled($"    {qualifying:N0} of {this.rarity.Total:N0} achievements " +
				$"({(float)qualifying / this.rarity.Total:P1}) count as rare");

			var rareMessage = config.RareUnlockMessage;
			if (ImGui.InputText("Rare message", ref rareMessage, 64)) {
				config.RareUnlockMessage = rareMessage;
				changed = true;
			}

			var showRarity = config.ShowRarity;
			if (ImGui.Checkbox("Show how many players have it", ref showRarity)) {
				config.ShowRarity = showRarity;
				changed = true;
			}
			Hint("The share of players registered on FFXIV Collect, who are far more completionist " +
				"than average -- true rarity is higher than the number shown.\n\n" +
				"Data from ffxivcollect.com, bundled with the plugin. Nothing is fetched at runtime.");
		}

		ImGui.Separator();
		ImGui.TextUnformatted("Sound");
		ImGui.Spacing();

		changed |= this.DrawSoundRow("Sound", "##sound",
			config.SoundFor(config.Style), p => config.SetSoundFor(config.Style, p, rare: false));

		changed |= this.DrawSoundRow("Rare sound", "##raresound",
			config.RareSoundOverrideFor(config.Style),
			p => config.SetSoundFor(config.Style, p, rare: true));

		if (!string.IsNullOrWhiteSpace(config.SoundFor(config.Style))
			&& string.IsNullOrWhiteSpace(config.RareSoundOverrideFor(config.Style)))
			ImGui.TextDisabled("    Rare achievements will use the ordinary sound.");

		if (ImGui.Button("Open my sounds folder")) {
			try {
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
					FileName = this.library.PersonalFolder,
					UseShellExecute = true,
				});
			} catch (Exception ex) {
				Plugin.Log.Error(ex, "could not open the sounds folder");
			}
		}

		ImGui.SameLine();
		if (ImGui.Button("Rescan"))
			this.library.Rescan();

		Hint("Anything you drop in this folder appears in the lists above.");

		var volume = config.SoundVolume;
		if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f, "%.2f")) {
			config.SoundVolume = volume;
			changed = true;
		}
		Hint("Plays outside the game's mixer, so it ignores every FFXIV volume setting including " +
			"mute, and will play while you are tabbed out.");

		ImGui.PopItemWidth();

		if (changed)
			config.Save();
	}

	private bool DrawSoundRow(string label, string id, string current, Action<string> assign) {
		var changed = false;

		var style = ImGui.GetStyle();
		var reserved = ImGui.CalcTextSize("Own file").X
			+ ImGui.CalcTextSize("Play").X
			+ (style.FramePadding.X * 4f)
			+ (style.ItemSpacing.X * 3f)
			+ LabelColumn;

		ImGui.SetNextItemWidth(
			Math.Max(ImGui.GetFontSize() * 8f, ImGui.GetContentRegionAvail().X - reserved));
		if (ImGui.BeginCombo(id, this.library.DisplayName(current))) {
			if (ImGui.Selectable("None", string.IsNullOrWhiteSpace(current))) {
				assign(string.Empty);
				changed = true;
			}

			foreach (var entry in this.library.Entries) {
				var selected = string.Equals(entry.Path, current, StringComparison.OrdinalIgnoreCase);
				if (ImGui.Selectable(entry.Name, selected)) {
					assign(entry.Path);
					this.sound.ForgetFailures();
					changed = true;
				}

				ImGui.SameLine();
				if (ImGui.SmallButton($"▶##{id}{entry.Path}"))
					this.sound.Play(this.library.Resolve(entry.Path), Plugin.Config.Fanfare.SoundVolume);
			}

			if (!string.IsNullOrWhiteSpace(current) && !this.library.IsKnown(current)) {
				if (ImGui.Selectable(this.library.DisplayName(current), true))
					changed = true;
			}

			ImGui.EndCombo();
		}

		ImGui.SameLine();
		if (ImGui.Button($"Own file{id}")) {
			this.fileDialogs.OpenFileDialog(
				$"Choose a {label.ToLowerInvariant()}",
				"Audio{.wav,.mp3,.aiff,.aif,.wma}",
				(ok, chosen) => {
					if (!ok || string.IsNullOrWhiteSpace(chosen))
						return;
					assign(chosen);
					this.sound.ForgetFailures();
					Plugin.Config.Fanfare.Save();
				});
		}

		ImGui.SameLine();
		ImGui.BeginDisabled(string.IsNullOrWhiteSpace(current));
		if (ImGui.Button($"Play{id}"))
			this.sound.Play(this.library.Resolve(current), Plugin.Config.Fanfare.SoundVolume);
		ImGui.EndDisabled();

		ImGui.SameLine();
		ImGui.TextUnformatted(label);

		return changed;
	}

	private static void Hint(string text) {
		ImGui.SameLine();
		ImGui.TextDisabled("(?)");
		if (!ImGui.IsItemHovered())
			return;

		ImGui.BeginTooltip();
		ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
		ImGui.TextUnformatted(text);
		ImGui.PopTextWrapPos();
		ImGui.EndTooltip();
	}
}
