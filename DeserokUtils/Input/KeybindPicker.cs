using System;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;

namespace DeserokUtils.Input;

internal static class KeybindPicker {
	private static string? capturing;

	private static readonly VirtualKey[] Ignored = [
		VirtualKey.NO_KEY, VirtualKey.CONTROL, VirtualKey.MENU, VirtualKey.SHIFT,
		VirtualKey.LCONTROL, VirtualKey.RCONTROL, VirtualKey.LMENU, VirtualKey.RMENU,
		VirtualKey.LSHIFT, VirtualKey.RSHIFT, VirtualKey.LBUTTON, VirtualKey.RBUTTON,
	];

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

		if (!repeats)
			return changed;

		float seconds = Math.Max(50, bind.RepeatMs) / 1000f;
		ImGui.SetNextItemWidth(180f);
		if (ImGui.SliderFloat($"repeat while held##{id}_rep", ref seconds, 0.05f, 3f, "%.2f s")) {
			bind.RepeatMs = (int)Math.Round(seconds * 1000f);
			changed = true;
		}

		return changed;
	}
}
