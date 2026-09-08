using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace DeserokUtils.Features.Interact;

internal sealed class InteractFeature: IDisposable {
	public string TabTitle => "Interact";

	public string Summary => "A key that operates the thing in front of you and nothing else — never a menu, never a cursor.";

	private readonly InteractSniffer sniffer = new();

	private readonly GimmickConfirm gimmicks = new();

	public InteractFeature() {
		Plugin.Chat.ChatMessage += OnChatMessage;

		Plugin.RegisterSub("interact", "Operate the thing in front of you, ignoring open menus.", this.OnCommand);
	}

	private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(800);

	private DateTime lastInteract = DateTime.MinValue;
	private string lastResult = "nothing yet";

	private static void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message) {
		if (PillionRider.SwallowRefusal(message.LogKind))
			message.PreventOriginal();
	}

	private void OnCommand(string command, string arguments) {
		string arg = arguments.Trim().ToLowerInvariant();

		if (arg.StartsWith("sniff", StringComparison.Ordinal)) {
			this.OnSniff(arg["sniff".Length..].Trim());
			return;
		}

		if (arg is "why" or "probe") {
			this.OnWhy();
			return;
		}

		if (arg == "pillion") {
			ProbePillion();
			return;
		}

		if (arg.Length > 0) {
			Plugin.Chat.PrintError($"[Interact] unknown argument \"{arg}\". Use /dsuinteract, why, sniff, or pillion.");
			return;
		}

		this.DoInteract();
	}

	private unsafe void DoInteract() {
		var player = Plugin.Objects.LocalPlayer;
		if (player is null) {
			Plugin.Chat.PrintError("[Interact] no local player.");
			return;
		}

		if (Plugin.Config.InteractAdvanceTalk && TalkAdvance.TryAdvance()) {

			this.lastInteract = DateTime.UtcNow;
			Plugin.Diag("Interact: advanced dialogue");
			return;
		}

		if (player.IsCasting) {
			Trace("press ignored: already casting (an interaction is in progress)");
			return;
		}
		if (DateTime.UtcNow - this.lastInteract < Floor) {
			Trace($"press ignored: within {Floor.TotalSeconds:0}s of the last interact");
			return;
		}

		var (chosen, how) = Choose(player);
		if (chosen is null) {

			if (Plugin.Config.InteractRidePillion && PillionRider.TryRide())
				return;

			Trace("nothing interactable nearby");
			return;
		}

		string before = Plugin.Targets.Target?.Name.ToString() ?? "none";

		if (chosen.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.GatheringPoint
		    && Plugin.Targets.Target?.Address != chosen.Address) {
			Plugin.Targets.Target = chosen;
			Trace($"targeted \"{chosen.Name}\" first -- gathering nodes need it");
		}

		this.gimmicks.Arm();

		ulong result = TargetSystem.Instance()->InteractWithObject(
			(FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)chosen.Address, true);
		this.lastInteract = DateTime.UtcNow;

		string after = Plugin.Targets.Target?.Name.ToString() ?? "none";
		this.lastResult = $"{chosen.Name} ({how})";

		Trace($"interacted with \"{chosen.Name}\" kind={chosen.ObjectKind} data={chosen.BaseId} "
			+ $"at {Vector3.Distance(chosen.Position, player.Position):0.#}y via {how} -> {result} "
			+ $"| target {before} -> {after}");
	}

	private static (Dalamud.Game.ClientState.Objects.Types.IGameObject? Object, string How) Choose(
		Dalamud.Game.ClientState.Objects.Types.IGameObject player) {

		var soft = Plugin.Targets.SoftTarget;
		if (soft is not null && Interactable(soft))
			return (soft, "soft target");

		var hard = Plugin.Targets.Target;
		if (hard is not null && Interactable(hard))
			return (hard, "current target");

		Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
		float bestDistance = float.MaxValue;

		foreach (var candidate in Plugin.Objects) {
			float distance = System.Numerics.Vector3.Distance(candidate.Position, player.Position);
			if (distance > Reach)
				continue;
			if (!Interactable(candidate, out string why)) {

				Plugin.Diag($"Interact: ignoring \"{candidate.Name}\" kind={candidate.ObjectKind} "
					+ $"at {distance:0.#}y -- {why}");
				continue;
			}
			if (distance < bestDistance) {
				best = candidate;
				bestDistance = distance;
			}
		}

		return best is null ? (null, "nothing") : (best, $"nearest at {bestDistance:0.#}y");
	}

	private const float Reach = 6f;

	private static bool Interactable(Dalamud.Game.ClientState.Objects.Types.IGameObject obj) => Interactable(obj, out _);

	private static bool Interactable(Dalamud.Game.ClientState.Objects.Types.IGameObject obj, out string why) {
		if (obj.ObjectKind is not (Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj
			or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure
			or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte
			or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
			or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.GatheringPoint)) {
			why = $"kind {obj.ObjectKind}";
			return false;
		}

		if (!obj.IsTargetable) {
			why = "not targetable";
			return false;
		}

		if (obj.Name.TextValue.Length == 0) {
			why = "no name";
			return false;
		}

		why = string.Empty;
		return true;
	}

	private unsafe void OnWhy() {
		var player = Plugin.Objects.LocalPlayer;
		if (player is null) {
			Plugin.Chat.PrintError("[Interact] no local player.");
			return;
		}

		var ts = TargetSystem.Instance();
		Plugin.Chat.Print($"[Interact] soft={Plugin.Targets.SoftTarget?.Name.ToString() ?? "none"} "
			+ $"hard={Plugin.Targets.Target?.Name.ToString() ?? "none"}");

		int seen = 0;
		foreach (var candidate in Plugin.Objects) {
			float distance = Vector3.Distance(candidate.Position, player.Position);
			if (distance > Reach)
				continue;

			seen++;
			var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)candidate.Address;

			Plugin.Chat.Print($"  \"{candidate.Name}\" {candidate.ObjectKind} {distance:0.#}y "
				+ $"{(Interactable(candidate, out string why) ? "usable" : "IGNORED: " + why)} "
				+ $"data={candidate.BaseId} "
				+ $"view={ts->IsObjectInViewRange(go)} screen={ts->IsObjectOnScreen(go)} "
				+ $"targetable={candidate.IsTargetable}");
		}

		if (seen == 0)
			Plugin.Chat.Print("  nothing within reach");

		var (chosen, how) = Choose(player);
		Plugin.Chat.Print($"[Interact] would use: {(chosen is null ? "nothing" : $"\"{chosen.Name}\" via {how}")}");
	}

	public void Press() => this.DoInteract();

	public void Tick() {
		this.gimmicks.Tick();

		this.sniffer.ExpireIfDue();
	}

	private static void Trace(string message) {
		Plugin.Log.Information($"Interact: {message}");
		Plugin.Diag($"Interact: {message}");
	}

	private void OnSniff(string rest) {
		if (rest is "off" or "stop") {
			if (this.sniffer.Armed)
				this.sniffer.Disarm();
			else
				Plugin.Chat.Print("[Interact] the recorder was not running.");
			return;
		}

		if (!this.sniffer.Available) {
			Plugin.Chat.PrintError("[Interact] neither TargetSystem function resolved -- nothing to hook. See /xllog.");
			return;
		}

		TimeSpan duration = InteractSniffer.DefaultDuration;
		if (rest.Length > 0) {
			if (!int.TryParse(rest, out int minutes) || minutes <= 0) {
				Plugin.Chat.PrintError($"[Interact] \"{rest}\" is not a number of minutes. Try /dsuinteract sniff 20.");
				return;
			}
			duration = TimeSpan.FromMinutes(minutes);
		}

		this.sniffer.Arm(duration);
		Plugin.Chat.Print($"[Interact] recording for {duration.TotalMinutes:0} min. Go operate things -- levers, "
			+ "keys on the floor, wheels, aetherytes. Both presses of Confirm are logged.");
	}

	public void DrawTab() {
		bool answer = Plugin.Config.InteractAnswerGimmicks;
		if (ImGui.Checkbox("Auto-confirm interaction confirmation prompts##interact_gimmick", ref answer)) {
			Plugin.Config.InteractAnswerGimmicks = answer;
			Plugin.Config.Save();
		}

		bool ride = Plugin.Config.InteractRidePillion;
		if (ImGui.Checkbox("Auto pillion if no interactables nearby##interact_pillion", ref ride)) {
			Plugin.Config.InteractRidePillion = ride;
			Plugin.Config.Save();
		}

		bool talk = Plugin.Config.InteractAdvanceTalk;
		if (ImGui.Checkbox("Advance NPC dialogue##interact_talk", ref talk)) {
			Plugin.Config.InteractAdvanceTalk = talk;
			Plugin.Config.Save();
		}
	}

	private static void ProbePillion() {
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.TextCommand>();
		if (sheet is null) {
			Plugin.Chat.PrintError("[Interact] no TextCommand sheet.");
			return;
		}

		var found = 0;
		foreach (var row in sheet) {
			var command = row.Command.ExtractText();
			var alias = row.Alias.ExtractText();

			if (!command.Contains("pillion", StringComparison.OrdinalIgnoreCase)
				&& !alias.Contains("pillion", StringComparison.OrdinalIgnoreCase))
				continue;

			found++;
			var line = $"[Interact] row {row.RowId}: {command} | alias {alias}"
				+ $" | short {row.ShortCommand.ExtractText()}/{row.ShortAlias.ExtractText()}";

			Plugin.Chat.Print(line);
			SniffLog.Write(line);

			var description = row.Description.ExtractText();
			if (description.Length > 0) {
				Plugin.Chat.Print($"    {description}");
				SniffLog.Write($"    {description}");
			}
		}

		if (found == 0)
			Plugin.Chat.PrintError("[Interact] nothing in TextCommand mentions pillion.");
	}

	public void DrawDiagnostics() {
		ImGui.TextDisabled($"last press: {this.lastResult}");
		ImGui.TextDisabled($"last prompt answered: {this.gimmicks.LastAnswer}");
		ImGui.TextDisabled($"prompts the game lists: {this.gimmicks.KnownPrompts}");
	}

	public void Dispose() {
		Plugin.Chat.ChatMessage -= OnChatMessage;

		this.sniffer.Dispose();
		this.gimmicks.Dispose();
	}
}
