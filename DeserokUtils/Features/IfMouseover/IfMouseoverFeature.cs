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
	public string Summary => "/ifmo -- use the mouseover if the action would land on it, otherwise target normally. One line instead of thirteen.";

	private const string Token = "{mo}";

	/// <summary>Last decision, for the tab. Purely diagnostic.</summary>
	private string lastDecision = "nothing yet";

	public IfMouseoverFeature() {
		Plugin.Commands.AddHandler("/ifmo", new CommandInfo(this.OnIfMouseover) {
			HelpMessage = "/ifmo <command with {mo}> -- {mo} becomes <mo> if the action would work on your mouseover, otherwise nothing.",
		});
	}

	private void OnIfMouseover(string command, string arguments) {
		string payload = arguments.Trim();

		if (payload.Length == 0) {
			Plugin.Chat.PrintError("[IfMouseover] nothing to run. Usage: /ifmo /ac Clemency {mo}");
			return;
		}

		// ⚠ A payload with no token is almost certainly a mistake -- it would run identically without
		// /ifmo, so silently passing it through would leave a macro that looks conditional and is not.
		if (payload.IndexOf(Token, StringComparison.OrdinalIgnoreCase) < 0) {
			Plugin.Chat.PrintError($"[IfMouseover] no {Token} in that line, so /ifmo would change nothing. Add {Token} where the target goes.");
			return;
		}

		(bool useMouseover, string why) = this.Decide(payload);

		// ⚠ Replace with an EMPTY string when the mouseover is not usable, then tidy the double space
		// it leaves. "/ac Clemency " and "/ac Clemency" behave the same, but the log line is read by a
		// human and a stray space reads like a bug.
		string line = System.Text.RegularExpressions.Regex.Replace(
			payload, System.Text.RegularExpressions.Regex.Escape(Token),
			useMouseover ? "<mo>" : string.Empty,
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		line = System.Text.RegularExpressions.Regex.Replace(line, @"\s{2,}", " ").Trim();

		this.lastDecision = $"{(useMouseover ? "used <mo>" : "targeted normally")} -- {why}";
		Trace($"/ifmo: {why} -> {line}");

		// ⭐ QUEUED, so the chatbox pipeline expands <mo> and every other placeholder in your line.
		// Sending it synchronously would break exactly the token this feature exists to insert.
		GameCommands.Queue(line);
	}

	/// <summary>
	/// Whether to use the mouseover, and the reason -- the reason is returned rather than logged here
	/// so every path names itself in one place.
	/// </summary>
	private (bool Use, string Why) Decide(string payload) {
		var mouseover = Plugin.Targets.MouseOverTarget;
		if (mouseover is null)
			return (false, "no mouseover");

		string? name = ActionLookup.ActionNameIn(payload);
		if (name is null) {
			// ⚠ Degrade, but SAY so. Without the action we cannot ask whether it would work, so this
			// falls back to the presence check -- which is the weaker test criticised above, and the
			// user deserves to know they are getting it.
			Trace("/ifmo: could not find an action name in that line; falling back to a presence check.");
			return (true, $"mouseover present ({mouseover.Name}), action unknown -- presence check only");
		}

		uint? actionId = ActionLookup.Resolve(name);
		if (actionId is null)
			return (true, $"mouseover present ({mouseover.Name}), \"{name}\" is not a player action -- presence check only");

		// ⚠ STATIC, not instance -- the compiler said so. Both of these are free functions on
		// ActionManager rather than members of the singleton, unlike UseAction next door in CastWatch.
		var target = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)mouseover.Address;
		bool can = ActionManager.CanUseActionOnTarget(actionId.Value, target);

		// ⭐⭐ MEASURED 2026-08-18, then obeyed -- it shipped "displayed, not obeyed" for exactly one
		// build. GetActionInRangeOrLoS returns a LogMessage ROW ID, and 0 means fine:
		//     0    (fine)                565/566  Target is not in range.
		//     562  Target not in line of sight.   563  Invalid target.
		// Confirmed by the strongest test available -- the SAME player read 566 across the zone and 0
		// up close, nothing changing but distance. And presses during the GCD still read 0, so it is
		// not contaminated by cooldown (572, "Cannot use yet", never appeared).
		//
		// ⚠⚠ OBEYING IT IS REQUIRED FOR PARITY, not a bonus. Ungated, an out-of-range mouseover
		// substitutes <mo>, the action fails, and nothing else happens -- while the thirteen-line
		// macro this replaces would have fallen through to its bare line and healed somebody. Without
		// this check /ifmo is a REGRESSION in exactly the case it claims to handle.
		uint rangeStatus = 0;
		var self = Plugin.Objects.LocalPlayer;
		if (self is not null)
			rangeStatus = ActionManager.GetActionInRangeOrLoS(
				actionId.Value,
				(FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)self.Address,
				target);

		bool reachable = rangeStatus == 0;
		Trace($"/ifmo: {name} ({actionId}) on {mouseover.Name}: CanUseActionOnTarget={can}, "
			+ $"rangeOrLoS={rangeStatus}{Explain(rangeStatus)}");

		if (!can)
			return (false, $"{name} cannot be used on {mouseover.Name}");
		if (!reachable)
			return (false, $"{mouseover.Name} is out of reach ({Reason(rangeStatus)})");

		return (true, $"{name} can be used on {mouseover.Name}");
	}

	// ── the tab section ──────────────────────────────────────────────────────────────────────

	public void DrawSection() {
		ImGui.TextWrapped(
			"Put {mo} where the target goes. It becomes <mo> when the action would actually land on "
			+ "your mouseover, and disappears otherwise -- which leaves an ordinary, untargeted line.");
		ImGui.Spacing();

		const string template = "/ifmo /ac Clemency {mo}";
		ImGui.TextUnformatted(template);
		if (ImGui.Button("Copy##ifmo"))
			ImGui.SetClipboardText(template);

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
