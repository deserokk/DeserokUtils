using System;
using System.Text;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.FcBuffs;

/// <summary>
/// Records what the game does when YOU switch an FC buff on by hand, so the plugin can later do
/// the same thing without anybody having guessed at it.
///
/// ⚠⚠ This exists because the alternative is inventing a payload. Firing a made-up callback into
/// a window is not a thing that fails loudly -- it either does nothing, or it presses something
/// else. Neither reports itself, and both look exactly like "the feature is broken". So the button
/// press gets observed once, from a real click, and replayed verbatim.
///
/// ⚠⚠ TWO SOURCES, because the cheap one turned out not to carry the answer. AddonLifecycle gives
/// the UI event -- which named both windows and found the confirmation dialog -- but its EventParam
/// is NOT the row index: measured 2026-08-07, clicking six different buffs in the list produced
/// eventParam=2 every single time. It identifies the KIND of event, not the item. The row index is
/// in the AtkValues handed to FireCallback, so that gets hooked too.
/// </summary>
internal sealed unsafe class FcActionRecorder: IDisposable {
	/// <summary>Stops on its own. An armed recorder logging every UI event is not a thing to leave running.</summary>
	private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(45);

	/// <summary>⚠ A hard cap, because mouse-over alone produces events faster than anyone can read.</summary>
	private const int MaxEntries = 500;

	/// <summary>
	/// Addons that are NEVER logged. Everything else is.
	///
	/// ⚠⚠ A BLOCKLIST, and it took two identical failures to get here. Filtering by what to INCLUDE
	/// means naming the addons involved before you know what they are -- which is the entire thing
	/// the recorder exists to find out. It hid the confirmation dialog first (SelectYesno has no
	/// "compan" in it), and then, after "selectyesno" was added, it hid the context menu (ContextMenu
	/// has none of the three). Both times the recording looked complete. Both times a step was
	/// missing, and the second one was only found by an activation failing in front of deserok.
	///
	/// ⚠ Unfiltered is still not an option: the very first run burned its whole 300-event budget
	/// before the FC window opened, 182 of those from the limit-break gauge in a city where it is
	/// never drawn. But the noisy addons are KNOWN and finite, which the interesting ones are not --
	/// so exclude what has been measured as noise and keep everything else.
	/// </summary>
	private static readonly string[] AlwaysNoisy = {
		"_LimitBreak", "ChatLog", "_ScreenInfo", "NamePlate", "_TargetCursor", "_ActionBar",
		"_PartyList", "_NaviMap", "AreaMap", "ScreenLog", "Tooltip", "Cursor", "DragDrop",
		"_Exp", "_Money", "_BagWidget", "_ParameterWidget", "_ToDoList", "ScenarioTree",
	};

	/// <summary>Optional allowlist. Empty means "everything except <see cref="AlwaysNoisy"/>".</summary>
	private string[] filters = Array.Empty<string>();

	/// <summary>
	/// Signature taken from the InteropGenerator delegate in the installed FFXIVClientStructs, NOT
	/// from memory: AtkUnitBase.FireCallback(UInt32 valueCount, AtkValue* values, Boolean close).
	/// If a patch changes it, this fails to compile instead of silently mis-reading arguments.
	/// </summary>
	private delegate bool FireCallbackDelegate(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close);

	private Hook<FireCallbackDelegate>? callbackHook;

	private bool armed;
	private DateTime armedAt;
	private int seen;

	public bool Armed => this.armed;

	/// <summary>
	/// ⚠⚠ Nothing is installed until the recorder is ARMED. Both of these sit in genuinely hot
	/// paths -- the listener fires on every addon event in the game, the hook on every UI callback --
	/// and they existed to be used for about thirty seconds, twice. Leaving them enabled costs a
	/// branch on every one of those, permanently, for a diagnostic that is almost never running.
	///
	/// ⭐ The cheapest branch is the one that is not there.
	/// </summary>
	private bool installed;

	private void Install() {
		if (this.installed)
			return;

		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, this.OnReceiveEvent);
		this.callbackHook ??= Plugin.Interop.HookFromAddress<FireCallbackDelegate>(
			AtkUnitBase.Addresses.FireCallback.Value, this.FireCallbackDetour);
		this.callbackHook.Enable();
		this.installed = true;
	}

	private void Uninstall() {
		if (!this.installed)
			return;

		Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, this.OnReceiveEvent);
		this.callbackHook?.Disable();
		this.installed = false;
	}

	/// <param name="nameFilter">
	/// Comma-separated substrings an addon's name must contain. Empty keeps the previous set --
	/// a filter that matches everything is available but has to be asked for by name, because it
	/// is the setting that made the first recording useless.
	/// </param>
	public void Toggle(string nameFilter) {
		if (this.armed) {
			this.Disarm("stopped by hand");
			return;
		}

		this.filters = nameFilter.Length > 0
			? nameFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			: Array.Empty<string>();

		this.Install();
		this.armed = true;
		this.armedAt = DateTime.UtcNow;
		this.seen = 0;
		string shown = this.filters.Length == 0 ? "everything except known noise" : string.Join(", ", this.filters);
		Plugin.Chat.Print(
			$"[FcBuffs] recording {shown} for 45s. Switch a buff on ONCE, "
			+ "then run /fcbuffs record again to stop.");
		Plugin.Log.Information($"FcBuffs recorder ARMED ({shown})");
	}

	private void Disarm(string why) {
		if (!this.armed)
			return;
		this.armed = false;
		this.Uninstall();
		Plugin.Chat.Print($"[FcBuffs] recording stopped ({why}) -- {this.seen} event(s). See dalamud.log.");
		Plugin.Log.Information($"FcBuffs recorder DISARMED ({why}), {this.seen} event(s)");
	}

	/// <summary>Called from the feature's tick so an armed recorder cannot outlive its window.</summary>
	public void Tick() {
		if (this.armed && DateTime.UtcNow - this.armedAt > Expiry)
			this.Disarm("timed out");
	}

	private bool Matches(string? name) {
		if (name is null)
			return false;

		if (Array.Exists(AlwaysNoisy, n => name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
			return false;

		// No allowlist means everything that survived the blocklist.
		return this.filters.Length == 0
			|| Array.Exists(this.filters, f => name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
	}

	/// <summary>
	/// The payload the game actually sends. This is the thing worth replaying -- everything else in
	/// this class is context around it.
	/// </summary>
	private bool FireCallbackDetour(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close) {
		try {
			if (this.armed && addon is not null && this.seen < MaxEntries) {
				string name = addon->NameString;
				if (this.Matches(name)) {
					this.seen++;
					Plugin.Log.Information(
						$"FcBuffs CALLBACK: addon=\"{name}\" close={close} values=[{DescribeValues(valueCount, values)}]");
				}
			}
		}
		catch (Exception ex) {
			// ⚠ Never let a diagnostic take the game with it. This runs inside every UI callback in
			// the client, so a throw here is not a broken recorder, it is a broken game.
			Plugin.Log.Error(ex, "FcBuffs recorder threw inside FireCallback");
		}

		return this.callbackHook!.Original(addon, valueCount, values, close);
	}

	internal static string DescribeValues(uint count, AtkValue* values) {
		if (values is null)
			return string.Empty;

		var sb = new StringBuilder();
		// ⚠ 256, not 32. The action list is an inventory of owned items -- thirteen of a possible
		// fifteen -- so a cap sized for a small dialog would truncate exactly the rows being looked
		// for, and truncation at the end of a list is invisible.
		for (uint i = 0; i < count && i < 256; i++) {
			if (i > 0)
				sb.Append(", ");
			var v = values[i];
			sb.Append($"{i}:{v.Type}=");
			// ⚠ AtkValueType, not ValueType -- the short name collides with System.ValueType and
			// resolves to it silently until every case label fails to compile.
			switch (v.Type) {
				case AtkValueType.Int: sb.Append(v.Int); break;
				case AtkValueType.UInt: sb.Append(v.UInt); break;
				case AtkValueType.Bool: sb.Append(v.Bool); break;
				case AtkValueType.Float: sb.Append(v.Float); break;
				case AtkValueType.String:
				case AtkValueType.ConstString:
				case AtkValueType.ManagedString:
					sb.Append('"').Append(v.String.ToString()).Append('"');
					break;
				default: sb.Append(v.Int); break;
			}
		}
		return sb.ToString();
	}

	private void OnReceiveEvent(AddonEvent type, AddonArgs args) {
		if (!this.armed || !this.Matches(args.AddonName))
			return;

		if (this.seen >= MaxEntries) {
			this.Disarm("hit the entry cap");
			return;
		}

		if (args is not AddonReceiveEventArgs e)
			return;

		this.seen++;

		// ⭐ To the log only, never to chat. Hundreds of UI events in the chat box would bury the one
		// line that matters -- and the log is the thing that survives to be grepped.
		Plugin.Log.Information(
			$"FcBuffs rec: addon=\"{args.AddonName}\" atkEventType={e.AtkEventType} eventParam={e.EventParam}");
	}

	public void Dispose() {
		this.Uninstall();
		this.callbackHook?.Dispose();
	}
}
