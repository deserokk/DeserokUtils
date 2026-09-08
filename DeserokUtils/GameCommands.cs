using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace DeserokUtils;

internal static class GameCommands {

	public static unsafe void RunNow(string line) {
		var shell = RaptureShellModule.Instance();
		UIModule* uiModule = UIModule.Instance();

		if (shell is not null && uiModule is not null) {
			using Utf8String cmd = new(line);
			shell->ExecuteCommandInner(&cmd, uiModule);
			return;
		}

		Plugin.Log.Warning("RaptureShellModule unavailable; falling back to the queued path.");
		Plugin.Diag("fell back to the QUEUED path (RaptureShellModule was null).");
		Queue(line);
	}

	public static unsafe void Queue(string line) {
		UIModule* uiModule = UIModule.Instance();
		if (uiModule is null) {

			Plugin.Log.Error("UIModule was null; could not run: " + line);
			Plugin.Chat.PrintError($"[DeserokUtils] could not run \"{line}\" (UIModule unavailable).");
			return;
		}

		using Utf8String utf8 = new(line);
		uiModule->ProcessChatBoxEntry(&utf8);
	}
}
