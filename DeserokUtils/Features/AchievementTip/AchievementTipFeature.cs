using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DeserokUtils.Features.AchievementTip;

/// <summary>
/// Click an achievement in chat and find out what it actually is.
///
/// ## The gap
///
/// Somebody near you earns an achievement. You want to know what it is. The game gives you nothing:
/// hovering shows no tooltip, and clicking opens YOUR achievements at "Last Five" -- not the one that
/// was linked. deserok: *"if I earned 6? welp, time to truffle hunt."* ⚠ FFXIV has no achievement
/// linking either, so you cannot even paste one to ask somebody.
///
/// ## ⭐⭐ Why this rewrites the message instead of branching per chat plugin
///
/// deserok runs Chat 2, which draws its own window; Bunny and Q run the default. The obvious shape --
/// *"if Chat 2 do this, if vanilla do that"* -- fails on both sides: Chat 2 owns its hit-testing and
/// exposes no API for it, and the native chat's link rectangles belong to a window a Chat 2 user is
/// not looking at.
///
/// ⭐ So neither renderer is asked to cooperate. The link is rewritten to carry a
/// <c>DalamudLinkPayload</c> **before either of them draws it**, and both already know how to render
/// one and route its click back to the owning plugin. One path, both chats. It is how SimpleTweaks'
/// clickable URLs work in both, which is what pointed the way.
///
/// ⚠ HOVER WAS THE ASK AND THIS IS A CLICK. Chat 2 has hover handlers for items, statuses and URIs
/// and none for achievements, with no way to add one from outside -- so a click is the ceiling for
/// that setup, and deserok accepted it knowingly rather than it being an oversight.
///
/// ## ⚠ This MODIFIES a chat message, which is a line worth being deliberate about
///
/// Everything downstream sees the altered version -- Chat 2's database, other plugins, the log. That
/// is only acceptable because of what it touches: a **system** line, whose existing link does
/// something useless. ⭐ Hijacking the name people already click means no clutter is added and no
/// working behaviour is taken away -- the window offers "open achievements" for what the click used to
/// do.
/// </summary>
internal sealed class AchievementTipFeature: IDisposable {
	/// <summary>
	/// ⚠ One Dalamud link handler per achievement seen, keyed by its id -- the id IS the command id,
	/// so a click hands it straight back with nothing to re-parse.
	///
	/// ⚠ Bounded by how many distinct achievements scroll past in one session, which is a handful.
	/// Registered lazily and never swept, deliberately: unregistering one would break the link in any
	/// older line still sitting in the scrollback.
	/// </summary>
	private readonly Dictionary<uint, DalamudLinkPayload> handlers = new();

	private uint shownId;
	private bool showWindow;
	private Vector2 openAt;

	public AchievementTipFeature() {
		Plugin.Chat.ChatMessage += this.OnChatMessage;
		Plugin.PluginInterface.UiBuilder.Draw += this.Draw;
	}

	/// <summary>
	/// ⚠ The narrowest gate first. Achievement lines arrive as <c>XivChatType.Progress</c>, measured,
	/// so everything else leaves immediately -- there is no path from here to rewriting a tell.
	/// </summary>
	private void OnChatMessage(IHandleableChatMessage message) {
		try {
			if (!Plugin.Config.AchievementTipEnabled)
				return;
			if (message.LogKind != Dalamud.Game.Text.XivChatType.Progress)
				return;

			var found = AchievementLink.FindIn(message.Message);
			if (found is not var (raw, achievement))
				return;

			var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Achievement>();
			var row = sheet?.GetRowOrDefault(achievement.Id);
			if (row is null) {
				Plugin.Log.Warning($"AchievementTip: link decoded to id {achievement.Id}, which is not in the sheet.");
				return;
			}

			// ⭐⭐ THE CROSS-CHECK, and it is the whole reason the embedded name is parsed at all. The
			// game puts the achievement's name inside the link; if the id we decoded points at a
			// different name, the decode is wrong and we must NOT show a confident description of the
			// wrong achievement. Reported loudly and skipped.
			string sheetName = row.Value.Name.ExtractText();
			if (achievement.Name.Length > 0
				&& !string.Equals(sheetName, achievement.Name, StringComparison.Ordinal)) {
				Plugin.Log.Warning(
					$"AchievementTip: id {achievement.Id} is \"{sheetName}\" but the link says "
					+ $"\"{achievement.Name}\". Not rewriting -- the decode disagrees with the game.");
				return;
			}

			if (!this.handlers.TryGetValue(achievement.Id, out var link)) {
				link = Plugin.Chat.AddChatLinkHandler(achievement.Id, this.OnLinkClicked);
				this.handlers[achievement.Id] = link;
			}

			// ⚠ Rebuilt rather than mutated in place: the payload list is what the renderers read, and
			// the link payload has to WRAP the existing text run to make that text clickable.
			var rebuilt = new SeString();
			foreach (var payload in message.Message.Payloads) {
				if (!ReferenceEquals(payload, raw)) {
					rebuilt.Payloads.Add(payload);
					continue;
				}

				// ⭐ Our link replaces the game's, around the same words. Nothing is added to the line,
				// so it looks untouched -- the click just stops being useless.
				rebuilt.Payloads.Add(link);
			}

			// ⚠ NO terminator is appended. The game's own line already ends the link with one -- the
			// second raw chunk, 02 27 07 CF ... -- and it is preserved by the rebuild above because it
			// does not parse as an achievement. Adding another would close a link that is already
			// closed, which is the kind of thing that renders fine in one chat plugin and not the other.
			message.Message = rebuilt;
		}
		catch (Exception ex) {
			// ⚠ Never throw at a chat handler. A broken rewrite must lose the feature, not the message.
			Plugin.Log.Error(ex, "AchievementTip: failed to rewrite an achievement link.");
		}
	}

	private void OnLinkClicked(uint id, SeString _) {
		this.shownId = id;
		this.showWindow = true;
		this.openAt = ImGui.GetMousePos();
	}

	/// <summary>
	/// ⭐ Opens AT the cursor, per deserok: *"a gui that just appears at mouse"*. Positioned only on
	/// the frame it opens, so it can then be dragged and stays where it was put.
	/// </summary>
	private void Draw() {
		if (!this.showWindow)
			return;

		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Achievement>();
		var row = sheet?.GetRowOrDefault(this.shownId);
		if (row is null) {
			this.showWindow = false;
			return;
		}

		var achievement = row.Value;
		ImGui.SetNextWindowPos(this.openAt + new Vector2(12f, 12f), ImGuiCond.Appearing);
		ImGui.SetNextWindowSizeConstraints(new Vector2(300f, 0f), new Vector2(460f, 600f));

		bool open = this.showWindow;
		if (ImGui.Begin($"{achievement.Name.ExtractText()}##dsuAchievement", ref open,
				ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)) {
			ImGui.TextWrapped(achievement.Description.ExtractText());
			ImGui.Spacing();
			ImGui.Separator();
			ImGui.Spacing();

			ImGui.Text($"{achievement.Points} points");

			string reward = this.RewardOf(achievement);
			if (reward.Length > 0)
				ImGui.TextWrapped($"Reward: {reward}");

			// ⚠⚠ THREE STATES, NOT TWO. The client only knows what you have earned once the achievement
			// UI has been opened at least once in this session, so "not earned" and "not loaded yet"
			// are different facts -- and reporting the second as the first would be a confident lie
			// about somebody's own progress.
			ImGui.Spacing();
			unsafe {
				var state = Achievement.Instance();
				if (state is null || !state->IsLoaded())
					ImGui.TextDisabled("Earned: not known yet -- open the Achievements window once.");
				else if (state->IsComplete((int)this.shownId))
					ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Earned");
				else
					ImGui.TextDisabled("Not earned");
			}

			ImGui.Spacing();
			if (ImGui.Button("Open Achievements"))
				Plugin.Chat.Print("[AchievementTip] open the Achievements window from the main menu.");
		}

		ImGui.End();
		this.showWindow = open;
	}

	private string RewardOf(Lumina.Excel.Sheets.Achievement achievement) {
		try {
			if (achievement.Item.RowId > 0) {
				var item = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(achievement.Item.RowId);
				if (item is not null)
					return item.Value.Name.ExtractText();
			}

			if (achievement.Title.RowId > 0) {
				var title = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Title>()?.GetRowOrDefault(achievement.Title.RowId);
				if (title is not null)
					return $"the title \"{title.Value.Masculine.ExtractText()}\"";
			}
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "AchievementTip: could not read the reward.");
		}

		return string.Empty;
	}

	public void Dispose() {
		Plugin.Chat.ChatMessage -= this.OnChatMessage;
		Plugin.PluginInterface.UiBuilder.Draw -= this.Draw;
		foreach (uint id in this.handlers.Keys)
			Plugin.Chat.RemoveChatLinkHandler(id);
		this.handlers.Clear();
	}
}
