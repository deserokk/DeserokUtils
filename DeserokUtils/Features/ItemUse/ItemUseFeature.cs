using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DeserokUtils.Features.ItemUse;

internal sealed unsafe class ItemUseFeature: IDisposable {
	public string SectionTitle => "Items";
	public string Summary => "/dsuitem -- use an item on a target from a macro. The game has no command for this at all; combine with /ifmo for mouseover.";

	private const uint FromAnywhere = 0xFFFF;

	public const ulong NoTarget = 0xE000_0000;

	private static string lastDecision = "nothing yet";

	public ItemUseFeature() {

		Plugin.Commands.AddHandler("/dsuitem", new CommandInfo(this.OnItem) {
			HelpMessage = "Use an item on a target from a macro.",

			ShowInHelp = false,
		});

		System.Threading.Tasks.Task.Run(() => {
			try {
				ItemLookup.Warm();
			}
			catch (Exception ex) {

				Plugin.Log.Error(ex, "ItemUse: warming the item name map failed.");
			}
		});
	}

	public readonly record struct Stack(uint UseId, bool Hq, int Count);

	public static Stack? PickStack(uint itemId) {
		var inventory = InventoryManager.Instance();
		if (inventory is null)
			return null;

		int nq = inventory->GetInventoryItemCount(itemId, false, false, false, 0);
		if (nq > 0)
			return new Stack(itemId, false, nq);

		int hq = inventory->GetInventoryItemCount(itemId, true, false, false, 0);
		if (hq > 0)
			return new Stack(itemId + ItemLookup.HqOffset, true, hq);

		return null;
	}

	public static uint Status(uint useId, ulong targetId) {
		var manager = ActionManager.Instance();
		return manager is null
			? 0
			: manager->GetActionStatus(ActionType.Item, useId, targetId, false, false, null);
	}

	public static bool Send(uint useId, ulong targetId) {
		var manager = ActionManager.Instance();
		if (manager is null) {
			Plugin.Log.Error("ItemUse: ActionManager was null; nothing sent.");
			return false;
		}

		return manager->UseAction(ActionType.Item, useId, targetId, FromAnywhere, ActionManager.UseActionMode.None, 0, null);
	}

	public static string Reason(uint status) {
		if (status == 0)
			return "fine";
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>();
		string text = sheet?.GetRowOrDefault(status)?.Text.ExtractText().Trim() ?? string.Empty;
		return text.Length > 0 ? text : $"status {status}";
	}

	public static ulong ResolvePlaceholder(string segment) {
		var pronoun = PronounModule.Instance();
		if (pronoun is null)
			return 0;

		var resolved = pronoun->ResolvePlaceholder($"<{segment.Trim('<', '>')}>", 0, 0, false);
		return resolved is null ? 0 : resolved->GetGameObjectId();
	}

	public static string NameOf(ulong id) {
		if (id is 0 or NoTarget)
			return "nobody";
		foreach (var obj in Plugin.Objects) {
			if (obj.GameObjectId == id)
				return obj.Name.ToString();
		}
		return "someone";
	}

	public static void Decided(string what) {
		lastDecision = what;
		Plugin.Log.Information($"/item: {what}");
		Plugin.Diag($"/item: {what}");
	}

	private void OnItem(string command, string arguments) {
		string payload = arguments.Trim();
		if (payload.Length == 0) {
			Plugin.Chat.PrintError("[ItemUse] nothing to use. Usage: /dsuitem \"Phoenix Down\" <mo>");
			return;
		}

		(string name, string? placeholder) = ParseArgs(payload);

		uint? itemId = ItemLookup.Resolve(name);
		if (itemId is null) {

			Plugin.Chat.PrintError($"[ItemUse] \"{name}\" is not an item you can use.{ItemLookup.SuggestionFor(name)}");
			return;
		}

		var stack = PickStack(itemId.Value);
		if (stack is null) {

			Plugin.Chat.PrintError($"[ItemUse] you are not carrying any {name}.");
			Decided($"no {name} in the bags");
			return;
		}

		ulong target;
		if (placeholder is null) {
			target = Plugin.Targets.Target?.GameObjectId ?? NoTarget;
		}
		else {
			target = ResolvePlaceholder(placeholder);
			if (target == 0) {

				Decided($"<{placeholder.Trim('<', '>')}> resolves to nobody -- nothing sent");
				return;
			}
		}

		uint status = Status(stack.Value.UseId, target);
		bool sent = Send(stack.Value.UseId, target);

		Decided($"{name}{(stack.Value.Hq ? " (HQ)" : "")} x{stack.Value.Count} on {NameOf(target)}"
			+ $" -- status {status} \"{Reason(status)}\", UseAction returned {sent}");
	}

	private static (string Name, string? Placeholder) ParseArgs(string payload) {
		if (payload[0] is '"' or '\'') {
			char quote = payload[0];
			int close = payload.IndexOf(quote, 1);
			if (close > 1) {
				string rest = payload[(close + 1)..].Trim();
				return (payload[1..close], rest.Length > 0 ? rest : null);
			}
		}

		int angle = payload.IndexOf('<');
		if (angle > 0)
			return (payload[..angle].TrimEnd(), payload[angle..].Trim());

		return (payload, null);
	}

	public void DrawSection() {
		ImGui.TextWrapped(
			"Use an item on a target from a macro. The game has no command that does this -- there is "
			+ "no /item and no /use in any of its 541 text commands -- even though an item dragged onto "
			+ "a hotbar uses on your target perfectly well.");
		ImGui.Spacing();

		foreach (string template in new[] {
			"/dsuitem \"Phoenix Down\" <t>",
			"/ifmo /dsuitem \"Phoenix Down\" {mo|t}",
			"/dsuitem \"Hi-Potion\"",
		}) {
			ImGui.TextUnformatted(template);
			ImGui.SameLine();
			if (ImGui.Button($"Copy##item{template.Length}"))
				ImGui.SetClipboardText(template);
		}

		ImGui.Spacing();
		ImGui.TextWrapped(
			"Any placeholder the game understands works -- <t>, <mo>, <2>, <me> -- and it is resolved "
			+ "by the game's own resolver, so party frames and alliance lists count as a mouseover. "
			+ "Leave the placeholder off to use on your current target, which is what a hotbar press does.");

		ImGui.Spacing();
		ImGui.TextWrapped(
			"Wrap it in /ifmo to get the chain: /ifmo /item \"Phoenix Down\" {mo|t} checks whether the "
			+ "item would actually work on your mouseover, falls back to your target if it would not, "
			+ "and sends exactly one attempt.");

		ImGui.Spacing();
		ImGui.TextWrapped(
			"NQ is spent before HQ. Phoenix Downs have no HQ, but potions do, and a macro that quietly "
			+ "reached for the better stack would cost money every press.");

		ImGui.Spacing();
		ImGui.TextWrapped(
			"/watch and /ifwatch work across this unchanged -- the use goes through the same engine "
			+ "call a hotbar press does, so CastWatch sees it and {who} names whoever received it.");

		ImGui.Spacing();
		ImGui.TextDisabled($"last press: {lastDecision}");

		ImGui.Spacing();
		ImGui.TextWrapped(
			"Inside /ifmo, /item and /useitem are read as the same thing -- that line is never handed to "
			+ "the game, so nothing can collide with it. On its own, the command is /dsuitem.");
	}

	public void Dispose() => Plugin.Commands.RemoveHandler("/dsuitem");
}
