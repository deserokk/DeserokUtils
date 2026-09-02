using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DeserokUtils.Features.ItemUse;

/// <summary>
/// Use an item on a target from a macro -- <c>/item "Phoenix Down" &lt;mo&gt;</c> -- which the game
/// itself cannot do at all.
///
/// ## ⚠⚠ This is NOT parity. The game has no item command whatsoever.
///
/// Measured 2026-09-02 by walking the game's own TextCommand sheet: **541 text commands, and not one
/// of them uses an item.** No <c>/item</c>, no <c>/use</c>, no alias hiding under another name.
/// <c>/action</c> documents <c>"action name" [placeholder]</c> and there is simply no counterpart.
///
/// ⭐ Which is the odd part, and the reason this is safe to build: the engine has no such gap. Drag a
/// Phoenix Down to a hotbar and one keypress uses it on your target, because
/// <c>ActionManager.UseAction</c> takes an <c>ActionType</c> and <c>Item</c> is one of its values.
/// Everything needed is already there and reachable; only the text command to reach it from is
/// missing. So this adds the missing doorway, not a new room.
///
/// ⚠ Therefore, unlike <see cref="IfMouseover.IfMouseoverFeature"/>, there is no line to hand back to
/// the game. /ifmo decides a placeholder and lets the chatbox pipeline run the result; here the
/// pipeline would reject the line outright, so we resolve the target and call the engine ourselves.
///
/// ⭐⭐ CastWatch still works across this, unchanged, and that is not luck: its hook sits on
/// <c>ActionManager.UseAction</c>, so a use we initiate walks through the same detour a hotbar press
/// does. <c>/watch Phoenix Down</c> already resolved items and already normalised HQ. So the raise
/// macro composes exactly as the Raise one does:
///
/// <code>
/// /watch Phoenix Down
/// /ifmo /item "Phoenix Down" {mo|t}
/// /wait 1
/// /ifwatch /p Raising {who}
/// </code>
/// </summary>
internal sealed unsafe class ItemUseFeature: IDisposable {
	public string SectionTitle => "Items";
	public string Summary => "/item -- use an item on a target from a macro. The game has no command for this at all; combine with /ifmo for mouseover.";

	/// <summary>
	/// What <c>UseAction</c> wants for "I did not name an inventory slot, treat it like a hotbar
	/// press". From the ClientStructs docs on the parameter, not from a guess.
	/// </summary>
	private const uint FromAnywhere = 0xFFFF;

	/// <summary>The id UseAction reports for "no target", and what we pass when nobody was named --
	/// so a potion self-uses exactly as pressing it on the hotbar would.</summary>
	public const ulong NoTarget = 0xE000_0000;

	/// <summary>Last decision, for the tab. Purely diagnostic.</summary>
	private static string lastDecision = "nothing yet";

	/// <summary>⚠ Whether the plugin managed to claim <c>/item</c>. It is free in the vanilla game,
	/// but another plugin may already have taken it, and Dalamud reports that by returning false
	/// rather than throwing -- so a silent failure here would look like "the command does nothing".</summary>
	private readonly bool claimedItem;

	public ItemUseFeature() {
		this.claimedItem = Plugin.Commands.AddHandler("/item", new CommandInfo(this.OnItem) {
			HelpMessage = "/item \"Phoenix Down\" <mo> -- use an item on a target. The game has no such command; this adds it.",
		});
		if (!this.claimedItem) {
			Plugin.Log.Warning("ItemUse: /item was already registered by another plugin; /dsuitem still works.");
			Plugin.Chat.PrintError("[DeserokUtils] /item is taken by another plugin -- use /dsuitem instead.");
		}

		// ⚠ Always registered, even when /item was claimed successfully. The prefixed name is the one
		// that cannot be stolen by a plugin load order, so a macro written against it keeps working.
		Plugin.Commands.AddHandler("/dsuitem", new CommandInfo(this.OnItem) {
			HelpMessage = "/dsuitem \"Phoenix Down\" <mo> -- same as /item, under a name no other plugin can claim.",
		});

		// ⚠ Off the main thread, at load, because the macro icon hook may ask for a name before any
		// macro is ever pressed -- and it asks from the UI, where the sheet walk would be a stutter.
		System.Threading.Tasks.Task.Run(() => {
			try {
				ItemLookup.Warm();
			}
			catch (Exception ex) {
				// Report rather than swallow: a failed warm-up is not fatal (the next caller rebuilds)
				// but a silent one turns into "why is the first press slow" with nothing to read.
				Plugin.Log.Error(ex, "ItemUse: warming the item name map failed.");
			}
		});
	}

	// ── the engine calls, shared with /ifmo ──────────────────────────────────────────────────

	/// <summary>Which stack of an item to use, and how many of it you are carrying.</summary>
	/// <param name="UseId">The id to hand UseAction -- the row id, or row id + 1,000,000 for HQ.</param>
	/// <param name="Hq">Which stack this is, for the log line. The id already carries the offset.</param>
	/// <param name="Count">How many you are carrying of it, so "one left" is visible in diagnostics.</param>
	public readonly record struct Stack(uint UseId, bool Hq, int Count);

	/// <summary>
	/// Pick the stack to spend, NQ first.
	///
	/// ⚠ NQ FIRST IS DELIBERATE and it is not symmetry: an HQ potion is worth more than the NQ one
	/// beside it, so a macro that quietly reaches for the better stack costs money every press. Phoenix
	/// Downs have no HQ at all, so the case that motivated this never sees the branch -- it is there
	/// for the potion macro somebody writes next.
	///
	/// ⚠ Returns null when you are carrying neither, which is a different answer from "the game
	/// refused it" and is reported as such.
	/// </summary>
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

	/// <summary>
	/// Would this item work on that target, right now.
	///
	/// ⭐ The item-shaped counterpart to /ifmo's pair of checks, and it is ONE call rather than two:
	/// <c>CanUseActionOnTarget</c> and <c>GetActionInRangeOrLoS</c> are both action-only -- neither
	/// takes an <c>ActionType</c> -- while <c>GetActionStatus</c> does, and answers legality and reach
	/// together. Zero means fine; anything else is a LogMessage row id, the same vocabulary the range
	/// code speaks.
	///
	/// ⚠ <c>checkRecastActive: false</c> is the important argument. Letting the cooldown into the
	/// answer would reintroduce the GCD race that /ifmo exists to remove -- a press during the GCD
	/// would read as "this target will not work" and fall through to the wrong one.
	/// </summary>
	public static uint Status(uint useId, ulong targetId) {
		var manager = ActionManager.Instance();
		return manager is null
			? 0
			: manager->GetActionStatus(ActionType.Item, useId, targetId, false, false, null);
	}

	/// <summary>
	/// Actually use it. ⭐ This is the call the CastWatch hook sees, which is what keeps /ifwatch and
	/// {who} working for items without CastWatch knowing this feature exists.
	/// </summary>
	public static bool Send(uint useId, ulong targetId) {
		var manager = ActionManager.Instance();
		if (manager is null) {
			Plugin.Log.Error("ItemUse: ActionManager was null; nothing sent.");
			return false;
		}

		return manager->UseAction(ActionType.Item, useId, targetId, FromAnywhere, ActionManager.UseActionMode.None, 0, null);
	}

	/// <summary>The game's own words for a status code, since the code IS a LogMessage row id.
	/// Shared with /ifmo rather than written twice.</summary>
	public static string Reason(uint status) {
		if (status == 0)
			return "fine";
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>();
		string text = sheet?.GetRowOrDefault(status)?.Text.ExtractText().Trim() ?? string.Empty;
		return text.Length > 0 ? text : $"status {status}";
	}

	/// <summary>Resolve one placeholder through the game's own resolver, so party frames, alliance
	/// lists and everything else nobody here thought to name all work. See the 1.7.0 note in
	/// <see cref="IfMouseover.IfMouseoverFeature"/> for why this is never reimplemented.</summary>
	public static ulong ResolvePlaceholder(string segment) {
		var pronoun = PronounModule.Instance();
		if (pronoun is null)
			return 0;

		var resolved = pronoun->ResolvePlaceholder($"<{segment.Trim('<', '>')}>", 0, 0, false);
		return resolved is null ? 0 : resolved->GetGameObjectId();
	}

	/// <summary>The name behind an object id, for the log line. "someone" rather than nothing, the
	/// same fallback {who} makes.</summary>
	public static string NameOf(ulong id) {
		if (id is 0 or NoTarget)
			return "nobody";
		foreach (var obj in Plugin.Objects) {
			if (obj.GameObjectId == id)
				return obj.Name.ToString();
		}
		return "someone";
	}

	/// <summary>Record what happened, for the tab and for the log.</summary>
	public static void Decided(string what) {
		lastDecision = what;
		Plugin.Log.Information($"/item: {what}");
		Plugin.Diag($"/item: {what}");
	}

	// ── the command ──────────────────────────────────────────────────────────────────────────

	private void OnItem(string command, string arguments) {
		string payload = arguments.Trim();
		if (payload.Length == 0) {
			Plugin.Chat.PrintError("[ItemUse] nothing to use. Usage: /item \"Phoenix Down\" <mo>");
			return;
		}

		(string name, string? placeholder) = ParseArgs(payload);

		uint? itemId = ItemLookup.Resolve(name);
		if (itemId is null) {
			Plugin.Chat.PrintError($"[ItemUse] \"{name}\" is not an item you can use.");
			return;
		}

		var stack = PickStack(itemId.Value);
		if (stack is null) {
			// ⚠ SAID OUT LOUD, because "you have none left" and "the target was wrong" produce the
			// same silence otherwise, and a rez macro that stopped working needs to say which.
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
				// ⚠ FAILS CLOSED, exactly as vanilla <mo> does: a placeholder that resolves to nobody
				// does nothing at all rather than falling through to your target. Corrected in
				// DeserokUtils.md 2026-08-18 after being written down wrongly for a year.
				Decided($"<{placeholder.Trim('<', '>')}> resolves to nobody -- nothing sent");
				return;
			}
		}

		uint status = Status(stack.Value.UseId, target);
		bool sent = Send(stack.Value.UseId, target);

		// ⚠ Sent EVEN WHEN the status is non-zero, deliberately. A hotbar press with a bad target
		// still fires and lets the game print its own refusal; refusing here instead would be a
		// different behaviour from the thing this is a doorway to. The status is logged, not obeyed --
		// obeying it is /ifmo's job, where a second candidate exists to try.
		Decided($"{name}{(stack.Value.Hq ? " (HQ)" : "")} x{stack.Value.Count} on {NameOf(target)}"
			+ $" -- status {status} \"{Reason(status)}\", UseAction returned {sent}");
	}

	/// <summary>
	/// Split <c>"Phoenix Down" &lt;mo&gt;</c> into a name and a placeholder.
	///
	/// ⚠ Quotes are optional, since nothing downstream re-parses this. Both <c>/item Phoenix Down
	/// &lt;t&gt;</c> and the quoted form have to work, because the quoted form is what everyone's
	/// fingers type after years of /ac.
	/// </summary>
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

	// ── the tab section ──────────────────────────────────────────────────────────────────────

	public void DrawSection() {
		ImGui.TextWrapped(
			"Use an item on a target from a macro. The game has no command that does this -- there is "
			+ "no /item and no /use in any of its 541 text commands -- even though an item dragged onto "
			+ "a hotbar uses on your target perfectly well.");
		ImGui.Spacing();

		foreach (string template in new[] {
			"/item \"Phoenix Down\" <t>",
			"/ifmo /item \"Phoenix Down\" {mo|t}",
			"/item \"Hi-Potion\"",
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

		if (!this.claimedItem) {
			ImGui.Spacing();
			ImGui.TextWrapped("⚠ Another plugin claimed /item first, so use /dsuitem instead. It does the same thing.");
		}
	}

	public void Dispose() {
		if (this.claimedItem)
			Plugin.Commands.RemoveHandler("/item");
		Plugin.Commands.RemoveHandler("/dsuitem");
	}
}
