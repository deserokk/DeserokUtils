using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.ClientState.Keys;

namespace DeserokUtils.Input;

/// <summary>
/// One key, optionally with modifiers, and how fast it repeats while held.
///
/// ## ⭐⭐ Why the plugin binds keys itself instead of telling you to make a macro
///
/// A macro lives in a hotbar slot, and **the hotbar is locked during a conversation** -- deserok
/// confirmed it by pressing one: *"hotbar is locked, no abilities fire or anything"*. So
/// <c>/dsuinteract</c> cannot run while an NPC is talking to you, which is exactly when Bunny wanted
/// to press it. Nothing was wrong with the code that advances dialogue; it was never being called.
///
/// He had already named the wider problem: *"I always found 'make a macro' a bit of a cop out,
/// plenty of mods allow you to bind things directly (our own targetting plugin allows custom
/// hotkeys)"*. He is right, and it had been a quiet tax on every command here -- each one costs a
/// hotbar slot, and this is a key you press constantly.
///
/// ⭐ A key read straight from <c>IKeyState</c> is not routed through the hotbar, so the lockout does
/// not apply. YesAlready's forced-yes hotkey works the same way with a dialog on screen, which is
/// evidence the game keeps updating its key state even while it refuses to fire actions.
/// </summary>
public sealed class Keybind {
	public VirtualKey Key { get; set; } = VirtualKey.NO_KEY;

	public bool Ctrl { get; set; }
	public bool Alt { get; set; }
	public bool Shift { get; set; }

	/// <summary>
	/// ⭐⭐ ACCESSIBILITY, and it is the reason this class has a clock in it at all.
	///
	/// deserok: *"maybe with a built in repeater for my disabled ass 'if key held, retry every
	/// second' or something. I have a hard time spamming buttons"*. Holding is the input he can
	/// actually give, so holding has to be a first-class way to press this, not a degraded one.
	///
	/// ⭐ It also solves the OPPOSITE problem for free, which is why there is one number here and not
	/// two. His key repeater turns a hold into a stream of roughly ten presses a second, and a
	/// keybound feature that treats each of those as a real press fires ten times. Because this is a
	/// floor on the gap between fires rather than an edge detector, a genuine hold and a 10Hz repeater
	/// stream produce exactly the same thing: one fire, then one per interval. The collapse the
	/// repeater needs and the repeat the hand needs are the same mechanism seen from two sides.
	/// </summary>
	public int RepeatMs { get; set; } = 1000;

	public bool IsBound => this.Key != VirtualKey.NO_KEY;

	public override string ToString() {
		if (!this.IsBound)
			return "unbound";
		string mods = (this.Ctrl ? "Ctrl+" : "") + (this.Alt ? "Alt+" : "") + (this.Shift ? "Shift+" : "");
		return mods + this.Key;
	}
}

/// <summary>
/// Polls the bound keys once a frame and runs what they are bound to.
///
/// ⚠ Deliberately NOT an edge detector. See <see cref="Keybind.RepeatMs"/> -- the thing that has to
/// be true is "at most one fire per interval", and edges cannot deliver that when the hardware is
/// manufacturing them.
/// </summary>
internal sealed class KeybindWatcher {
	private readonly List<(Func<Keybind?> Bind, Action Run, string Name, string Label)> bound = new();
	private readonly Dictionary<string, DateTime> lastFired = new();

	/// <summary>
	/// ⭐ The registry IS the tab. Anything registered here is bindable and shows up, so a new
	/// keybind is one Register call rather than a call plus a row somebody has to remember to add.
	/// </summary>
	public IEnumerable<(string Name, string Label, Keybind? Bind)> Entries =>
		this.bound.Select(b => (b.Name, b.Label, b.Bind()));

	/// <summary>
	/// ⚠ The binding is fetched through a delegate rather than stored, because it lives in the config
	/// and the config object is replaced wholesale when it reloads. A captured reference would go on
	/// watching a key nobody is bound to any more.
	/// </summary>
	public void Register(string name, string label, Func<Keybind?> bind, Action run) =>
		this.bound.Add((bind, run, name, label));

	/// <summary>
	/// ⚠⚠ SAMPLED ON THE RENDER THREAD, never read from one. ImGui state belongs to the draw
	/// callback, and this watcher runs in the framework update -- calling <c>ImGui.GetIO()</c> from
	/// there is a crash waiting for a slow frame. Plugin sets this during Draw; Tick only reads it.
	/// </summary>
	internal static bool TextInputActive;

	public void Tick() {
		// ⚠⚠ Never while you are typing. Dalamud suppresses key state during game text entry, but our
		// OWN ImGui windows are not the game, and a keybind that fires while you are naming a watched
		// status in a text box would be this plugin's most annoying bug.
		if (TextInputActive)
			return;

		var now = DateTime.UtcNow;

		foreach (var (getBind, run, name, _) in this.bound) {
			var bind = getBind();
			if (bind is null || !bind.IsBound)
				continue;

			if (!Held(bind))
				continue;

			// ⭐ One clock per binding, and the gap is checked BEFORE firing, so the first press of a
			// key that has been idle is instant -- the interval only ever delays a repeat.
			int gap = Math.Max(50, bind.RepeatMs);
			if (this.lastFired.TryGetValue(name, out var last) && (now - last).TotalMilliseconds < gap)
				continue;

			this.lastFired[name] = now;

			try {
				run();
			}
			catch (Exception ex) {
				// ⚠ A keybind runs every frame. An exception that escaped here would repeat at frame
				// rate and bury dalamud.log for every other feature sharing it.
				Plugin.Log.Error($"Keybind {name} ({bind}) threw: {ex}");
			}
		}
	}

	/// <summary>
	/// ⚠ Modifiers are checked as EXACT state, not as "at least these". Without that, a bind on G also
	/// fires for Ctrl+G, which is somebody else's shortcut.
	/// </summary>
	private static bool Held(Keybind bind) =>
		Plugin.Keys[bind.Key]
		&& Plugin.Keys[VirtualKey.CONTROL] == bind.Ctrl
		&& Plugin.Keys[VirtualKey.MENU] == bind.Alt
		&& Plugin.Keys[VirtualKey.SHIFT] == bind.Shift;
}
