using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeserokUtils.Features.IfMouseover;

/// <summary>
/// Use the mouseover if it would actually work, otherwise behave normally -- in ONE line.
///
///   /ifmo /ac Clemency {mo}
///
/// <c>{mo}</c> becomes <c>&lt;mo&gt;</c> when the action would really land on what you are pointing
/// at, and becomes NOTHING otherwise, leaving <c>/ac Clemency</c> -- ordinary targeting.
///
/// ## ⚠⚠ What this replaces, and why one line beats thirteen
///
/// The vanilla way is a ghetto queue with a fallback at the bottom:
/// <code>
/// /merror off
/// /ac Clemency &lt;mo&gt;      x12
/// /ac Clemency             &lt;- the fallback
/// </code>
/// It works, and it has a race. Press it while the GCD is still rolling and all twelve mouseover
/// lines fail on cooldown rather than on targeting; then the GCD expires and the NEXT line to run is
/// the bare one. It lands on your target or on you while you had a perfectly good mouseover.
///
/// ⭐ The fallback sits last, so it is the line best placed to win exactly when you pressed early --
/// which is the whole reason for ghetto-queueing in the first place. One line removes the race
/// completely: there is a single attempt and its targeting was decided before it fired.
///
/// ## ⚠⚠ Why "is there a mouseover" is the WRONG test
///
/// That is what TinyCommands' `-o` checks, and using it here would be WORSE than the vanilla macro.
/// Mouse over an enemy while trying to heal: vanilla fires &lt;mo&gt;, fails on an invalid target,
/// keeps going, and the bare line rescues it with normal behaviour. A presence check would substitute
/// &lt;mo&gt;, fail identically, and have no second line to fall through to. Same for out of range and
/// out of line of sight -- every case where vanilla currently degrades correctly.
///
/// ⭐ So the question is "would this action work on that target", and the game answers it itself:
/// <c>ActionManager.CanUseActionOnTarget</c>.
///
/// ⚠ Braces, not angle brackets, for the same reason as CastWatch's {who}: &lt;mo&gt; is the game's
/// own syntax and a token the chat pipeline tries to parse is a token that breaks unreadably.
/// </summary>
internal sealed unsafe class IfMouseoverFeature: IDisposable {
	public string SectionTitle => "Mouseover";
	public string Summary => "/ifmo -- pick the first target the action would actually land on. {mo|2|noop} in one line instead of a fallback chain.";


	/// <summary>Last decision, for the tab. Purely diagnostic.</summary>
	private string lastDecision = "nothing yet";

	public IfMouseoverFeature() {
		Plugin.Commands.AddHandler("/ifmo", new CommandInfo(this.OnIfMouseover) {
			HelpMessage = "/ifmo <command with {mo}> -- picks the first target the action would land on. Chain with {mo|2}, end with |noop to send nothing.",
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
		// ⚠ A payload with no token is almost certainly a mistake -- it would run identically without
		// /ifmo, so silently passing it through would leave a macro that looks conditional and is not.
		if (!match.Success) {
			Plugin.Chat.PrintError("[IfMouseover] no {…} token in that line, so /ifmo would change nothing. "
				+ "Put {mo} -- or {mo|2}, {mo|2|noop} -- where the target goes.");
			return;
		}

		string[] chain = match.Groups[1].Value.Split('|', StringSplitOptions.TrimEntries);
		(string? placeholder, bool send, string why) = this.Decide(payload, chain);

		if (!send) {
			this.lastDecision = $"sent nothing -- {why}";
			Trace($"/ifmo: {why} -> nothing sent (noop)");
			return;
		}

		// ⚠ An empty substitution leaves "/ac Cover " -- harmless to the game, but the log line is read
		// by a human and a stray double space reads like a bug.
		string line = payload.Remove(match.Index, match.Length).Insert(match.Index, placeholder ?? string.Empty);
		line = AddMissingQuotes(line, match.Index);
		line = System.Text.RegularExpressions.Regex.Replace(line, @"\s{2,}", " ").Trim();

		this.lastDecision = $"{(placeholder is null ? "targeted normally" : $"used {placeholder}")} -- {why}";
		Trace($"/ifmo: {why} -> {line}");

		// ⭐ QUEUED, so the chatbox pipeline expands the placeholder we chose and any others in your
		// line. Sending it synchronously would break exactly the token this feature exists to insert.
		GameCommands.Queue(line);
	}


	/// <summary>
	/// Quote a multi-word action name the user forgot to quote.
	///
	/// ⚠⚠ Because forgetting is normal and the failure is invisible. `/ac` requires quotes for
	/// multi-word names, but this plugin's parser does not -- so an unquoted "Heart of Corundum"
	/// resolves, validates, picks the right target, and then emits a line the game silently rejects.
	/// Correct diagnostics, nothing happening. The plugin already knows the real name, so it can just
	/// put the quotes in.
	///
	/// ⭐ Only ever ADDS quotes around a name that resolved to a real player action, and only when it
	/// actually contains a space. It never rewrites anything else in your line.
	///
	/// ⚠ Skips the edit if the action name sits AFTER the token, since the index arithmetic would be
	/// wrong -- nobody writes that, and quietly corrupting a line beats nothing at all.
	/// </summary>
	private static string AddMissingQuotes(string line, int tokenAt) {
		var span = ActionLookup.ActionNameIn(line);
		if (span is null || span.Value.Quoted)
			return line;
		if (!span.Value.Name.Contains(' '))
			return line;
		if (span.Value.Start > tokenAt)
			return line;
		if (ActionLookup.Resolve(span.Value.Name) is null)
			return line;

		string quoted = line.Remove(span.Value.Start, span.Value.Length)
			.Insert(span.Value.Start, $"\"{span.Value.Name}\"");
		Trace($"/ifmo: added the quotes \"{span.Value.Name}\" needs -- /ac rejects unquoted multi-word names.");
		return quoted;
	}

	/// <summary>
	/// Walk the chain and pick the first placeholder the action would actually land on.
	///
	/// ⭐⭐ Better than the vanilla fallback chain, not merely shorter. Vanilla FIRES AND FAILS down
	/// the list -- a line per miss, each burning a chance for the GCD to expire onto the wrong one.
	/// This validates every candidate before anything is sent, so there is one attempt and it is the
	/// right one.
	///
	/// ⚠⚠ The tail is EXPLICIT, because the correct tail differs per action and no default is right
	/// for all of them:
	/// <code>
	/// {mo}          mouseover, else ordinary targeting      Clemency
	/// {mo|2}        mouseover, then &lt;2&gt;, else ordinary targeting
	/// {mo|2|noop}   mouseover, then &lt;2&gt;, else SEND NOTHING     Heart of Corundum
	/// </code>
	/// Cover cannot target you, so ordinary targeting merely no-ops there. **Heart of Corundum CAN**,
	/// so the same fallthrough silently self-casts a cooldown you pressed for somebody else -- and
	/// deserok has self-use on a separate key. `noop` is how you say "if neither works, do nothing".
	///
	/// ⚠ Segment names are NOT validated against a list. Whatever the game resolves is legal --
	/// <c>mo</c>, <c>2</c>, <c>t</c>, <c>f</c>, <c>me</c>, and anything else that exists. Same reason
	/// the mouseover bug happened: an allowlist cannot contain what nobody thought to name.
	/// </summary>
	private (string? Placeholder, bool Send, string Why) Decide(string payload, string[] chain) {
		string? name = ActionLookup.ActionNameIn(payload)?.Name;
		uint? actionId = name is null ? null : ActionLookup.Resolve(name);

		if (actionId is null) {
			// ⚠ Degrade, but SAY so. Without the action we cannot ask whether anything would work, so
			// this falls back to "first segment that resolves to somebody" -- the weaker presence test,
			// and the user deserves to know they are getting it.
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
			// ⚠ noop ends the walk wherever it sits, so {mo|noop|2} means what it says: the 2 is
			// unreachable. Honouring position beats quietly reordering somebody's intent.
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

			Trace($"/ifmo: {name} ({actionId}) on <{seg}> = {who->NameString}: "
				+ $"CanUseActionOnTarget={can}, rangeOrLoS={status}{Explain(status)}");

			if (can && status == 0)
				return ($"<{seg}>", true, $"{name} lands on {who->NameString} via <{seg}>");
		}

		// ⚠ Falling off the end means ordinary targeting -- the behaviour {mo} shipped with, kept so
		// the Clemency macro written before the chain existed still does what it did.
		return (null, true, $"nothing in the chain could take {name} -- ordinary targeting");
	}

	/// <summary>
	/// Resolve one chain segment through the game's own placeholder resolver.
	///
	/// ⚠ Dalamud's world-only mouseover reading is logged alongside for <c>mo</c>, because that
	/// disagreement is exactly the bug that shipped in 1.7.0 and it should stay visible.
	/// </summary>
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

	// ── the tab section ──────────────────────────────────────────────────────────────────────

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

	/// <summary>
	/// The game's own words for a status code, since the code IS a LogMessage row id.
	///
	/// ⭐ Reading it back out of the sheet rather than hardcoding "566 means out of range". The
	/// mapping came from the game and stays coming from the game, so a patch that renumbers these
	/// makes the log read oddly instead of making it read confidently wrong.
	/// </summary>
	private static string Reason(uint status) {
		if (status == 0)
			return "fine";
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>();
		string text = sheet?.GetRowOrDefault(status)?.Text.ExtractText().Trim() ?? string.Empty;
		return text.Length > 0 ? text : $"status {status}";
	}

	private static string Explain(uint status) => status == 0 ? "" : $" \"{Reason(status)}\"";

	/// <summary>
	/// Every decision, to BOTH the log and the diagnostic channel.
	///
	/// ⚠⚠ THE SAME GAP AS DRAWSHEATHE, MADE TWICE IN ONE NIGHT. Diag is off by default, so the first
	/// /ifmo test produced a dalamud.log containing the action-map line and nothing else -- the
	/// command had clearly run and left no evidence of what it decided. A macro press is a rare
	/// event; one line each costs nothing against being able to read what happened afterwards
	/// without asking for a toggle and a repeat.
	/// </summary>
	private static void Trace(string message) {
		Plugin.Log.Information(message);
		Plugin.Diag(message);
	}

	public void Dispose() => Plugin.Commands.RemoveHandler("/ifmo");
}
