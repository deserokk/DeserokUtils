using System;
using System.Text;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.FcBuffs;

internal sealed unsafe class FcActionRecorder: IDisposable {

	private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(180);

	private const int MaxEntries = 2000;

	private static readonly string[] AlwaysNoisy = {
		"_LimitBreak", "ChatLog", "_ScreenInfo", "NamePlate", "_TargetCursor", "_ActionBar",
		"_PartyList", "_NaviMap", "AreaMap", "ScreenLog", "Tooltip", "Cursor", "DragDrop",
		"_Exp", "_Money", "_BagWidget", "_ParameterWidget", "_ToDoList", "ScenarioTree",
	};

	private string[] filters = Array.Empty<string>();

	private delegate bool FireCallbackDelegate(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close);

	private Hook<FireCallbackDelegate>? callbackHook;

	private bool armed;
	private DateTime armedAt;
	private int seen;

	public bool Armed => this.armed;

	private bool installed;

	private void Install() {
		if (this.installed)
			return;

		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, this.OnReceiveEvent);

		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, this.OnAddonOpen);
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, this.OnAddonClose);
		this.callbackHook ??= Plugin.Interop.HookFromAddress<FireCallbackDelegate>(
			AtkUnitBase.Addresses.FireCallback.Value, this.FireCallbackDetour);
		this.callbackHook.Enable();
		this.installed = true;
	}

	private void Uninstall() {
		if (!this.installed)
			return;

		Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, this.OnReceiveEvent);
		Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, this.OnAddonOpen);
		Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, this.OnAddonClose);
		this.callbackHook?.Disable();
		this.installed = false;
	}

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
			$"[FcBuffs] recording {shown} for 3 minutes. Do the sequence ONCE, "
			+ "then run /fcbuffs record again to stop.");
		SniffLog.Mark($"RECORDING ARMED ({shown})");
	}

	private void Disarm(string why) {
		if (!this.armed)
			return;
		this.armed = false;
		this.Uninstall();
		SniffLog.Mark($"RECORDING STOPPED ({why}) — {this.seen} event(s)");
		Plugin.Chat.Print(
			$"[FcBuffs] recording stopped ({why}) -- {this.seen} event(s), written to sniff.log.");
	}

	public void Tick() {
		if (this.armed && DateTime.UtcNow - this.armedAt > Expiry)
			this.Disarm("timed out");
	}

	private bool Matches(string? name) {
		if (name is null)
			return false;

		if (Array.Exists(AlwaysNoisy, n => name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
			return false;

		return this.filters.Length == 0
			|| Array.Exists(this.filters, f => name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
	}

	private bool FireCallbackDetour(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close) {
		try {
			if (this.armed && addon is not null && this.seen < MaxEntries) {
				string name = addon->NameString;
				if (this.Matches(name)) {
					this.seen++;

					SniffLog.Write(
						$"CALLBACK addon=\"{name}\" close={close} values=[{DescribeValues(valueCount, values)}]");
				}
			}
		}
		catch (Exception ex) {

			Plugin.Log.Error(ex, "FcBuffs recorder threw inside FireCallback");
		}

		return this.callbackHook!.Original(addon, valueCount, values, close);
	}

	internal static string DescribeValues(uint count, AtkValue* values) {
		if (values is null)
			return string.Empty;

		var sb = new StringBuilder();

		for (uint i = 0; i < count && i < 256; i++) {
			if (i > 0)
				sb.Append(", ");
			var v = values[i];
			sb.Append($"{i}:{v.Type}=");

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

	private void OnAddonOpen(AddonEvent type, AddonArgs args) {
		if (!this.armed || !this.Matches(args.AddonName)) return;

		SniffLog.Write($"OPEN  {args.AddonName}");
	}

	private void OnAddonClose(AddonEvent type, AddonArgs args) {
		if (!this.armed || !this.Matches(args.AddonName)) return;

		SniffLog.Write($"CLOSE {args.AddonName}");
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

		SniffLog.Write(
			$"  event addon=\"{args.AddonName}\" type={e.AtkEventType} param={e.EventParam}");
	}

	public void Dispose() {
		this.Uninstall();
		this.callbackHook?.Dispose();
	}
}
