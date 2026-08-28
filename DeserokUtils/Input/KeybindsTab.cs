using Dalamud.Bindings.ImGui;

namespace DeserokUtils.Input;

/// <summary>
/// Every key this plugin can bind, in one place.
///
/// ⭐ deserok asked for it once the first bind proved itself: *"it would be nice to have a keybinds
/// tab"*. It draws whatever <see cref="KeybindWatcher.Entries"/> reports, so adding a bind is one
/// Register call and nothing here changes.
/// </summary>
internal static class KeybindsTab {
	public static string TabTitle => "Keybinds";

	public static void Draw(KeybindWatcher watcher) {
		ImGui.TextWrapped(
			"Bound keys are read directly and do not use a hotbar slot. That also makes them the only "
			+ "way to press something during a conversation, when the hotbar is locked.");
		ImGui.Spacing();

		foreach (var (name, label, bind) in watcher.Entries) {
			if (bind is null)
				continue;

			ImGui.Separator();
			ImGui.TextUnformatted(label);
			if (KeybindPicker.Draw(name, bind))
				Plugin.Config.Save();
			ImGui.Spacing();
		}

		ImGui.Separator();

		// ⚠ A fact about what the slider does, not an explanation of why it exists. The reasoning
		// lives in Keybind.RepeatMs, where it is long and belongs.
		ImGui.TextWrapped(
			"Holding a key repeats it at its own rate rather than firing once per frame, so a key "
			+ "repeater and a held finger behave the same.");
	}
}
