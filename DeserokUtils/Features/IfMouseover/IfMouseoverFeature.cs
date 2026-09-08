using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeserokUtils.Features.IfMouseover;

internal sealed unsafe class IfMouseoverFeature: IDisposable {
	public string SectionTitle => "Mouseover";
	public string Summary => "/ifmo -- pick the first target the action would actually land on. {mo|2|noop} in one line instead of a fallback chain.";

	private string lastDecision = "nothing yet";

	private readonly System.Collections.Generic.HashSet<string> ambiguityLogged = new();

	public IfMouseoverFeature() {
		Plugin.Commands.AddHandler("/ifmo", new CommandInfo(this.OnIfMouseover) {
			HelpMessage = "Run a macro line against the first target it would actually land on.",

			ShowInHelp = false,
		});
	}

	private static readonly System.Text.RegularExpressions.Regex TokenPattern =
		new(@"\{([A-Za-z0-9|]+)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

	private void OnIfMouseover(string command, string arguments) {
		string payload = arguments.Trim();

		if (payload.Length == 0) {
			Plugin.Chat.PrintError("[IfMouseover] nothing to run. Usage: /ifmo /ac Clemency {mo}");
			return;
		}

		var match = TokenPattern.Match(payload);

		if (!match.Success) {
			Plugin.Chat.PrintError("[IfMouseover] no {…} token in that line, so /ifmo would change nothing. "
				+ "Put {mo} -- or {mo|2}, {mo|2|noop} -- where the target goes.");
			return;
		}

		string[] chain = match.Groups[1].Value.Split('|', StringSplitOptions.TrimEntries);

		if (Features.ItemUse.ItemLookup.ItemNameIn(payload) is { } item) {
			this.RunItemChain(item.Name, chain);
			return;
		}

		(string? placeholder, bool send, string why) = this.Decide(payload, chain);

		if (!send) {
			this.lastDecision = $"sent nothing -- {why}";
			Trace($"/ifmo: {why} -> nothing sent (noop)");
			return;
		}

		string line = payload.Remove(match.Index, match.Length).Insert(match.Index, placeholder ?? string.Empty);
		line = AddMissingQuotes(line, match.Index);
		line = System.Text.RegularExpressions.Regex.Replace(line, @"\s{2,}", " ").Trim();

		this.lastDecision = $"{(placeholder is null ? "targeted normally" : $"used {placeholder}")} -- {why}";
		Trace($"/ifmo: {why} -> {line}");

		GameCommands.Queue(line);
	}

	private static string AddMissingQuotes(string line, int tokenAt) {
		var span = ActionLookup.ActionNameIn(line);
		if (span is null || span.Value.Quoted)
			return line;
		if (!span.Value.Name.Contains(' '))
			return line;
		if (span.Value.Start > tokenAt)
			return line;
		if (ActionLookup.Resolve(span.Value.Name, span.Value.PvpVerb || ActionLookup.InPvp) is null)
			return line;

		string quoted = line.Remove(span.Value.Start, span.Value.Length)
			.Insert(span.Value.Start, $"\"{span.Value.Name}\"");
		Trace($"/ifmo: added the quotes \"{span.Value.Name}\" needs -- the action commands reject "
			+ "unquoted multi-word names.");
		return quoted;
	}

	private void RunItemChain(string name, string[] chain) {
		uint? itemId = Features.ItemUse.ItemLookup.Resolve(name);
		if (itemId is null) {
			Plugin.Chat.PrintError($"[IfMouseover] \"{name}\" is not an item you can use."
				+ Features.ItemUse.ItemLookup.SuggestionFor(name));
			return;
		}

		var stack = Features.ItemUse.ItemUseFeature.PickStack(itemId.Value);
		if (stack is null) {

			Plugin.Chat.PrintError($"[IfMouseover] you are not carrying any {name}.");
			this.lastDecision = $"sent nothing -- no {name} in the bags";
			Trace($"/ifmo: no {name} in the bags -> nothing sent");
			return;
		}

		foreach (string seg in chain) {
			if (seg.Equals("noop", StringComparison.OrdinalIgnoreCase)) {
				this.lastDecision = $"sent nothing -- no candidate before noop could take {name}";
				Trace($"/ifmo: no candidate before noop could take {name} -> nothing sent (noop)");
				return;
			}

			ulong who = Features.ItemUse.ItemUseFeature.ResolvePlaceholder(seg);
			if (who == 0) {
				Trace($"/ifmo: <{seg}> resolves to nobody");
				continue;
			}

			uint status = Features.ItemUse.ItemUseFeature.Status(stack.Value.UseId, who);
			string whoName = Features.ItemUse.ItemUseFeature.NameOf(who);
			Trace($"/ifmo: {name} (item {stack.Value.UseId}{(stack.Value.Hq ? ", HQ" : "")}) on <{seg}> "
				+ $"= {whoName}: status={status}{Explain(status)}");

			if (status != 0)
				continue;

			bool sent = Features.ItemUse.ItemUseFeature.Send(stack.Value.UseId, who);
			this.lastDecision = $"used <{seg}> -- {name} on {whoName}";
			Trace($"/ifmo: {name} lands on {whoName} via <{seg}> -> sent (UseAction returned {sent})");
			return;
		}

		ulong target = Plugin.Targets.Target?.GameObjectId ?? Features.ItemUse.ItemUseFeature.NoTarget;
		bool fallback = Features.ItemUse.ItemUseFeature.Send(stack.Value.UseId, target);
		this.lastDecision = $"targeted normally -- nothing in the chain could take {name}";
		Trace($"/ifmo: nothing in the chain could take {name} -> current target "
			+ $"({Features.ItemUse.ItemUseFeature.NameOf(target)}), UseAction returned {fallback}");
	}

	private (string? Placeholder, bool Send, string Why) Decide(string payload, string[] chain) {
		var span = ActionLookup.ActionNameIn(payload);
		string? name = span?.Name;

		bool preferPvp = span?.PvpVerb == true || ActionLookup.InPvp;
		var found = name is null ? null : ActionLookup.Resolve(name, preferPvp);
		uint? actionId = found?.Id;

		if (actionId is uint named) {
			var manager = ActionManager.Instance();
			if (manager is not null) {
				uint adjusted = manager->GetAdjustedActionId(named);
				if (adjusted != named && adjusted != 0) {
					Plugin.Log.Information(
						$"IfMouseover: \"{name}\" is action {named}, but the game would cast {adjusted} "
						+ "right now (trait upgrade or level sync). Checking the one it would cast.");
					actionId = adjusted;
				}
			}
		}

		if (found is { Ambiguous: true } && ambiguityLogged.Add($"{name}|{preferPvp}")) {
			Plugin.Log.Information($"IfMouseover: \"{name}\" exists as both a PvP and a non-PvP action; "
				+ $"using the {(found.Value.Pvp ? "PvP" : "non-PvP")} row ({found.Value.Id}). "
				+ $"verb={(span?.PvpVerb == true ? "/pvpac" : "/ac")} inPvp={ActionLookup.InPvp}");
		}

		if (actionId is null) {

			Trace($"/ifmo: no usable action name in that line ({name ?? "none found"}); presence check only.");
			foreach (string seg in chain) {
				if (seg.Equals("noop", StringComparison.OrdinalIgnoreCase))
					return (null, false, "nothing resolved, and the chain ends in noop");
				var who = Resolve(seg);
				if (who is not null)
					return ($"<{seg}>", true, $"<{seg}> resolves to {who->NameString} -- presence check only");
			}
			return (null, true, "nothing in the chain resolved -- ordinary targeting");
		}

		var self = Plugin.Objects.LocalPlayer;
		foreach (string seg in chain) {

			if (seg.Equals("noop", StringComparison.OrdinalIgnoreCase))
				return (null, false, $"no candidate before noop could take {name}");

			var who = Resolve(seg);
			if (who is null) {
				Trace($"/ifmo: <{seg}> resolves to nobody");
				continue;
			}

			bool can = ActionManager.CanUseActionOnTarget(actionId.Value, who);
			uint status = self is null ? 0 : ActionManager.GetActionInRangeOrLoS(
				actionId.Value,
				(FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)self.Address,
				who);

			Trace($"/ifmo: {name} ({actionId}{(found!.Value.Pvp ? ", PvP" : "")}) on <{seg}> "
				+ $"= {who->NameString}: CanUseActionOnTarget={can}, rangeOrLoS={status}{Explain(status)}");

			if (can && status == 0)
				return ($"<{seg}>", true, $"{name} lands on {who->NameString} via <{seg}>");
		}

		return (null, true, $"nothing in the chain could take {name} -- ordinary targeting");
	}

	private static FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* Resolve(string segment) {
		var pronoun = FFXIVClientStructs.FFXIV.Client.UI.Misc.PronounModule.Instance();
		if (pronoun is null)
			return null;

		var resolved = pronoun->ResolvePlaceholder($"<{segment}>", 0, 0, false);

		if (resolved is null && segment.Equals("mo", StringComparison.OrdinalIgnoreCase)) {
			string worldOnly = Plugin.Targets.MouseOverTarget?.Name.ToString() ?? "none";
			if (worldOnly != "none")
				Plugin.Log.Warning($"/ifmo: the game resolved <mo> to nothing while Dalamud reports {worldOnly}. "
					+ "Suspect the ResolvePlaceholder arguments.");
		}

		return resolved;
	}

	public void DrawSection() {
		ImGui.TextWrapped(
			"Put a token where the target goes. Each candidate is checked against the action, and the "
			+ "first one it would actually land on is used.");
		ImGui.Spacing();

		if (ImGui.BeginTable("ifmo_chain", 2,
			ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) {
			ImGui.TableSetupColumn("token", ImGuiTableColumnFlags.WidthFixed, 130f);
			ImGui.TableSetupColumn("meaning");
			ImGui.TableHeadersRow();
			Row("{mo}", "Mouseover, else ordinary targeting. Good for Clemency.");
			Row("{mo|2}", "Mouseover, then <2>, else ordinary targeting.");
			Row("{mo|2|noop}", "Mouseover, then <2>, else send NOTHING.");
			ImGui.EndTable();
		}

		ImGui.Spacing();
		ImGui.TextWrapped(
			"⚠ The tail matters, and differs per action. Cover cannot target you, so ordinary targeting "
			+ "just no-ops. Heart of Corundum CAN target you, so the same fallthrough quietly spends a "
			+ "cooldown on yourself that you pressed for somebody else. That is what noop is for.");
		ImGui.Spacing();
		ImGui.TextWrapped(
			"Any placeholder the game understands works as a segment -- mo, 2, t, f, me and the rest. "
			+ "There is no list of allowed names; whatever the game resolves is legal.");

		ImGui.Spacing();
		foreach (string template in new[] {
			"/ifmo /ac Clemency {mo}",
			"/ifmo /ac Cover {mo|2}",
			"/ifmo /ac \"Heart of Corundum\" {mo|2|noop}",
		}) {
			ImGui.TextUnformatted(template);
			ImGui.SameLine();
			if (ImGui.Button($"Copy##ifmo{template.Length}"))
				ImGui.SetClipboardText(template);
		}

		ImGui.Spacing();
		ImGui.TextDisabled($"last press: {this.lastDecision}");

		ImGui.Spacing();
		ImGui.TextWrapped(
			"This replaces the twelve-line ghetto queue with a fallback at the bottom. That pattern "
			+ "works, but it has a race: press it while the GCD is still rolling and every mouseover "
			+ "line fails on cooldown, then the GCD expires onto the bare fallback line -- so it "
			+ "targets normally despite a perfectly good mouseover. The fallback sits last, which is "
			+ "exactly where it is most likely to win. One line has nothing to race.");

		ImGui.Spacing();
		ImGui.TextWrapped(
			"The test is \"would this action work on that target\", not \"is something under the "
			+ "cursor\". Pointing at an enemy while healing has to fall through to normal targeting, "
			+ "the way the vanilla macro does -- a presence check would send <mo> anyway and simply "
			+ "fail. If the action name cannot be read out of your line, it degrades to a presence "
			+ "check and says so in diagnostics rather than pretending.");
	}

	private static void Row(string token, string what) {
		ImGui.TableNextRow();
		ImGui.TableNextColumn();
		ImGui.TextUnformatted(token);
		ImGui.TableNextColumn();
		ImGui.TextWrapped(what);
	}

	private static string Reason(uint status) => Features.ItemUse.ItemUseFeature.Reason(status);

	private static string Explain(uint status) => status == 0 ? "" : $" \"{Reason(status)}\"";

	private static void Trace(string message) {
		Plugin.Log.Information(message);
		Plugin.Diag(message);
	}

	public void Dispose() => Plugin.Commands.RemoveHandler("/ifmo");
}
