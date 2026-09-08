using System;
using System.Collections.Generic;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Text;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Repairs;

internal sealed unsafe class RepairAcceptFeature: IDisposable {
	public string TabTitle => "Repairs";

	public string Summary => "Accepts a party member's repair request without the confirmation box.";

	private const int RepairButton = 14;

	private bool acceptPending;

	private string requester = string.Empty;

	public RepairAcceptFeature() {
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RepairRequest", this.OnPrompt);
		Plugin.Framework.Update += this.OnUpdate;
	}

	public void Dispose() {
		Plugin.AddonLifecycle.UnregisterListener(this.OnPrompt);
		Plugin.Framework.Update -= this.OnUpdate;
	}

	private void OnPrompt(AddonEvent type, AddonArgs args) {
		if (args.Addon.IsNull)
			return;

		var addon = (AtkUnitBase*)args.Addon.Address;
		if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 3)
			return;

		if (!Plugin.Config.RepairAutoAccept)
			return;

		this.requester = FindRequester(addon);

		this.acceptPending = true;
	}

	private static string FindRequester(AtkUnitBase* addon) {
		if (addon->AtkValues == null)
			return string.Empty;

		var count = Math.Min(addon->AtkValuesCount, (uint)24);
		var seen = new List<string>();

		for (var i = 0; i < count; i++) {
			var text = addon->AtkValues[i].GetValueAsString();
			if (string.IsNullOrEmpty(text))
				continue;

			seen.Add($"[{i}]{text}");

			foreach (var member in Plugin.Party) {
				var name = member.Name.TextValue;
				if (name.Length == 0)
					continue;

				if (text.Contains(name, StringComparison.Ordinal))
					return name;
			}
		}

		Plugin.Log.Information(
			$"[Repairs] no party name among the prompt's strings. party={Plugin.Party.Length}, "
			+ $"strings: {string.Join(" | ", seen)}");

		return string.Empty;
	}

	private void OnUpdate(IFramework framework) {
		if (!this.acceptPending)
			return;

		this.acceptPending = false;

		var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("RepairRequest").Address;
		if (addon == null || !addon->IsVisible)
			return;

		var values = stackalloc AtkValue[3];
		values[0] = default;
		values[1] = default;
		values[2] = default;
		values[0].SetInt(RepairButton);

		addon->FireCallback(3, values, true);

		Plugin.Chat.Print(new XivChatEntry {
			Type = XivChatType.Echo,
			Message = this.requester.Length > 0
				? $"[Repairs] accepted a repair request from {this.requester}."
				: "[Repairs] accepted a repair request.",
		});
	}

	public void DrawTab() {
		bool enabled = Plugin.Config.RepairAutoAccept;
		if (ImGui.Checkbox("Accept repair requests automatically", ref enabled)) {
			Plugin.Config.RepairAutoAccept = enabled;
			Plugin.Config.Save();
		}
	}
}
