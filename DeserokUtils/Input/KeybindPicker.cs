using System;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;

namespace DeserokUtils.Input;

/// <summary>
/// The "click here, then press a key" widget.
///
/// ⚠ Capture reads <c>IKeyState</c> rather than ImGui's keyboard, deliberately: the key has to be
/// recorded in the same vocabulary it will later be WATCHED in, or a key that binds fine will
/// silently never fire. Same trap as resolving a status name to an id in one place and matching it
/// in another.
/// </summary>
internal static class KeybindPicker {
	private static string? capturing;

	/// <summary>
	/// ⚠ Modifiers are never captured AS the key. Holding Ctrl to press Ctrl+G would otherwise bind
	/// Ctrl the instant it went down, before G was ever pressed.
	/// </summary>
	private static readonly VirtualKey[] Ignored = [
		VirtualKey.NO_KEY, VirtualKey.CONTROL, VirtualKey.MENU, VirtualKey.SHIFT,
		VirtualKey.LCONTROL, VirtualKey.RCONTROL, VirtualKey.LMENU, VirtualKey.RMENU,
		VirtualKey.LSHIFT, VirtualKey.RSHIFT, VirtualKey.LBUTTON, VirtualKey.RBUTTON,
	];

	/// <summary>Draws the picker. Returns true if the binding changed and wants saving.</summary>
	public static bool Draw(string id, Keybind bind, bool repeats = true) {
		bool changed = false;
		bool active = capturing == id;

		if (active) {
			ImGui.TextColored(new System.Numerics.Vector4(0.4f, 1f, 0.4f, 1f), "press a key...");
			ImGui.SameLine();
			if (ImGui.Button($"Cancel##{id}_cancel"))
				capturing = null;

			foreach (var key in Plugin.Keys.GetValidVirtualKeys()) {
				if (Ignored.Contains(key) || !Plugin.Keys[key])
					continue;

				// Escape clears the binding rather than binding Escape, which nobody wants.
				if (key == VirtualKey.ESCAPE) {
					bind.Key = VirtualKey.NO_KEY;
					bind.Ctrl = bind.Alt = bind.Shift = false;
				}
				else {
					bind.Key = key;
					bind.Ctrl = Plugin.Keys[VirtualKey.CONTROL];
					bind.Alt = Plugin.Keys[VirtualKey.MENU];
					bind.Shift = Plugin.Keys[VirtualKey.SHIFT];
				}

				capturing = null;
				changed = true;
				break;
			}
		}
		else {
			if (ImGui.Button($"{bind}##{id}_set"))
				capturing = id;
			ImGui.SameLine();
			ImGui.TextDisabled("click, then press a key (Esc clears)");
		}

		if (!bind.IsBound)
			return changed;

		// ⚠ No slider for an action that cannot repeat. A control for a setting that does nothing is
		// worse than no control: it invites you to tune something and then blames you when it has no
		// effect. Draw/sheathe fires once per press however long you hold it.
		if (!repeats) {
			ImGui.TextDisabled("fires once per press");
			return changed;
		}

		// ⭐ Seconds, not milliseconds. The setting exists for a hand, not for a tuning pass.
		float seconds = Math.Max(50, bind.RepeatMs) / 1000f;
		ImGui.SetNextItemWidth(180f);
		if (ImGui.SliderFloat($"repeat while held##{id}_rep", ref seconds, 0.05f, 3f, "%.2f s")) {
			bind.RepeatMs = (int)Math.Round(seconds * 1000f);
			changed = true;
		}

		return changed;
	}
}
