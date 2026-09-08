using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;

using DeserokUtils.Features.Fanfare.Notify;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DeserokUtils.Features.Food;

internal sealed class FoodFeature: IDisposable {
	public string TabTitle => "Food reminder";

	public string Summary => "Reminds you to eat while levelling, since any food is 3% experience.";

	private const uint WellFedStatus = 48;

	private const string BoopSound = "Sounds/food-boop.wav";

	private readonly SoundPlayer sound = new();
	private readonly SoundLibrary library;

	private bool wasFed;

	private bool wasInDuty;

	private bool dutyPending;

	private bool wasInCombat;

	private DateTime dutyEnteredAt = DateTime.MinValue;

	private static readonly TimeSpan DutySettle = TimeSpan.FromSeconds(4);

	private DateTime lastNag = DateTime.MinValue;

	private ISharedImmediateTexture? icon;

	public FoodFeature() {
		this.library = new SoundLibrary();
		Plugin.Framework.Update += this.OnFramework;
		Plugin.PluginInterface.UiBuilder.Draw += this.DrawOverlay;
	}

	public void Dispose() {
		Plugin.Framework.Update -= this.OnFramework;
		Plugin.PluginInterface.UiBuilder.Draw -= this.DrawOverlay;
		this.sound.Dispose();
	}

	private unsafe bool Relevant() {
		if (!Plugin.Config.FoodEnabled)
			return false;

		var me = Plugin.Objects.LocalPlayer;
		if (me is null)
			return false;

		if (!Plugin.Config.FoodAtMaxLevel) {
			var state = PlayerState.Instance();
			if (state is not null && state->MaxLevel > 0 && me.Level >= state->MaxLevel)
				return false;
		}

		var info = TerritoryInfo.Instance();
		if (info is not null && info->InSanctuary)
			return false;

		return true;
	}

	private static bool IsFed() {
		var me = Plugin.Objects.LocalPlayer;
		if (me is null)
			return false;

		foreach (var status in me.StatusList) {
			if (status?.StatusId == WellFedStatus)
				return true;
		}

		return false;
	}

	private void OnFramework(Dalamud.Plugin.Services.IFramework framework) {
		if (!Plugin.Config.FoodEnabled)
			return;

		var fed = IsFed();
		var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
		var inCombat = Plugin.Condition[ConditionFlag.InCombat];

		try {

			if (inDuty && !this.wasInDuty) {
				this.dutyPending = true;
				this.dutyEnteredAt = DateTime.UtcNow;
			}

			if (!inDuty)
				this.dutyPending = false;

			if (this.dutyPending
				&& Plugin.Objects.LocalPlayer is not null
				&& DateTime.UtcNow - this.dutyEnteredAt > DutySettle) {

				this.dutyPending = false;

				if (!fed && this.Relevant() && Plugin.Config.FoodBoopOnDuty)
					this.Boop();
			}

			if (this.wasFed && !fed && this.Relevant() && Plugin.Config.FoodSayOnLapse) {
				Plugin.Announce(Message());
				this.lastNag = DateTime.UtcNow;
			}

			if (inCombat && !this.wasInCombat && !inDuty && !fed && this.Relevant()
				&& Plugin.Config.FoodBoopOverworld
				&& DateTime.UtcNow - this.lastNag > TimeSpan.FromMinutes(Plugin.Config.FoodNagMinutes)) {

				this.lastNag = DateTime.UtcNow;
				this.Boop();
			}

			if (!fed && this.Relevant() && Plugin.Config.FoodSayPeriodically
				&& DateTime.UtcNow - this.lastNag > TimeSpan.FromMinutes(Plugin.Config.FoodNagMinutes)) {
				this.lastNag = DateTime.UtcNow;
				Plugin.Announce(Message());
			}
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "Food: checking the buff failed.");
		}
		finally {
			this.wasFed = fed;
			this.wasInDuty = inDuty;
			this.wasInCombat = inCombat;
		}
	}

	private static string Message() {
		var text = Plugin.Config.FoodMessage.Trim();
		return text.Length > 0 ? text : "No food buff";
	}

	private void Boop() {
		var path = string.IsNullOrWhiteSpace(Plugin.Config.FoodSoundPath)
			? BoopSound
			: Plugin.Config.FoodSoundPath;

		this.sound.Play(this.library.Resolve(path), Plugin.Config.FoodVolume);
	}

	private void DrawOverlay() {
		if (!Plugin.Config.FoodEnabled || !Plugin.Config.FoodShowIcon)
			return;

		if (!Plugin.Config.FoodIconPreview && (!this.Relevant() || IsFed()))
			return;

		if (!Plugin.Config.FoodIconPreview
			&& !Plugin.Condition[ConditionFlag.BoundByDuty]
			&& !Plugin.Config.FoodBoopOverworld)
			return;

		if (Plugin.PluginInterface.UiBuilder.CutsceneActive)
			return;

		if (!Plugin.Config.FoodIconPreview && Plugin.Condition[ConditionFlag.InCombat])
			return;

		this.icon ??= LoadIcon();
		if (this.icon?.GetWrapOrDefault() is not { } texture)
			return;

		var viewport = ImGui.GetMainViewport();
		var height = 64f * Plugin.Config.FoodIconScale * ImGuiHelpers.GlobalScale;

		var ratio = texture.Height > 0 ? (float)texture.Width / texture.Height : 1f;
		var size = new Vector2(height * ratio, height);

		var at = viewport.Pos + new Vector2(
			(viewport.Size.X - size.X) / 2f,
			(viewport.Size.Y / 2f) - (height * 1.8f));

		var draw = ImGui.GetForegroundDrawList();
		draw.AddImage(
			texture.Handle, at, at + size,
			Vector2.Zero, Vector2.One,
			ImGui.GetColorU32(new Vector4(1f, 1f, 1f, Plugin.Config.FoodIconAlpha)));

		var label = Message();
		if (label.Length == 0)
			return;

		var font = ImGui.GetFontSize() * Plugin.Config.FoodIconScale;
		var width = ImGui.CalcTextSize(label).X * Plugin.Config.FoodIconScale;
		var textAt = new Vector2(at.X + ((size.X - width) / 2f), at.Y + size.Y + (4f * ImGuiHelpers.GlobalScale));
		var alpha = Plugin.Config.FoodIconAlpha;

		draw.AddText(ImGui.GetFont(), font, textAt + new Vector2(1f, 1f),
			ImGui.GetColorU32(new Vector4(0f, 0f, 0f, alpha)), label);
		draw.AddText(ImGui.GetFont(), font, textAt,
			ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)), label);
	}

	private static ISharedImmediateTexture? LoadIcon() {
		try {
			var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>();
			if (sheet?.GetRowOrDefault(WellFedStatus) is not { } row || row.Icon == 0)
				return null;

			return Plugin.Textures.GetFromGameIcon(new GameIconLookup(row.Icon));
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "Food: could not load the Well Fed icon.");
			return null;
		}
	}

	public unsafe void DrawDiagnostics() {
		var me = Plugin.Objects.LocalPlayer;
		var state = PlayerState.Instance();
		var info = TerritoryInfo.Instance();

		void Row(string label, bool ok, string detail) {
			ImGui.TextUnformatted(ok ? "PASS" : "no  ");
			ImGui.SameLine();
			ImGui.TextDisabled($"{label}  {detail}");
		}

		Row("feature on", Plugin.Config.FoodEnabled, string.Empty);
		Row("player", me is not null, me?.Name.TextValue ?? "(none)");

		var cap = state is null ? (byte)0 : state->MaxLevel;
		Row("below cap", Plugin.Config.FoodAtMaxLevel || (me is not null && cap > 0 && me.Level < cap),
			$"level {me?.Level.ToString() ?? "?"} of {cap}"
			+ (Plugin.Config.FoodAtMaxLevel ? ", ignored" : string.Empty));

		var sanctuary = info is not null && info->InSanctuary;
		Row("not in a town", !sanctuary, sanctuary ? "in a sanctuary" : string.Empty);

		Row("unfed", !IsFed(), IsFed() ? "Well Fed is up" : string.Empty);

		var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
		Row("in scope", inDuty || Plugin.Config.FoodBoopOverworld,
			inDuty ? "in a duty" : "open world, and the open-world option is "
				+ (Plugin.Config.FoodBoopOverworld ? "on" : "OFF"));

		Row("icon on", Plugin.Config.FoodShowIcon, Plugin.Config.FoodIconPreview ? "preview forcing it" : string.Empty);
		Row("not in combat", !Plugin.Condition[ConditionFlag.InCombat], string.Empty);
	}

	public void DrawTab() {
		var changed = false;

		var message = Plugin.Config.FoodMessage;
		ImGui.SetNextItemWidth(-(ImGui.GetFontSize() * 11f));
		if (ImGui.InputTextWithHint("Reminder", "No food buff", ref message, 64)) {
			Plugin.Config.FoodMessage = message;
			changed = true;
		}

		ImGui.Spacing();

		var lapse = Plugin.Config.FoodSayOnLapse;
		if (ImGui.Checkbox("Say something when it runs out", ref lapse)) {
			Plugin.Config.FoodSayOnLapse = lapse;
			changed = true;
		}

		var boop = Plugin.Config.FoodBoopOnDuty;
		if (ImGui.Checkbox("Play a sound when a duty starts without one", ref boop)) {
			Plugin.Config.FoodBoopOnDuty = boop;
			changed = true;
		}

		if (boop) {
			ImGui.Indent(16f);

			var volume = Plugin.Config.FoodVolume;
			ImGui.SetNextItemWidth(-(ImGui.GetFontSize() * 11f));
			if (ImGui.SliderFloat("Volume##food", ref volume, 0f, 1f, "%.2f")) {
				Plugin.Config.FoodVolume = volume;
				changed = true;
			}

			if (ImGui.Button("Test##food"))
				this.Boop();

			ImGui.Unindent(16f);
		}

		var overworld = Plugin.Config.FoodBoopOverworld;
		if (ImGui.Checkbox("Remind me in the open world too", ref overworld)) {
			Plugin.Config.FoodBoopOverworld = overworld;
			changed = true;
		}

		if (overworld)
			ImGui.TextDisabled("    For FATE grinding. The sound is limited to the interval below.");

		var periodic = Plugin.Config.FoodSayPeriodically;
		if (ImGui.Checkbox("Keep reminding me while I have none", ref periodic)) {
			Plugin.Config.FoodSayPeriodically = periodic;
			changed = true;
		}

		if (periodic) {
			ImGui.Indent(16f);

			var minutes = Plugin.Config.FoodNagMinutes;
			ImGui.SetNextItemWidth(-(ImGui.GetFontSize() * 11f));
			if (ImGui.SliderInt("Every##food", ref minutes, 1, 30, "%d min")) {
				Plugin.Config.FoodNagMinutes = minutes;
				changed = true;
			}

			ImGui.Unindent(16f);
		}

		ImGui.Spacing();

		var showIcon = Plugin.Config.FoodShowIcon;
		if (ImGui.Checkbox("Put an icon on the screen until I eat", ref showIcon)) {
			Plugin.Config.FoodShowIcon = showIcon;
			changed = true;
		}

		if (showIcon) {
			ImGui.Indent(16f);

			var scale = Plugin.Config.FoodIconScale;
			ImGui.SetNextItemWidth(-(ImGui.GetFontSize() * 11f));
			if (ImGui.SliderFloat("Size##food", ref scale, 0.5f, 3f, "%.2fx")) {
				Plugin.Config.FoodIconScale = scale;
				changed = true;
			}

			var alpha = Plugin.Config.FoodIconAlpha;
			ImGui.SetNextItemWidth(-(ImGui.GetFontSize() * 11f));
			if (ImGui.SliderFloat("Opacity##food", ref alpha, 0.1f, 1f, "%.2f")) {
				Plugin.Config.FoodIconAlpha = alpha;
				changed = true;
			}

			var preview = Plugin.Config.FoodIconPreview;
			if (ImGui.Checkbox("Show it now, so I can size it##food", ref preview)) {
				Plugin.Config.FoodIconPreview = preview;
				changed = true;
			}

			ImGui.Unindent(16f);
		}

		ImGui.Spacing();

		var atCap = Plugin.Config.FoodAtMaxLevel;
		if (ImGui.Checkbox("Remind me at max level too", ref atCap)) {
			Plugin.Config.FoodAtMaxLevel = atCap;
			changed = true;
		}

		ImGui.TextDisabled(atCap ? "Silent in towns." : "Silent in towns, and once you hit max level.");

		if (changed)
			Plugin.Config.Save();
	}
}
