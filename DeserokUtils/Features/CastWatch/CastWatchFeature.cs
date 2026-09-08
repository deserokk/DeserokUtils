using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeserokUtils.Features.CastWatch;

internal sealed class CastWatchFeature: IDisposable {
	private readonly ActionWatcher watcher = new();

	public string TabTitle => "CastWatch";
	public string Summary => "/watch + /ifwatch -- run a macro line only if the action actually went off, including instants with no cast bar.";

	private const string Template =
		"/watch \"Nascent Flash\"\n"
		+ "/ac \"Nascent Flash\" <t>\n"
		+ "/wait 1\n"
		+ "/ifwatch /p Nascent Flash on {who}";

	public CastWatchFeature() {
		Plugin.Commands.AddHandler("/watch", new CommandInfo(this.OnWatch) {
			HelpMessage = "Arm a watch on an action, for use above an /ac in a macro.",

			ShowInHelp = false,
		});
		Plugin.Commands.AddHandler("/ifwatch", new CommandInfo(this.OnIfWatch) {
			HelpMessage = "Run a macro line only if the watched action went off.",
			ShowInHelp = false,
		});
	}

	private void OnWatch(string command, string arguments) {

		(string rest, TargetFilter filter) = ParseFilter(arguments.Trim());
		string name = StripQuotes(rest);

		if (name.Length == 0) {
			if (this.watcher.ArmIsLive)
				Plugin.Chat.Print($"[CastWatch] watching: {this.watcher.WatchedName}{(this.watcher.Fired ? " (fired)" : "")}");
			else
				Plugin.Chat.Print("[CastWatch] nothing armed. Usage: /watch <action name>");
			return;
		}

		if (name is "-off" or "off" or "clear") {
			this.watcher.Disarm();
			Plugin.Chat.Print("[CastWatch] disarmed.");
			return;
		}

		if (!this.watcher.Available) {
			Plugin.Chat.PrintError("[CastWatch] the UseAction hook is not installed -- /watch cannot work. See /xllog.");
			return;
		}

		var resolved = Resolve(name);
		if (resolved is null) {

			string near = Features.ItemUse.ItemLookup.Suggest(name)
				?? Features.IfMouseover.ActionLookup.Suggest(name)
				?? string.Empty;
			Plugin.Chat.PrintError($"[CastWatch] no player action or usable item named \"{name}\". Nothing armed."
				+ (near.Length > 0 ? $" Did you mean \"{near}\"?" : string.Empty));
			return;
		}

		(ActionType type, uint id) = resolved.Value;

		WatchContext context = WatchContext.Capture();
		this.watcher.Arm(id, name, type, context, filter);
		Plugin.Diag($"armed {name} ({type} {id}) filter={filter} | {context.Summary()}");
	}

	private void OnIfWatch(string command, string arguments) {

		string payload = arguments.Trim();

		if (!this.watcher.Armed) {
			Plugin.Chat.PrintError("[CastWatch] /ifwatch with nothing armed -- is there a /watch line above it? Macro NOT cancelled.");
			return;
		}

		if (!this.watcher.ArmIsLive) {
			this.watcher.Disarm();
			Plugin.Chat.PrintError($"[CastWatch] the watch expired after {ActionWatcher.Expiry.TotalSeconds:0}s. Macro NOT cancelled.");
			return;
		}

		bool casting = this.IsCastingWatched(this.watcher.Filter, this.watcher.Context);
		bool pass = this.watcher.Fired || casting;

		string watched = this.watcher.WatchedName;
		bool sawAttempt = this.watcher.SawAttempt;
		bool lastResult = this.watcher.LastResult;
		bool fired = this.watcher.Fired;
		int attempts = this.watcher.Attempts;
		int filteredOut = this.watcher.FilteredOut;
		TargetFilter filter = this.watcher.Filter;

		string firedOn = this.watcher.Context?.Describe(this.watcher.FiredTargetId) ?? "unknown";

		ulong whoId = this.watcher.Fired
			? this.watcher.FiredTargetId
			: Plugin.Objects.LocalPlayer?.CastTargetObjectId ?? 0;
		WatchContext? ctx = this.watcher.Context;
		this.watcher.Disarm();

		Plugin.Diag($"{watched}: attempts={attempts} lastReturned={lastResult}"
			+ $" fired={fired}{(fired ? $" on {firedOn}" : "")} casting={casting}"
			+ (filter != TargetFilter.Any ? $" filter={filter}" : "")
			+ (filteredOut > 0 ? $" filteredOut={filteredOut}" : "")
			+ $" -> {(pass ? "PASS" : "CANCEL")}"
			+ (sawAttempt ? "" : "  [no attempt seen at all]"));

		if (!pass && filteredOut > 0) {
			Plugin.Chat.Print($"[CastWatch] {watched} went off, but not to a target matching --{filter.ToString().ToLowerInvariant()}.");
		}

		if (pass) {
			if (payload.Length > 0) {
				string line = SubstituteWho(payload, ctx, whoId);

				Plugin.Diag($"passed -> running: {line}");
				GameCommands.Queue(line);
			}
			return;
		}

		if (payload.Length > 0) {

			Plugin.Diag($"did not go off -> suppressed: {payload}");
			return;
		}

		GameCommands.RunNow("/macrocancel");
		Plugin.Diag("/macrocancel executed inline.");
	}

	public void DrawTab() {
		ImGui.TextWrapped("Gate a macro line on whether an action actually went off.");
		ImGui.Spacing();

		Section("Right now");
		if (!this.watcher.Available) {
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
				"The UseAction hook is NOT installed. Nothing will ever fire. See /xllog.");
		}
		else if (!this.watcher.Armed) {
			ImGui.TextDisabled("nothing armed");
		}
		else {
			ImGui.Text($"watching: {this.watcher.WatchedName}");
			ImGui.SameLine();
			if (!this.watcher.ArmIsLive)
				ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), "(expired)");
			else if (this.watcher.Fired)
				ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "(fired)");
			else
				ImGui.TextDisabled($"({this.watcher.Attempts} attempts, none accepted yet)");
		}

		Section("Commands");
		if (ImGui.BeginTable("castwatch_cmds", 2,
			ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) {
			ImGui.TableSetupColumn("command", ImGuiTableColumnFlags.WidthFixed, 200f);
			ImGui.TableSetupColumn("what it does");
			ImGui.TableHeadersRow();

			Row("/watch <action>", "Arm a watch. Put it above the /ac. Quotes optional. Usable items work too (Phoenix Down).");
			Row("/watch", "Report what is armed.");
			Row("/watch off", "Disarm.");
			Row("/ifwatch <command>", "Run that command ONLY if the action went off.");
			Row("/ifwatch", "No command: cancel the macro if the action did not go off.");

			ImGui.EndTable();
		}

		Section("Target filters");
		ImGui.TextWrapped("Add one to /watch to say WHO has to receive it for the callout to count:");
		ImGui.Spacing();
		if (ImGui.BeginTable("castwatch_filters", 2,
			ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) {
			ImGui.TableSetupColumn("flag", ImGuiTableColumnFlags.WidthFixed, 200f);
			ImGui.TableSetupColumn("counts when");
			ImGui.TableHeadersRow();

			Row("--any", "Anyone at all. The default when no flag is given.");
			Row("--self", "It went to you.");
			Row("--notself", "It went to anyone but you. Catches a self-redirect -- Aurora and friends quietly retarget to you when the mouseover is invalid.");
			Row("--party", "It went to a party member other than you. NOT for out-of-party rezzes; use --notself there.");

			ImGui.EndTable();
		}
		ImGui.Spacing();
		ImGui.TextDisabled("/watch Aurora --notself");
		ImGui.TextWrapped(
			"Only exact tests are offered. Friendly/hostile guessed from object type puts pets, "
			+ "chocobos and friendly NPCs in a grey zone, and a filter that is usually right is worse "
			+ "than one that is not offered.");

		Section("The {who} token");
		ImGui.TextWrapped(
			"Put {who} in the /ifwatch line and it becomes whoever actually received the action.");
		ImGui.Spacing();
		ImGui.TextDisabled("/ifwatch /p Aurora on {who}");
		ImGui.Spacing();
		ImGui.TextWrapped(
			"This is the thing a vanilla macro cannot do: <mo> on the callout line is evaluated when "
			+ "THAT line runs, so a fallback macro that fell through from <mo> to <2> would announce "
			+ "your mouseover while someone else got the heal. Braces, not <who> -- angle brackets "
			+ "collide with the game's own placeholders. If the name cannot be resolved it becomes "
			+ "\"someone\" rather than eating the line.");

		Section("Macro template");
		ImGui.TextUnformatted(Template);
		if (ImGui.Button("Copy##castwatch"))
			ImGui.SetClipboardText(Template);
		ImGui.SameLine();
		ImGui.TextDisabled("prefer the /ifwatch <command> form");

		Section("Notes");
		ImGui.BulletText("Works for instant casts and oGCDs, which have no cast bar to watch.");
		ImGui.BulletText("A watch expires after 10s and is one-shot: /ifwatch disarms it.");
		ImGui.BulletText("Arming again replaces the previous arm, so a double-press starts clean.");
		ImGui.TextWrapped("It only ever runs a line you wrote. It cannot compose or send anything itself.");
	}

	private static void Section(string title) {
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextDisabled(title);
		ImGui.Spacing();
	}

	private static void Row(string cmd, string what) {
		ImGui.TableNextRow();
		ImGui.TableNextColumn();
		ImGui.TextUnformatted(cmd);
		ImGui.TableNextColumn();
		ImGui.TextWrapped(what);
	}

	private static (string Remainder, TargetFilter Filter) ParseFilter(string input) {
		int dash = input.LastIndexOf("--", StringComparison.Ordinal);
		if (dash < 0)
			return (input, TargetFilter.Any);

		string flag = input[(dash + 2)..].Trim().ToLowerInvariant();
		string rest = input[..dash].Trim();

		TargetFilter? filter = flag switch {
			"self" => TargetFilter.Self,
			"notself" or "not-self" or "other" => TargetFilter.NotSelf,
			"party" or "partymember" => TargetFilter.Party,
			"any" => TargetFilter.Any,
			_ => null,
		};

		if (filter is null) {
			Plugin.Chat.PrintError($"[CastWatch] unknown filter \"--{flag}\". Use --self, --notself, --party or --any.");
			return (rest, TargetFilter.Any);
		}

		return (rest, filter.Value);
	}

	private static string SubstituteWho(string payload, WatchContext? context, ulong whoId) {
		if (payload.IndexOf("{who}", StringComparison.OrdinalIgnoreCase) < 0)
			return payload;

		string name = context?.NameOf(whoId) ?? string.Empty;

		if (name.Length == 0) {
			name = "someone";
			Plugin.Diag($"{{who}} could not be resolved (id 0x{whoId:X}); used \"someone\".");
		}

		return System.Text.RegularExpressions.Regex.Replace(
			payload, @"\{who\}", name.Replace("$", "$$"),
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);
	}

	private static string StripQuotes(string s) {
		if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
			return s[1..^1].Trim();
		return s;
	}

	private static (ActionType Type, uint Id)? Resolve(string name) {
		uint? action = ResolveActionId(name);
		uint? item = ResolveItemId(name);

		if (action is not null && item is not null) {
			Plugin.Chat.PrintError(
				$"[CastWatch] \"{name}\" is both an action ({action}) and an item ({item}). Watching the ACTION.");
			return (ActionType.Action, action.Value);
		}

		if (action is not null)
			return (ActionType.Action, action.Value);
		if (item is not null)
			return (ActionType.Item, item.Value);
		return null;
	}

	private static uint? ResolveActionId(string name) {
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
		if (sheet is null)
			return null;

		foreach (var row in sheet) {
			if (!row.IsPlayerAction)
				continue;
			string rowName = row.Name.ExtractText();
			if (rowName.Length > 0 && string.Equals(rowName, name, StringComparison.OrdinalIgnoreCase))
				return row.RowId;
		}

		return null;
	}

	private static uint? ResolveItemId(string name) {
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
		if (sheet is null)
			return null;

		foreach (var row in sheet) {
			if (row.ItemAction.RowId == 0)
				continue;
			string rowName = row.Name.ExtractText();
			if (rowName.Length > 0 && string.Equals(rowName, name, StringComparison.OrdinalIgnoreCase))
				return row.RowId;
		}

		return null;
	}

	private bool IsCastingWatched(TargetFilter filter, WatchContext? context) {
		var player = Plugin.Objects.LocalPlayer;
		if (player is null || !player.IsCasting)
			return false;

		if (!this.watcher.MatchesWatch(this.watcher.WatchedType, player.CastActionId, out _, out _)) {
			Plugin.Diag($"casting id={player.CastActionId} castType={player.CastActionType}"
				+ $" vs watch {this.watcher.WatchedId} ({this.watcher.WatchedType}) -- no match");
			return false;
		}

		if (filter == TargetFilter.Any || context is null)
			return true;

		return context.Passes(filter, player.CastTargetObjectId, player.GameObjectId);
	}

	public void Dispose() {
		Plugin.Commands.RemoveHandler("/watch");
		Plugin.Commands.RemoveHandler("/ifwatch");
		this.watcher.Dispose();
	}
}
