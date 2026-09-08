using System.Collections.Generic;

using Dalamud.Bindings.ImGui;

namespace DeserokUtils.Input;

internal static class KeybindsTab {
	public static string TabTitle => "Keybinds";

	public static string Summary => "Every key this plugin listens for, and what it is bound to.";

	public static void Draw(KeybindWatcher watcher, IReadOnlySet<string>? except = null) {
		var shown = 0;

		foreach (var (name, label, bind, repeats) in watcher.Entries) {
			if (bind is null || except?.Contains(name) == true)
				continue;

			if (shown++ > 0)
				ImGui.Separator();

			ImGui.TextUnformatted(label);
			if (KeybindPicker.Draw(name, bind, repeats))
				Plugin.Config.Save();
			ImGui.Spacing();
		}

		if (shown == 0)
			ImGui.TextDisabled("Every key belongs to a tweak; set them on the tweak itself.");

		ImGui.Separator();
		ImGui.TextWrapped(
			"Bound keys are read directly and do not use a hotbar slot. That also makes them the only "
			+ "way to press something during a conversation, when the hotbar is locked.");
		ImGui.Spacing();

		ImGui.TextWrapped(
			"Holding a key repeats it at its own rate rather than firing once per frame, so a key "
			+ "repeater and a held finger behave the same. Actions that make no sense repeated, like "
			+ "draw and sheathe, fire once however long you hold them.");
	}
}
