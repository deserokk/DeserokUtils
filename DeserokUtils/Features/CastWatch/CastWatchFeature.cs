using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeserokUtils.Features.CastWatch;

/// <summary>
/// Gate a macro line on whether an action actually went off.
///
///   /watch "Nascent Flash"
///   /ac "Nascent Flash" &lt;t&gt;
///   /wait 1
///   /ifwatch /p Nascent Flash on &lt;t&gt;
///
/// It never composes anything: the line is yours, and the only decision is whether it runs.
/// </summary>
internal sealed class CastWatchFeature: IDisposable {
	private readonly ActionWatcher watcher = new();

	public string TabTitle => "CastWatch";

	private const string Template =
		"/watch \"Nascent Flash\"\n"
		+ "/ac \"Nascent Flash\" <t>\n"
		+ "/wait 1\n"
		+ "/ifwatch /p Nascent Flash on <t>";

	public CastWatchFeature() {
		Plugin.Commands.AddHandler("/watch", new CommandInfo(this.OnWatch) {
			HelpMessage = "/watch <action> -- arm a watch for that action. Put it above the /ac.",
		});
		Plugin.Commands.AddHandler("/ifwatch", new CommandInfo(this.OnIfWatch) {
			HelpMessage = "/ifwatch [command] -- run the command only if the action went off. Bare: cancel the macro if it did not.",
		});
	}

	// ── /watch ───────────────────────────────────────────────────────────────────────────────

	private void OnWatch(string command, string arguments) {
		// FFXIV macros need quotes around multi-word action names (/ac "Nascent Flash"), so the
		// natural line 1 mirrors it. Accept both -- otherwise the quoted form fails the lookup and
		// reads as "that action doesn't exist", which points at the wrong problem entirely.
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
			// A typo must not arm a watch that can never fire. Fail at the point of the mistake,
			// not later at /ifwatch, where it is indistinguishable from "the spell fizzled".
			Plugin.Chat.PrintError($"[CastWatch] no player action or usable item named \"{name}\". Nothing armed.");
			return;
		}

		(ActionType type, uint id) = resolved.Value;

		// Snapshot the placeholders NOW. A fallback macro resolves in well under a second, but by
		// the time it reaches a later line the mouse has moved -- so reading <mo> at check time
		// answers "where is the cursor now", not "who did the spell go to".
		WatchContext context = WatchContext.Capture();
		this.watcher.Arm(id, name, type, context, filter);
		Plugin.Diag($"armed {name} ({type} {id}) filter={filter} | {context.Summary()}");
	}

	// ── /ifwatch ─────────────────────────────────────────────────────────────────────────────

	private void OnIfWatch(string command, string arguments) {
		// ⭐⭐ TWO FORMS, and the inline one is better:
		//
		//   /ifwatch /p Raising <t>   <- run this line only if it went off
		//   /ifwatch                  <- cancel the macro if it did not
		//
		// The inline form has no following line to suppress, so the cancel race cannot exist at
		// all. Structurally simpler than the thing it replaces, not a workaround layered on it.
		// The bare form stays because a multi-line follow-up still needs it.
		string payload = arguments.Trim();

		// ⚠ Four outcomes, and they must NOT look alike:
		//   nothing armed      -> a macro-authoring bug (line 1 is missing). Say so; do NOT cancel.
		//   arm expired        -> also say so; do NOT cancel.
		//   armed, didn't fire -> a legitimate false. This is the normal suppression path.
		//   armed and fired    -> run it.
		// Collapsing the first two into the third is how you lose an evening wondering why the
		// callout stopped working when the real answer is you deleted a line.
		if (!this.watcher.Armed) {
			Plugin.Chat.PrintError("[CastWatch] /ifwatch with nothing armed -- is there a /watch line above it? Macro NOT cancelled.");
			return;
		}

		if (!this.watcher.ArmIsLive) {
			this.watcher.Disarm();
			Plugin.Chat.PrintError($"[CastWatch] the watch expired after {ActionWatcher.Expiry.TotalSeconds:0}s. Macro NOT cancelled.");
			return;
		}

		// ⚠ The filter applies to the hardcast path too. A Raise cast at yourself is impossible, but
		// a mitigation is not -- and a filter that silently only covered instants would be right
		// for oGCDs and quietly wrong for everything with a cast bar.
		bool casting = IsCastingWatched(this.watcher.WatchedId, this.watcher.WatchedType, this.watcher.Filter, this.watcher.Context);
		bool pass = this.watcher.Fired || casting;

		// One-shot: reading disarms, so a stale arm can never leak a callout into a later macro.
		string watched = this.watcher.WatchedName;
		bool sawAttempt = this.watcher.SawAttempt;
		bool lastResult = this.watcher.LastResult;
		bool fired = this.watcher.Fired;
		int attempts = this.watcher.Attempts;
		int filteredOut = this.watcher.FilteredOut;
		TargetFilter filter = this.watcher.Filter;
		// Describe against the SNAPSHOT, so the answer is "it went to your mouseover" or "it fell
		// through to <2>" -- the thing a fallback macro cannot tell you on its own.
		string firedOn = this.watcher.Context?.Describe(this.watcher.FiredTargetId) ?? "unknown";
		this.watcher.Disarm();

		Plugin.Diag($"{watched}: attempts={attempts} lastReturned={lastResult}"
			+ $" fired={fired}{(fired ? $" on {firedOn}" : "")} casting={casting}"
			+ (filter != TargetFilter.Any ? $" filter={filter}" : "")
			+ (filteredOut > 0 ? $" filteredOut={filteredOut}" : "")
			+ $" -> {(pass ? "PASS" : "CANCEL")}"
			+ (sawAttempt ? "" : "  [no attempt seen at all]"));

		// ⚠ The one case that would otherwise be baffling: it DID go off, and the filter is why
		// nothing happened. Say that out loud rather than letting it read as a failed cast.
		if (!pass && filteredOut > 0) {
			Plugin.Chat.Print($"[CastWatch] {watched} went off, but not to a target matching --{filter.ToString().ToLowerInvariant()}.");
		}

		if (pass) {
			if (payload.Length > 0) {
				// ⭐ QUEUED on purpose, unlike the cancel below. This has nothing to outrun, and the
				// chatbox pipeline is what expands <t> / <mo> / <2> -- so the user's placeholders
				// behave exactly as they would on an ordinary macro line.
				Plugin.Diag($"passed -> running: {payload}");
				GameCommands.Queue(payload);
			}
			return;
		}

		if (payload.Length > 0) {
			// Nothing to cancel: the line simply never runs. This is the point of the inline form.
			Plugin.Diag($"did not go off -> suppressed: {payload}");
			return;
		}

		// ⚠ SYNCHRONOUS, and that is the whole fix. The queued path loses to the next macro line.
		GameCommands.RunNow("/macrocancel");
		Plugin.Diag("/macrocancel executed inline.");
	}

	// ── the tab ──────────────────────────────────────────────────────────────────────────────

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

			Row("/watch <action>", "Arm a watch. Put it above the /ac. Quotes optional.");
			Row("/watch", "Report what is armed.");
			Row("/watch off", "Disarm.");
			Row("/ifwatch <command>", "Run that command ONLY if the action went off. Nothing happens if it did not.");
			Row("/ifwatch", "No command: cancel the macro if the action did not go off.");

			ImGui.EndTable();
		}

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

	/// <summary>ImGui.SeparatorText does not exist in this binding version; this is the stand-in.</summary>
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

	// ── helpers ──────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Pull a trailing filter flag off the action name: /watch Aurora --notself
	///
	/// ⚠ An UNRECOGNISED --flag is an error, not something to ignore. Silently treating
	/// "--notslef" as part of the action name would fail the name lookup and report that no such
	/// action exists, which points at the wrong half of the line entirely.
	/// </summary>
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

	private static string StripQuotes(string s) {
		if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
			return s[1..^1].Trim();
		return s;
	}

	/// <summary>
	/// Name -> action id, matching how the name reads on the hotbar. Restricted to player actions
	/// so a watch cannot silently bind to some internal ability that shares a name.
	/// </summary>
	private static (ActionType Type, uint Id)? Resolve(string name) {
		uint? action = ResolveActionId(name);
		uint? item = ResolveItemId(name);

		// ⚠ A name matching both sheets is ambiguous and must SAY so. Picking one silently is how a
		// watch ends up armed on something the macro never uses, with no way to tell from outside.
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

	/// <summary>
	/// Items that can be USED, which is the only kind worth watching. Phoenix Down is the reason
	/// this exists: it is a long cast, it rezzes, and the person using one is usually a DPS nobody
	/// expects to be rezzing at all -- which is exactly when a callout earns its keep.
	/// ⚠ Restricted to rows with an ItemAction, or every piece of gear and every crafting material
	/// becomes a candidate name.
	/// </summary>
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

	/// <summary>
	/// The hardcast case: nothing has resolved yet, but the cast bar is up and it is the watched
	/// action. Instant casts never reach this -- the hook catches those instead.
	/// </summary>
	private static bool IsCastingWatched(uint id, ActionType type, TargetFilter filter, WatchContext? context) {
		var player = Plugin.Objects.LocalPlayer;
		if (player is null || !player.IsCasting || player.CastActionId != id)
			return false;

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
