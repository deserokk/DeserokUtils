using System;

using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace CastWatch;

/// <summary>
/// Two commands, for gating a macro line on whether an action actually went off.
///
///   /watch Aurora
///   /ac Aurora &lt;t&gt;
///   /wait 1
///   /ifwatch
///   /echo Aurora on &lt;t&gt;
///
/// /ifwatch cancels the macro when the watched action did NOT go off, so every line after it is an
/// ordinary vanilla macro line that the GAME sends. This plugin never sends chat on your behalf --
/// it can only ever stop a line you already wrote. The one thing it transmits is /macrocancel,
/// which is client-side and produces no chat.
/// </summary>
public sealed class Plugin: IDalamudPlugin {
	public string Name => "CastWatch";

	internal static ICommandManager Commands { get; private set; } = null!;
	internal static IChatGui Chat { get; private set; } = null!;
	internal static IObjectTable Objects { get; private set; } = null!;
	internal static IDataManager Data { get; private set; } = null!;
	internal static ITargetManager Targets { get; private set; } = null!;
	internal static IPartyList Party { get; private set; } = null!;
	internal static IGameInteropProvider Interop { get; private set; } = null!;
	internal static IPluginLog Log { get; private set; } = null!;

	/// <summary>
	/// Prints what the hook saw and what /ifwatch decided, to chat rather than the log, because
	/// the log is not where you are looking mid-pull. Default ON while this is being diagnosed.
	/// </summary>
	internal static bool Verbose { get; set; } = true;

	private readonly ActionWatcher watcher;

	public Plugin(
		ICommandManager commands,
		IChatGui chat,
		IObjectTable objects,
		IDataManager data,
		ITargetManager targets,
		IPartyList party,
		IGameInteropProvider interop,
		IPluginLog log) {

		Commands = commands;
		Chat = chat;
		Objects = objects;
		Data = data;
		Targets = targets;
		Party = party;
		Interop = interop;
		Log = log;

		this.watcher = new ActionWatcher();

		Commands.AddHandler("/watch", new CommandInfo(this.OnWatch) {
			HelpMessage = "/watch <action name> -- arm a watch for that action. Put it on line 1, above the /ac.",
		});
		Commands.AddHandler("/ifwatch", new CommandInfo(this.OnIfWatch) {
			HelpMessage = "/ifwatch [command] -- with a command, run it only if the watched action went off. Bare, cancel the macro if it did not.",
		});
		Commands.AddHandler("/castwatch", new CommandInfo(this.OnToggleVerbose) {
			HelpMessage = "/castwatch -- toggle CastWatch's diagnostic output.",
		});
	}

	private void OnToggleVerbose(string command, string arguments) {
		Verbose = !Verbose;
		Diag($"diagnostics {(Verbose ? "ON" : "off")}.");
		Chat.Print($"[CastWatch] diagnostics {(Verbose ? "ON" : "off")} (debug channel).");
	}

	/// <summary>
	/// Diagnostics go to the Debug channel so they land in a tab you have configured for them,
	/// rather than in the middle of a pull. Genuine user-facing errors do NOT come through here --
	/// those stay in normal chat where they cannot be missed.
	/// </summary>
	internal static void Diag(string message) {
		if (!Verbose)
			return;
		Chat.Print(new XivChatEntry {
			Type = XivChatType.Debug,
			Name = "CastWatch",
			Message = message,
		});
	}

	// ── /watch ───────────────────────────────────────────────────────────────────────────────

	private void OnWatch(string command, string arguments) {
		// FFXIV macros require quotes around multi-word action names (/ac "Nascent Flash"), so the
		// natural thing to write on line 1 is /watch "Nascent Flash" to match. Accept both --
		// otherwise the quoted form fails the name lookup and reads as "that action doesn't exist",
		// which points at the wrong problem entirely.
		string name = StripQuotes(arguments.Trim());

		if (name.Length == 0) {
			if (this.watcher.ArmIsLive)
				Chat.Print($"[CastWatch] watching: {this.watcher.WatchedName}{(this.watcher.Fired ? " (fired)" : "")}");
			else
				Chat.Print("[CastWatch] nothing armed. Usage: /watch <action name>");
			return;
		}

		if (name is "-off" or "off" or "clear") {
			this.watcher.Disarm();
			Chat.Print("[CastWatch] disarmed.");
			return;
		}

		if (!this.watcher.Available) {
			Chat.PrintError("[CastWatch] the UseAction hook is not installed -- /watch cannot work. See /xllog.");
			return;
		}

		uint? id = ResolveActionId(name);
		if (id is null) {
			// A typo must not arm a watch that can never fire. Fail at the point of the mistake,
			// not later at /ifwatch, where it would be indistinguishable from "the spell fizzled".
			Chat.PrintError($"[CastWatch] no player action named \"{name}\". Nothing armed.");
			return;
		}

		// Snapshot the placeholders NOW. A fallback macro resolves in well under a second, but by
		// the time it reaches a later line the mouse has moved -- so reading <mo> at check time
		// answers "where is the cursor now", not "who did the spell go to".
		WatchContext context = WatchContext.Capture();
		this.watcher.Arm(id.Value, name, context);
		Diag($"armed {name} (id {id.Value}) | {context.Summary()}");
		Log.Debug($"CastWatch: armed {name} (id {id.Value})");
	}

	// ── /ifwatch ─────────────────────────────────────────────────────────────────────────────

	private void OnIfWatch(string command, string arguments) {
		// ⭐⭐ TWO FORMS, and the inline one is better:
		//
		//   /ifwatch /echo Raising <t>     <- run this line only if it went off
		//   /ifwatch                       <- cancel the macro if it did not
		//
		// The inline form has no following line to suppress, so the cancel race cannot exist at
		// all. deserok's idea, and it is structurally simpler than the thing it replaces rather
		// than a workaround for it. The bare form stays because a multi-line follow-up still needs
		// it.
		string payload = arguments.Trim();

		// ⚠ Four outcomes, and they must NOT look alike:
		//   nothing armed      -> a macro-authoring bug (line 1 is missing). Say so; do NOT cancel.
		//   arm expired        -> also say so; do NOT cancel.
		//   armed, didn't fire -> a legitimate false. Cancel. This is the normal suppression path.
		//   armed and fired    -> continue.
		// Collapsing the first two into the third is how you lose an evening wondering why the
		// callout stopped working when the real answer is you deleted a line.
		if (!this.watcher.Armed) {
			Chat.PrintError("[CastWatch] /ifwatch with nothing armed -- is there a /watch line above it? Macro NOT cancelled.");
			return;
		}

		if (!this.watcher.ArmIsLive) {
			this.watcher.Disarm();
			Chat.PrintError($"[CastWatch] the watch expired after {ActionWatcher.Expiry.TotalSeconds:0}s. Macro NOT cancelled.");
			return;
		}

		bool casting = IsCastingWatched(this.watcher.WatchedId);
		bool pass = this.watcher.Fired || casting;

		// One-shot: reading disarms, so a stale arm can never leak a callout into a later macro.
		string watched = this.watcher.WatchedName;
		bool sawAttempt = this.watcher.SawAttempt;
		bool lastResult = this.watcher.LastResult;
		bool fired = this.watcher.Fired;
		int attempts = this.watcher.Attempts;
		// Describe against the SNAPSHOT, so the answer is "it went to your mouseover" or "it fell
		// through to <2>" -- which is the thing a fallback macro cannot tell you on its own.
		string firedOn = this.watcher.Context?.Describe(this.watcher.FiredTargetId)
			?? DescribeTarget(this.watcher.FiredTargetId);
		this.watcher.Disarm();

		// ⚠ DIAGNOSTIC. Reports every input to the decision, so "it ran anyway" separates into
		// distinct causes: no attempt seen at all (hook dead or wrong id), attempt seen and
		// UseAction returned true on a cast that visibly failed (wrong signal), or a correct
		// cancel that arrived too late to stop the next line (timing).
		Diag($"{watched}: attempts={attempts} lastReturned={lastResult}"
			+ $" fired={fired}{(fired ? $" on {firedOn}" : "")} casting={casting}"
			+ $" -> {(pass ? "PASS" : "CANCEL")}"
			+ (sawAttempt ? "" : "  [no attempt seen at all]"));

		if (pass) {
			Log.Debug($"CastWatch: {watched} passed ({(casting ? "casting" : "fired")})");
			if (payload.Length > 0) {
				// ⭐ QUEUED on purpose, unlike the cancel. This path has nothing to outrun, and the
				// chatbox pipeline is what expands <t> / <mo> / <2> -- so the user's own
				// placeholders behave exactly as they would on an ordinary macro line.
				Diag($"passed -> running: {payload}");
				SendToChatbox(payload);
			}
			return;
		}

		if (payload.Length > 0) {
			// Nothing to cancel: the line simply never runs. This is the whole point of the form.
			Diag($"did not go off -> suppressed: {payload}");
			return;
		}

		Log.Debug($"CastWatch: {watched} did not go off -- cancelling macro");
		RunCommandNow("/macrocancel");
		Diag("/macrocancel executed inline.");
	}

	// ── helpers ──────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Name -> action id, matching how the name reads on the hotbar. Restricted to player actions
	/// so a watch cannot silently bind to some internal ability that shares a name.
	/// </summary>
	/// <summary>
	/// Resolve a requested-target id to something readable. Answers the question a repeat-macro
	/// raises: /ac Aurora &lt;mo&gt; x7 then &lt;2&gt; x6 fires on whichever resolved first, and by
	/// the time the macro reaches its echo line, &lt;mo&gt; is wherever the mouse happens to be --
	/// which is why the macro itself cannot name who it healed.
	/// </summary>
	private static string DescribeTarget(ulong id) {
		if (id is 0 or 0xE0000000)
			return "none";
		if (Objects.LocalPlayer is { } me && me.GameObjectId == id)
			return "SELF";
		foreach (var obj in Objects) {
			if (obj.GameObjectId == id) {
				string name = obj.Name.TextValue;
				return name.Length > 0 ? name : $"0x{id:X}";
			}
		}
		return $"0x{id:X}";
	}

	private static string StripQuotes(string s) {
		if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
			return s[1..^1].Trim();
		return s;
	}

	private static uint? ResolveActionId(string name) {
		var sheet = Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
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
	/// Covers the hardcast case: the action has not resolved yet, but the cast bar is up and it is
	/// the one being watched. Instant casts never reach this -- the hook catches those instead.
	/// </summary>
	private static bool IsCastingWatched(uint id) {
		var player = Objects.LocalPlayer;
		if (player is null || !player.IsCasting)
			return false;
		return player.CastActionId == id;
	}

	/// <summary>
	/// Run a text command SYNCHRONOUSLY, in-line, before the macro advances.
	///
	/// ⚠⚠ This is the fix for the bug that made the whole plugin look broken. The first version
	/// used UIModule.ProcessChatBoxEntry, which is what TinyCommands uses and what every guide
	/// suggests -- but it QUEUES. Measured 2026-08-02: /ifwatch correctly decided CANCEL, the queued
	/// /macrocancel was still sitting in the queue when the macro executed its next line, and the
	/// /echo went out anyway. The gate was right and the suppression lost the race.
	///
	/// RaptureShellModule.ExecuteCommandInner runs the command immediately instead. Only ever called
	/// with "/macrocancel", which is client-side and transmits no chat.
	/// </summary>
	private static unsafe void RunCommandNow(string line) {
		var shell = RaptureShellModule.Instance();
		UIModule* uiModule = UIModule.Instance();

		if (shell is not null && uiModule is not null) {
			using Utf8String cmd = new(line);
			shell->ExecuteCommandInner(&cmd, uiModule);
			return;
		}

		// Fall back to the queued path rather than doing nothing -- late is better than never --
		// but SAY which path ran, or a fixed race and an unfixed one look identical.
		Log.Warning("CastWatch: RaptureShellModule unavailable; falling back to the queued chatbox path.");
		Diag("fell back to the QUEUED cancel path (RaptureShellModule was null).");
		SendToChatbox(line);
	}

	private static unsafe void SendToChatbox(string line) {
		UIModule* uiModule = UIModule.Instance();
		if (uiModule is null) {
			// Report rather than swallow: a silent failure here means the macro keeps running and
			// the line you wanted suppressed goes out anyway, which is the exact wrong direction.
			Log.Error("CastWatch: UIModule was null; could not cancel the macro.");
			Chat.PrintError("[CastWatch] could not cancel the macro (UIModule unavailable).");
			return;
		}

		using Utf8String utf8 = new(line);
		uiModule->ProcessChatBoxEntry(&utf8);
	}

	public void Dispose() {
		Commands.RemoveHandler("/watch");
		Commands.RemoveHandler("/ifwatch");
		Commands.RemoveHandler("/castwatch");
		this.watcher.Dispose();
	}
}
