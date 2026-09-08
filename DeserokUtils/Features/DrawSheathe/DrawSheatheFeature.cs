using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Command;

using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DeserokUtils.Features.DrawSheathe;

internal sealed class DrawSheatheFeature: IDisposable {
	public string TabTitle => "DrawSheathe";

	public string Summary => "One key that draws or sheathes, whichever is currently correct, with the Gold Saucer emotes where they work.";

	private readonly WeaponStateSniffer sniffer = new();

	private DateTime lastPressAt = DateTime.MinValue;

	public DrawSheatheFeature() {
		Plugin.RegisterSub("draw", "Draw or sheathe, whichever is currently correct.", this.OnDrawSheathe);
	}

	internal static bool? WeaponIsOut() {
		var player = Plugin.Objects.LocalPlayer;
		if (player is null)
			return null;
		return player.StatusFlags.HasFlag(StatusFlags.WeaponOut);
	}

	internal static unsafe bool? ClientSaysUnsheathed() {
		UIState* ui = UIState.Instance();
		return ui is null ? null : ui->WeaponState.IsUnsheathed;
	}

	internal static unsafe bool? PlayerIsMoving() {
		AgentMap* map = AgentMap.Instance();
		return map is null ? null : map->IsPlayerMoving;
	}

	internal static unsafe float? SheatheCooldown() {
		UIState* ui = UIState.Instance();
		return ui is null ? null : ui->WeaponState.SheatheCooldown;
	}

	private string? PressTooSoonBecause() {
		DateTime now = DateTime.UtcNow;
		TimeSpan sinceLastPress = now - this.lastPressAt;
		this.lastPressAt = now;

		int windowMs = RepeatCollapseMs;
		if (windowMs > 0 && sinceLastPress < TimeSpan.FromMilliseconds(windowMs))
			return $"key repeat ({sinceLastPress.TotalMilliseconds:0}ms gap, collapsing anything under {windowMs}ms)";

		float? cooldown = SheatheCooldown();
		if (cooldown > 0f)
			return $"the game's sheathe cooldown ({cooldown.Value:0.###}s left)";

		return null;
	}

	internal static string CommandFor(bool weaponOut) =>

		(weaponOut ? Configuration.DefaultSheatheCommand : Configuration.DefaultDrawCommand).Trim();

	internal static string? EmoteRefusedBecause(string? command = null) {

		if (command is not null && EmoteUnlock.LockedBecause(command) is string locked)
			return locked;

		if (PlayerIsMoving() == true)
			return "moving";

		if (Plugin.Condition[ConditionFlag.Jumping] || Plugin.Condition[ConditionFlag.Jumping61])
			return "jumping";

		return null;
	}

	public void Press() => this.OnDrawSheathe("/drawsheathe", string.Empty);

	private void OnDrawSheathe(string command, string arguments) {
		string arg = arguments.Trim().ToLowerInvariant();
		bool? weaponOut = WeaponIsOut();
		string? refused = EmoteRefusedBecause(
			weaponOut is null ? null : CommandFor(weaponOut.Value));

		if (arg is "state" or "status" or "?") {
			this.ReportState(weaponOut, refused);
			return;
		}

		if (arg is "conditions" or "cond" or "flags") {
			DumpConditions();
			return;
		}

		if (arg.StartsWith("sniff", StringComparison.Ordinal) || arg == "record") {
			this.OnSniff(arg.StartsWith("sniff", StringComparison.Ordinal) ? arg["sniff".Length..].Trim() : string.Empty);
			return;
		}

		if (arg.Length > 0) {
			Plugin.Chat.PrintError($"[DrawSheathe] unknown argument \"{arg}\". Use /drawsheathe, or /drawsheathe state.");
			return;
		}

		if (weaponOut is null) {
			Plugin.Chat.PrintError("[DrawSheathe] could not read your weapon state (no local player). Nothing sent.");
			return;
		}

		string? tooSoon = this.PressTooSoonBecause();
		if (tooSoon is not null) {
			Trace($"press ignored: {tooSoon}");
			return;
		}

		if (refused is not null) {
			this.FastToggle(weaponOut.Value, refused);
			return;
		}

		this.PlayEmote(weaponOut.Value, refused);
	}

	private static void DumpConditions() {
		var set = new System.Collections.Generic.List<string>();
		foreach (ConditionFlag flag in Enum.GetValues<ConditionFlag>()) {
			if (Plugin.Condition[flag])
				set.Add($"{flag} ({(int)flag})");
		}

		string moving = Show(PlayerIsMoving());
		string cooldown = Show(SheatheCooldown());
		Plugin.Chat.Print($"[DrawSheathe] IsPlayerMoving={moving}, SheatheCooldown={cooldown}; conditions set: "
			+ (set.Count > 0 ? string.Join(", ", set) : "none"));
		Plugin.Log.Information($"DrawSheathe conditions: moving={moving} cooldown={cooldown}; {string.Join(", ", set)}");
	}

	private void PlayEmote(bool weaponOut, string? refused) {
		string line = CommandFor(weaponOut);
		if (line.Length == 0) {

			Plugin.Chat.PrintError(
				$"[DrawSheathe] the {(weaponOut ? "sheathe" : "draw")} command is blank in the DrawSheathe tab. Nothing sent.");
			return;
		}

		Trace($"weapon {(weaponOut ? "OUT" : "away")}, refused={refused ?? "no"} -> emote: {line}"
			+ $" | cooldown={Show(SheatheCooldown())}");
		GameCommands.Queue(line);
	}

	private unsafe void FastToggle(bool weaponOut, string? refused) {
		UIState* ui = UIState.Instance();
		if (ui is null) {

			Plugin.Log.Warning("DrawSheathe: UIState was null; falling back to the emote.");
			Trace("UIState unavailable -> fell back to the emote path.");
			this.PlayEmote(weaponOut, refused);
			return;
		}

		float before = ui->WeaponState.SheatheCooldown;
		bool ok = ui->WeaponState.SetUnsheathed(!weaponOut, true, true);
		Trace($"weapon {(weaponOut ? "OUT" : "away")}, refused={refused ?? "no"} -> default toggle"
			+ $" SetUnsheathed({!weaponOut}, sendPacket: true, isInstant: true) returned {ok}"
			+ $" | cooldown {before:0.###} -> {ui->WeaponState.SheatheCooldown:0.###}");

		if (!ok)
			Plugin.Log.Information($"DrawSheathe: SetUnsheathed({!weaponOut}) returned false -- the client refused it.");
	}

	private void OnSniff(string rest) {
		if (rest is "off" or "stop") {
			if (this.sniffer.Armed)
				this.sniffer.Disarm();
			else
				Plugin.Chat.Print("[DrawSheathe] the sniffer was not running.");
			return;
		}

		if (!this.sniffer.Available) {
			Plugin.Chat.PrintError("[DrawSheathe] neither WeaponState function could be resolved -- nothing to hook. See /xllog.");
			return;
		}

		TimeSpan duration = WeaponStateSniffer.DefaultDuration;
		if (rest.Length > 0) {
			if (!int.TryParse(rest, out int seconds) || seconds <= 0) {
				Plugin.Chat.PrintError($"[DrawSheathe] \"{rest}\" is not a number of seconds. Try /drawsheathe sniff 60.");
				return;
			}
			duration = TimeSpan.FromSeconds(seconds);
		}

		this.sniffer.Arm(duration);
		Plugin.Chat.Print(
			$"[DrawSheathe] sniffer ON for {duration.TotalSeconds:0}s. Now press YOUR OWN draw/sheathe keybind "
			+ "(not this command) while standing still, then again while moving. /drawsheathe sniff off to stop.");
	}

	private void ReportState(bool? weaponOut, string? refused) {
		if (weaponOut is null) {
			Plugin.Chat.Print("[DrawSheathe] no local player to read.");
			return;
		}

		bool fast = refused is not null;
		string verb = weaponOut.Value ? "sheathe" : "draw";
		string how = fast ? $"the game's own toggle (you are {refused})" : "the emote";
		Plugin.Chat.Print(
			$"[DrawSheathe] weapon is {(weaponOut.Value ? "OUT" : "AWAY")}"
			+ $"{(refused is null ? "" : $", {refused}")} -- a press would {verb} using {how}.");
	}

	private static string Show(bool? value) => value switch {
		true => "yes",
		false => "no",
		null => "unreadable",
	};

	private static string Show(float? value) => value is null ? "unreadable" : $"{value.Value:0.###}";

	private static void Trace(string message) {
		Plugin.Log.Information($"DrawSheathe: {message}");
		Plugin.Diag(message);
	}

	private const int RepeatCollapseMs = 250;

	public void DrawDiagnostics() {

		this.sniffer.ExpireIfDue();

		bool? weaponOut = WeaponIsOut();
		string? refused = EmoteRefusedBecause(
			weaponOut is null ? null : CommandFor(weaponOut.Value));

		Section("Right now");
		switch (weaponOut) {
			case true:
				ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "weapon is OUT");
				break;
			case false:
				ImGui.Text("weapon is away");
				break;
			default:
				ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), "no local player to read");
				break;
		}

		if (weaponOut is not null) {
			ImGui.SameLine();
			ImGui.TextDisabled(
				$"-- {refused ?? "still"}, so a press would {(weaponOut.Value ? "sheathe" : "draw")}"
				+ $" using {(refused is not null ? "the game's toggle" : "the emote")}");

			bool? client = ClientSaysUnsheathed();
			if (client is not null && client != weaponOut)
				ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f),
					$"⚠ the client's own WeaponState.IsUnsheathed says {client} -- these disagree.");

			ImGui.TextDisabled($"game's SheatheCooldown: {Show(SheatheCooldown())}");
		}

		Section("Sniffer");
		ImGui.TextWrapped(
			"Records what the client actually calls when you press your own draw/sheathe keybind.");
		ImGui.Spacing();

		if (this.sniffer.Armed) {
			ImGui.TextColored(
				new Vector4(0.4f, 1f, 0.4f, 1f), $"recording -- {this.sniffer.Remaining.TotalSeconds:0}s left");
			ImGui.SameLine();
			if (ImGui.Button("Stop##ds_sniff"))
				this.sniffer.Disarm();
		}
		else if (!this.sniffer.Available) {
			ImGui.TextColored(
				new Vector4(1f, 0.4f, 0.4f, 1f),
				"neither WeaponState function resolved -- nothing to hook. See /xllog.");
		}
		else {
			if (ImGui.Button("Record for 60s##ds_sniff"))
				this.sniffer.Arm(WeaponStateSniffer.DefaultDuration);
			ImGui.SameLine();
			ImGui.TextDisabled("or /drawsheathe sniff");
		}
	}

	private static void Section(string title) {
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextDisabled(title);
		ImGui.Spacing();
	}

	public void Dispose() {
		this.sniffer.Dispose();
	}
}
