using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace DeserokUtils;

/// <summary>
/// Two ways to run a text command, and WHICH ONE YOU PICK IS LOAD-BEARING.
///
/// ⚠⚠ Measured 2026-08-02, and it is the bug that made CastWatch look broken for a day: the queued
/// path is what every guide suggests and what TinyCommands uses, but it QUEUES. A /macrocancel sent
/// that way was still sitting in the queue when the macro executed its next line, so the line it
/// was meant to suppress went out anyway. The gate was correct; the suppression lost the race.
/// </summary>
internal static class GameCommands {
	/// <summary>
	/// Runs the command IMMEDIATELY, in-line, before the macro can advance.
	/// ✅ Use for anything that must beat the next macro line -- /macrocancel above all.
	/// ⚠ Bypasses the chatbox pipeline, so placeholders like &lt;t&gt; are NOT expanded.
	/// </summary>
	public static unsafe void RunNow(string line) {
		var shell = RaptureShellModule.Instance();
		UIModule* uiModule = UIModule.Instance();

		if (shell is not null && uiModule is not null) {
			using Utf8String cmd = new(line);
			shell->ExecuteCommandInner(&cmd, uiModule);
			return;
		}

		// Fall back rather than do nothing -- late beats never -- but SAY which path ran, or a
		// fixed race and an unfixed one look identical from outside.
		Plugin.Log.Warning("RaptureShellModule unavailable; falling back to the queued path.");
		Plugin.Diag("fell back to the QUEUED path (RaptureShellModule was null).");
		Queue(line);
	}

	/// <summary>
	/// Queues the command through the chatbox, exactly as if typed.
	/// ✅ Use for anything the USER wrote: this is the path that expands &lt;t&gt;, &lt;mo&gt;,
	/// &lt;2&gt; and friends, and its timing does not matter when nothing has to be outrun.
	/// </summary>
	public static unsafe void Queue(string line) {
		UIModule* uiModule = UIModule.Instance();
		if (uiModule is null) {
			// Report rather than swallow. A silent failure here means a line the user expected to
			// go out simply does not, with nothing anywhere saying so.
			Plugin.Log.Error("UIModule was null; could not run: " + line);
			Plugin.Chat.PrintError($"[DeserokUtils] could not run \"{line}\" (UIModule unavailable).");
			return;
		}

		using Utf8String utf8 = new(line);
		uiModule->ProcessChatBoxEntry(&utf8);
	}
}
