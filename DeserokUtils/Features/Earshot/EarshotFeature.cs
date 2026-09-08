using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

using DeserokUtils.Features.ChatColours;
using DeserokUtils.Features.Fanfare.Notify;

using FFXIVClientStructs.FFXIV.Client.UI;

namespace DeserokUtils.Features.Earshot;

internal sealed class EarshotFeature: IDisposable {
	public string TabTitle => "Earshot";

	public string Summary => "Marks your name in chat and plays a sound when somebody says it.";

	private static readonly HashSet<XivChatType> Channels = [
		XivChatType.Say, XivChatType.Shout, XivChatType.Yell,
		XivChatType.Party, XivChatType.CrossParty, XivChatType.Alliance,
		XivChatType.FreeCompany, XivChatType.NoviceNetwork, XivChatType.PvPTeam,
		XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
		XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
		XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3,
		XivChatType.CrossLinkShell4, XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6,
		XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
	];

	internal const string BuiltInBell = "Sounds/earshot-bell.wav";

	internal const string BuiltInSoft = "Sounds/earshot-soft.wav";

	private readonly SoundPlayer sound = new();
	private readonly SoundLibrary library;

	private DateTime lastSound = DateTime.MinValue;

	public EarshotFeature() {

		this.library = new SoundLibrary();
		Plugin.Chat.ChatMessage += this.OnChatMessage;
	}

	public void Dispose() {
		Plugin.Chat.ChatMessage -= this.OnChatMessage;
		this.sound.Dispose();
	}

	private void OnChatMessage(IHandleableChatMessage message) {
		if (!Plugin.Config.EarshotEnabled)
			return;

		try {
			if (!Channels.Contains(message.LogKind))
				return;

			var me = Plugin.Objects.LocalPlayer?.Name.TextValue ?? string.Empty;
			if (me.Length > 0 && message.Sender.TextValue.Contains(me, StringComparison.Ordinal))
				return;

			var triggers = this.Triggers();
			if (triggers.Count == 0)
				return;

			if (Highlight(message.Message, triggers) is not { } marked)
				return;

			message.Message = marked;

			if (DateTime.UtcNow - this.lastSound < TimeSpan.FromSeconds(Plugin.Config.EarshotCooldown))
				return;

			this.lastSound = DateTime.UtcNow;
			this.PlayAlert();
		}
		catch (Exception ex) {

			Plugin.Log.Error(ex, "Earshot: checking a line failed.");
		}
	}

	private void PlayAlert() {
		var path = Plugin.Config.EarshotSoundPath;

		if (!string.IsNullOrWhiteSpace(path))
			this.sound.Play(this.library.Resolve(path), Plugin.Config.EarshotVolume);
		else
			UIGlobals.PlayChatSoundEffect((uint)Math.Clamp(Plugin.Config.EarshotSound, 1, 16));
	}

	private List<string> Triggers() {
		var list = new List<string>(4);
		var full = Plugin.Objects.LocalPlayer?.Name.TextValue ?? string.Empty;
		var parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);

		if (Plugin.Config.EarshotFirstName && parts.Length > 0)
			list.Add(parts[0]);

		if (Plugin.Config.EarshotLastName && parts.Length > 1)
			list.Add(parts[^1]);

		if (Plugin.Config.EarshotFullName && parts.Length > 1)
			list.Add(full);

		foreach (var entry in Plugin.Config.EarshotCustom.Split(',')) {
			var trimmed = entry.Trim();
			if (trimmed.Length > 0)
				list.Add(trimmed);
		}

		return list;
	}

	private static SeString? Highlight(SeString source, List<string> triggers) {
		var payloads = new List<Payload>(source.Payloads.Count + 8);
		var touched = false;
		var key = Plugin.Config.EarshotHighlight;
		if (key == 0)
			key = ChatPalette.KeyFor(0);

		foreach (var payload in source.Payloads) {
			if (payload is not TextPayload text || text.Text is null || text.Text.Length == 0) {
				payloads.Add(payload);
				continue;
			}

			var body = text.Text;
			var at = 0;

			while (at < body.Length) {
				var found = FirstMatch(body, at, triggers, out var length);
				if (found < 0)
					break;

				if (found > at)
					payloads.Add(new TextPayload(body[at..found]));

				payloads.Add(new UIForegroundPayload(key));
				payloads.Add(new UIGlowPayload(key));
				payloads.Add(new TextPayload(body.Substring(found, length)));
				payloads.Add(UIGlowPayload.UIGlowOff);
				payloads.Add(new UIForegroundPayload(0));

				at = found + length;
				touched = true;
			}

			if (at == 0)
				payloads.Add(payload);
			else if (at < body.Length)
				payloads.Add(new TextPayload(body[at..]));
		}

		return touched ? new SeString(payloads) : null;
	}

	private static int FirstMatch(string body, int from, List<string> triggers, out int length) {
		var best = -1;
		length = 0;

		foreach (var trigger in triggers) {
			var at = IndexOfWord(body, trigger, from);
			if (at < 0)
				continue;

			if (best < 0 || at < best || (at == best && trigger.Length > length)) {
				best = at;
				length = trigger.Length;
			}
		}

		return best;
	}

	private static int IndexOfWord(string haystack, string needle, int from) {
		if (needle.Length == 0 || from >= haystack.Length)
			return -1;

		var at = from;
		while (at <= haystack.Length - needle.Length) {
			var found = haystack.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase);
			if (found < 0)
				return -1;

			var before = found == 0 || !char.IsLetterOrDigit(haystack[found - 1]);
			var afterAt = found + needle.Length;
			var after = afterAt >= haystack.Length || !char.IsLetterOrDigit(haystack[afterAt]);

			if (before && after)
				return found;

			at = found + 1;
		}

		return -1;
	}

	public void DrawTab() {
		var changed = false;
		var full = Plugin.Objects.LocalPlayer?.Name.TextValue ?? string.Empty;
		var parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);

		var first = Plugin.Config.EarshotFirstName;
		if (ImGui.Checkbox(parts.Length > 0 ? $"\"{parts[0]}\"" : "First name", ref first)) {
			Plugin.Config.EarshotFirstName = first;
			changed = true;
		}

		var last = Plugin.Config.EarshotLastName;
		if (ImGui.Checkbox(parts.Length > 1 ? $"\"{parts[^1]}\"" : "Last name", ref last)) {
			Plugin.Config.EarshotLastName = last;
			changed = true;
		}

		var whole = Plugin.Config.EarshotFullName;
		if (ImGui.Checkbox(parts.Length > 1 ? $"\"{full}\"" : "Full name", ref whole)) {
			Plugin.Config.EarshotFullName = whole;
			changed = true;
		}

		if (!first && !last && whole)
			ImGui.TextDisabled("Only your full name will set this off.");

		var custom = Plugin.Config.EarshotCustom;
		ImGui.SetNextItemWidth(-(ImGui.GetFontSize() * 11f));
		if (ImGui.InputTextWithHint("Also listen for", "nickname, another name", ref custom, 256)) {
			Plugin.Config.EarshotCustom = custom;
			changed = true;
		}

		ImGui.TextDisabled("Separated by commas. Anything with a space in it works as a phrase.");

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		var path = Plugin.Config.EarshotSoundPath;

		ImGui.SetNextItemWidth(-(ImGui.GetFontSize() * 11f));
		if (ImGui.BeginCombo("Sound",
				string.IsNullOrWhiteSpace(path) ? "The game's own"
				: string.Equals(path, BuiltInBell, StringComparison.OrdinalIgnoreCase) ? "Bell"
				: string.Equals(path, BuiltInSoft, StringComparison.OrdinalIgnoreCase) ? "Soft"
				: this.library.DisplayName(path))) {

			if (ImGui.Selectable("Bell", string.Equals(path, BuiltInBell, StringComparison.OrdinalIgnoreCase))) {
				Plugin.Config.EarshotSoundPath = BuiltInBell;
				this.sound.ForgetFailures();
				changed = true;
			}

			if (ImGui.Selectable("Soft", string.Equals(path, BuiltInSoft, StringComparison.OrdinalIgnoreCase))) {
				Plugin.Config.EarshotSoundPath = BuiltInSoft;
				this.sound.ForgetFailures();
				changed = true;
			}

			if (ImGui.Selectable("The game's own", string.IsNullOrWhiteSpace(path))) {
				Plugin.Config.EarshotSoundPath = string.Empty;
				changed = true;
			}

			foreach (var entry in this.library.Entries) {

				if (!entry.Personal)
					continue;

				if (ImGui.Selectable(entry.Name, string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))) {
					Plugin.Config.EarshotSoundPath = entry.Path;
					this.sound.ForgetFailures();
					changed = true;
				}
			}

			ImGui.EndCombo();
		}

		if (string.IsNullOrWhiteSpace(path)) {
			var which = Plugin.Config.EarshotSound;
			ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6f);
			if (ImGui.SliderInt("Which##earshot", ref which, 1, 16)) {
				Plugin.Config.EarshotSound = which;
				changed = true;
			}
		} else {
			var volume = Plugin.Config.EarshotVolume;
			ImGui.SetNextItemWidth(-(ImGui.GetFontSize() * 11f));
			if (ImGui.SliderFloat("Volume##earshot", ref volume, 0f, 1f, "%.2f")) {
				Plugin.Config.EarshotVolume = volume;
				changed = true;
			}

			ImGui.TextDisabled("Plays outside the game's mixer, so it ignores FFXIV's volume settings.");
		}

		if (ImGui.Button("Test##earshot"))
			this.PlayAlert();

		ImGui.SameLine();
		if (ImGui.Button("Open my sounds folder##earshot")) {
			try {
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
					FileName = this.library.PersonalFolder,
					UseShellExecute = true,
				});
			} catch (Exception ex) {
				Plugin.Log.Error(ex, "Earshot: could not open the sounds folder");
			}
		}

		ImGui.SameLine();
		if (ImGui.Button("Rescan##earshot"))
			this.library.Rescan();

		ImGui.TextDisabled("Only your own sounds are listed. Drop a file in that folder and it appears above.");

		ImGui.Spacing();

		var key = Plugin.Config.EarshotHighlight;
		if (key == 0)
			key = ChatPalette.KeyFor(0);

		var rgb = ChatPalette.RgbFor(key);
		if (ImGui.ColorButton("##earshotcol", new Vector4(rgb, 1f), ImGuiColorEditFlags.NoTooltip,
				new Vector2(26f, 22f)))
			ImGui.OpenPopup("earshotcol_pop");

		if (ImGui.BeginPopup("earshotcol_pop")) {
			var colours = ChatPalette.Colours;
			for (var i = 0; i < colours.Count; i++) {
				if (i % 8 != 0) ImGui.SameLine();

				var (candidate, colour) = colours[i];
				if (ImGui.ColorButton($"##nac{candidate}", new Vector4(colour, 1f),
						ImGuiColorEditFlags.NoTooltip, new Vector2(22f, 22f))) {
					Plugin.Config.EarshotHighlight = candidate;
					changed = true;
					ImGui.CloseCurrentPopup();
				}
			}

			ImGui.EndPopup();
		}

		ImGui.SameLine();
		ImGui.TextUnformatted("Mark colour");

		if (changed)
			Plugin.Config.Save();
	}
}
