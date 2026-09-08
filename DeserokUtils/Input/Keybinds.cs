using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.ClientState.Keys;

namespace DeserokUtils.Input;

public sealed class Keybind {
	public VirtualKey Key { get; set; } = VirtualKey.NO_KEY;

	public bool Ctrl { get; set; }
	public bool Alt { get; set; }
	public bool Shift { get; set; }

	public int RepeatMs { get; set; } = 1000;

	public bool IsBound => this.Key != VirtualKey.NO_KEY;

	public override string ToString() {
		if (!this.IsBound)
			return "unbound";
		string mods = (this.Ctrl ? "Ctrl+" : "") + (this.Alt ? "Alt+" : "") + (this.Shift ? "Shift+" : "");
		return mods + this.Key.GetFancyName();
	}
}

internal sealed class KeybindWatcher {
	private readonly List<(Func<Keybind?> Bind, Action Run, string Name, string Label, bool Repeats)> bound = new();
	private readonly Dictionary<string, DateTime> lastFired = new();

	public IEnumerable<(string Name, string Label, Keybind? Bind, bool Repeats)> Entries =>
		this.bound.Select(b => (b.Name, b.Label, b.Bind(), b.Repeats));

	public void Register(string name, string label, Func<Keybind?> bind, Action run, bool repeats = true) =>
		this.bound.Add((bind, run, name, label, repeats));

	internal static bool TextInputActive;

	public void Tick() {

		if (TextInputActive)
			return;

		var now = DateTime.UtcNow;

		foreach (var (getBind, run, name, _, _) in this.bound) {
			var bind = getBind();
			if (bind is null || !bind.IsBound)
				continue;

			if (!Held(bind))
				continue;

			int gap = Math.Max(50, bind.RepeatMs);
			if (this.lastFired.TryGetValue(name, out var last) && (now - last).TotalMilliseconds < gap)
				continue;

			this.lastFired[name] = now;

			try {
				run();
			}
			catch (Exception ex) {

				Plugin.Log.Error($"Keybind {name} ({bind}) threw: {ex}");
			}
		}
	}

	private static bool Held(Keybind bind) =>
		Plugin.Keys[bind.Key]
		&& Plugin.Keys[VirtualKey.CONTROL] == bind.Ctrl
		&& Plugin.Keys[VirtualKey.MENU] == bind.Alt
		&& Plugin.Keys[VirtualKey.SHIFT] == bind.Shift;
}
