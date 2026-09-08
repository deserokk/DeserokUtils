using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace DeserokUtils.Features.ChatColours;

internal sealed class ChatColourFeature: IDisposable {
	public string TabTitle => "Chat colours";

	public string Summary => "Colours player names in chat, and gives the same person the same colour every time.";

	private DateTime lastReArm = DateTime.MinValue;

	private static bool chatTwoCached;

	private static long chatTwoCheckedAt;

	private static bool ChatTwoLoaded {
		get {
			var now = Environment.TickCount64;
			if (now - chatTwoCheckedAt < 1000)
				return chatTwoCached;

			chatTwoCheckedAt = now;
			chatTwoCached = Plugin.PluginInterface.InstalledPlugins
				.Any(p => p.IsLoaded && string.Equals(p.InternalName, "ChatTwo", StringComparison.Ordinal));

			return chatTwoCached;
		}
	}

	private static readonly TimeSpan ReArmEvery = TimeSpan.FromSeconds(30);

	public ChatColourFeature() => Plugin.Chat.ChatMessage += this.OnChatMessage;

	public void Dispose() => Plugin.Chat.ChatMessage -= this.OnChatMessage;

	private void KeepLast() {
		var now = DateTime.UtcNow;
		if (now - this.lastReArm < ReArmEvery)
			return;

		this.lastReArm = now;

		Plugin.Chat.ChatMessage -= this.OnChatMessage;
		Plugin.Chat.ChatMessage += this.OnChatMessage;
	}

	private void OnChatMessage(IHandleableChatMessage message) {
		if (!Plugin.Config.ChatColours)
			return;

		try {

			if (Plugin.Verbose)
				Trace(message);

			if (Recolour(message.Sender) is { } sender)
				message.Sender = sender;

			if (Recolour(message.Message) is { } body)
				message.Message = body;

			this.KeepLast();
		}
		catch (Exception ex) {

			Plugin.Log.Error(ex, "ChatColours: rebuilding a line failed.");
		}
	}

	private static void Trace(IHandleableChatMessage message) {
		static string Shape(SeString s) => s.Payloads.Count == 0
			? "(none)"
			: string.Join(" ", s.Payloads.Select(p => p switch {
				PlayerPayload pp => $"Player<{pp.PlayerName}@{pp.World.ValueNullable?.Name.ExtractText() ?? "-"}>",
				TextPayload t => $"Text<{t.Text}>",
				_ => p.GetType().Name.Replace("Payload", string.Empty),
			}));

		SniffLog.Write($"chat {message.LogKind}: sender = {Shape(message.Sender)}");
	}

	private static SeString? Recolour(SeString source) {
		var payloads = new List<Payload>(source.Payloads.Count + 8);

		var pending = new List<Payload>(8);

		PlayerPayload? player = null;
		var touched = false;

		void Flush() {
			payloads.AddRange(pending);
			pending.Clear();
			player = null;
		}

		foreach (var payload in source.Payloads) {
			if (payload is PlayerPayload found) {
				Flush();
				player = found;
				pending.Add(payload);
				continue;
			}

			if (player is null) {

				if (Plugin.Config.ChatColourOwnName
					&& payload is TextPayload own
					&& own.Text is not null
					&& IsLocalPlayerName(own.Text.Trim())
					&& ColourFor(own.Text.Trim(), LocalWorld()) is { } mine) {

					payloads.Add(new UIForegroundPayload(mine));
					payloads.Add(payload);
					payloads.Add(new UIForegroundPayload(0));
					touched = true;
					continue;
				}

				payloads.Add(payload);
				continue;
			}

			if (payload is not TextPayload text || text.Text is null || text.Text.Length == 0) {
				pending.Add(payload);
				continue;
			}

			var isName = text.Text.Contains(player.PlayerName, StringComparison.Ordinal)
				|| text.Text.Trim().Contains(' ');

			if (!isName) {
				pending.Add(payload);
				Flush();
				continue;
			}

			if (ColourFor(player.PlayerName, WorldOf(player)) is not { } colour) {

				pending.Add(payload);
				Flush();
				continue;
			}

			if (ChatTwoLoaded) {

				payloads.AddRange(pending);
				pending.Clear();
				payloads.Add(new UIForegroundPayload(colour));
			} else {

				payloads.Add(new UIForegroundPayload(colour));
				payloads.AddRange(pending);
				pending.Clear();
			}

			payloads.Add(payload);

			payloads.Add(new UIForegroundPayload(0));

			touched = true;
			player = null;
		}

		Flush();

		return touched ? new SeString(payloads) : null;
	}

	private static ushort? ColourFor(string name, string world) {
		foreach (var over in Plugin.Config.ChatColourOverrides) {

			if (!string.Equals(over.Who.Trim(), name, StringComparison.OrdinalIgnoreCase))
				continue;

			if (over.Colour == 0)
				continue;

			var wanted = over.World.Trim();

			if (wanted.Length == 0 || string.Equals(wanted, world, StringComparison.OrdinalIgnoreCase))
				return over.Colour;
		}

		if (Plugin.Config.ChatColoursOnlyKnown)
			return null;

		unchecked {
			var hash = 2166136261u;
			foreach (var c in name)
				hash = (hash ^ c) * 16777619u;

			return ChatPalette.KeyFor((int)(hash % (uint)Math.Max(1, ChatPalette.Colours.Count)));
		}
	}

	private static HashSet<string>? worldNames;

	private static bool IsKnownWorld(string world) {
		var typed = world.Trim();
		if (typed.Length == 0)
			return true;

		if (worldNames is null) {
			worldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.World>();
			if (sheet is not null) {
				foreach (var row in sheet) {

					if (!row.IsPublic)
						continue;
					var name = row.Name.ExtractText();
					if (name.Length > 0)
						worldNames.Add(name);
				}
			}
		}

		return worldNames.Count == 0 || worldNames.Contains(typed);
	}

	private static bool IsLocalPlayerName(string text) {
		var me = Plugin.Objects.LocalPlayer?.Name.TextValue;
		return !string.IsNullOrEmpty(me) && string.Equals(text, me, StringComparison.Ordinal);
	}

	private static string LocalWorld()
		=> Plugin.Objects.LocalPlayer?.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;

	private static string WorldOf(PlayerPayload player) {
		var named = player.World.ValueNullable?.Name.ExtractText() ?? string.Empty;
		if (named.Length > 0)
			return named;

		return Plugin.Objects.LocalPlayer?.HomeWorld.ValueNullable?.Name.ExtractText()
			?? string.Empty;
	}

	public void DrawTab() {
		var s = 1f;
		var overrides = Plugin.Config.ChatColourOverrides;

		var only = Plugin.Config.ChatColoursOnlyKnown;
		if (ImGui.Checkbox("Only colour the people listed below", ref only)) {
			Plugin.Config.ChatColoursOnlyKnown = only;
			Plugin.Config.Save();
		}

		var own = Plugin.Config.ChatColourOwnName;
		if (ImGui.Checkbox("Colour my own name too", ref own)) {
			Plugin.Config.ChatColourOwnName = own;
			Plugin.Config.Save();
		}

		ImGui.Spacing();
		ImGui.TextDisabled(only
			? "Everybody else is left exactly as the game drew them."
			: "Everyone gets a colour. These people get one you picked.");

		ImGui.TextDisabled("Leave the world blank to match anyone with that name.");
		ImGui.TextDisabled("Click a colour to pick freely, or right-click one for the standard set.");
		ImGui.Spacing();

		var remove = -1;
		for (var i = 0; i < overrides.Count; i++) {
			var entry = overrides[i];

			ImGui.SetNextItemWidth(180f);
			var who = entry.Who;
			if (ImGui.InputTextWithHint($"##ccw{i}", "First Last", ref who, 48)) {
				entry.Who = who;
				Plugin.Config.Save();
			}

			ImGui.SameLine();
			ImGui.SetNextItemWidth(110f);
			var world = entry.World;
			if (ImGui.InputTextWithHint($"##ccworld{i}", "world", ref world, 32)) {
				entry.World = world;
				Plugin.Config.Save();
			}

			ImGui.SameLine();
			if (Swatch($"##ccc{i}", entry.Colour, out var picked)) {
				entry.Colour = picked;
				Plugin.Config.Save();
			}

			ImGui.SameLine();
			if (ImGui.SmallButton($"x##ccd{i}"))
				remove = i;

			if (entry.Colour == 0) {
				ImGui.SameLine();
				ImGui.TextDisabled("(no colour set)");
				if (ImGui.IsItemHovered())
					ImGui.SetTooltip("Pick a colour, or this row does nothing.");
			}

			if (!IsKnownWorld(entry.World)) {
				ImGui.SameLine();
				ImGui.TextDisabled("(unknown world)");
				if (ImGui.IsItemHovered())
					ImGui.SetTooltip(
						"No world by that name. This row will never match. Leave it blank to match "
						+ "anyone with this name.");
			}
		}

		if (remove >= 0) {
			overrides.RemoveAt(remove);
			Plugin.Config.Save();
		}

		if (ImGui.Button("+ Add a person##cc")) {
			overrides.Add(new ChatColourOverride { Colour = ChatPalette.KeyFor(overrides.Count) });
			Plugin.Config.Save();
		}

		_ = s;
	}

	private static readonly Dictionary<string, Vector3> WheelState = new();

	private static bool Swatch(string id, ushort current, out ushort picked) {
		picked = current;

		var raw = WheelState.TryGetValue(id, out var held) ? held : ChatPalette.RgbFor(current);

		if (ImGui.ColorEdit3(id, ref raw, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
			WheelState[id] = raw;

		if (ImGui.IsItemDeactivatedAfterEdit() && WheelState.TryGetValue(id, out var settled))
			WheelState[id] = ChatPalette.RgbFor(ChatPalette.NearestKey(settled));

		var landed = ChatPalette.NearestKey(raw);

		if (ImGui.BeginPopupContextItem($"{id}_quick")) {
			var quick = ChatPalette.Colours;
			for (var i = 0; i < quick.Count; i++) {
				if (i % 8 != 0) ImGui.SameLine();

				var (key, colour) = quick[i];
				if (ImGui.ColorButton($"{id}_q{key}", new Vector4(colour, 1f),
						ImGuiColorEditFlags.NoTooltip, new Vector2(22f, 22f))) {

					WheelState[id] = colour;
					landed = key;
					ImGui.CloseCurrentPopup();
				}
			}

			ImGui.EndPopup();
		}

		if (landed == current)
			return false;

		picked = landed;
		return true;
	}

	public void DrawDiagnostics() {
		var colours = ChatPalette.Colours;
		ImGui.TextDisabled($"palette: {colours.Count} colours derived from the UIColor sheet");

		for (var i = 0; i < colours.Count; i++) {
			if (i % 8 != 0) ImGui.SameLine();
			var (key, rgb) = colours[i];
			ImGui.ColorButton($"##pal{key}", new Vector4(rgb, 1f), ImGuiColorEditFlags.None, new Vector2(22f, 22f));
		}
	}
}
