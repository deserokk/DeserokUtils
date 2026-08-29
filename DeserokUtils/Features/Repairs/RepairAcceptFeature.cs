using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Repairs;

/// <summary>
/// Accepts incoming repair requests without the confirmation box.
///
/// ## Why this is safe to automate
///
/// Two facts make this a much smaller decision than it first appears, and both came from deserok
/// rather than from reasoning:
///
///  - **Only party members can request a repair at all.** The option does not exist otherwise, so
///    the "only accept from party members" rule everyone reaches for is already enforced by the
///    game. There is nothing here for a plugin to gate.
///  - **Self repair has no confirmation.** RepairRequest is therefore only ever somebody else
///    asking, so the trap that broke his melding : one addon serving several prompts, a yes-clicker
///    answering all of them : does not exist here.
///
/// ⚠ Unlike the meld auto-accept, this one COSTS something: dark matter, one grade-appropriate unit
/// per item. deserok carries thousands specifically for it, which is why he wants it automated at
/// all. It still defaults off, and it still says who it spent them on, because silently consuming
/// someone's items is not a thing to do quietly.
///
/// ## Recorded, not guessed
///
/// The prompt's values name the requester at [2] and their world at [3]. The Repair button sends a
/// single meaningful int: 14, with close=true. That matches the parameter deserok's YesAlready rule
/// was using, which is a pleasant confirmation rather than the source.
/// </summary>
internal sealed unsafe class RepairAcceptFeature: IDisposable {
	public string TabTitle => "Repairs";

	/// <summary>The value the Repair button sends. Recorded from a real click.</summary>
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
		if (!Plugin.Config.RepairAutoAccept || args.Addon.IsNull)
			return;

		var addon = (AtkUnitBase*)args.Addon.Address;
		if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 3)
			return;

		this.requester = addon->AtkValues[2].GetValueAsString();

		// ⚠ Never answered inside PostSetup. The addon is still being built, and deserok runs other
		// plugins that answer prompts; two replies in the same frame is how stacked dialogs happen.
		this.acceptPending = true;
	}

	private void OnUpdate(IFramework framework) {
		if (!this.acceptPending)
			return;

		this.acceptPending = false;

		var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("RepairRequest").Address;
		if (addon == null || !addon->IsVisible)
			return;

		// ⚠ Explicitly zeroed rather than trusting stackalloc. Firing a callback built on whatever
		// happened to be on the stack is not a risk worth taking when the button spends items.
		var values = stackalloc AtkValue[3];
		values[0] = default;
		values[1] = default;
		values[2] = default;
		values[0].SetInt(RepairButton);

		addon->FireCallback(3, values, true);

		Plugin.Chat.Print(this.requester.Length > 0
			? $"[Repairs] accepted a repair request from {this.requester}."
			: "[Repairs] accepted a repair request.");
	}

	public void DrawTab() {
		ImGui.TextWrapped(
			"Accepts repair requests without the confirmation box. Only party members can send one, "
			+ "so there is nobody else it could answer.");
		ImGui.Spacing();

		bool enabled = Plugin.Config.RepairAutoAccept;
		if (ImGui.Checkbox("Accept repair requests automatically", ref enabled)) {
			Plugin.Config.RepairAutoAccept = enabled;
			Plugin.Config.Save();
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Spends dark matter. Each accepted request is reported in chat.");
	}
}
