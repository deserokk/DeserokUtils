using System;
using System.Collections.Generic;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Text;
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
/// The Repair button sends a single meaningful int: 14, with close=true. That matches the parameter
/// deserok's YesAlready rule was using, which is a pleasant confirmation rather than the source.
///
/// ⚠ The requester's NAME is not at a fixed index, despite the first recording putting it at [2].
/// See FindRequester : that assumption shipped and was wrong on its first real use.
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
		if (args.Addon.IsNull)
			return;

		var addon = (AtkUnitBase*)args.Addon.Address;
		if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 3)
			return;

		if (!Plugin.Config.RepairAutoAccept)
			return;

		this.requester = FindRequester(addon);

		// ⚠ Never answered inside PostSetup. The addon is still being built, and deserok runs other
		// plugins that answer prompts; two replies in the same frame is how stacked dialogs happen.
		this.acceptPending = true;
	}

	/// <summary>
	/// Finds who sent the request by matching the prompt's strings against the party roster.
	///
	/// ⚠⚠ NOT a fixed index. The recording that this feature was built from had the requester at
	/// AtkValues[2] and their world at [3], and shipping that read the wrong value on the very first
	/// real use : it announced the item being repaired instead of the person. The prompt's layout
	/// evidently shifts with the request, so an index recorded from one sample is a coincidence, not
	/// a contract.
	///
	/// ⭐ Matching against the party is the sound version precisely because of the rule that made
	/// the party gate unnecessary: only party members can send a repair request, so the requester's
	/// name is guaranteed to be in the roster. Whichever string matches IS the requester, wherever
	/// it happens to sit.
	/// </summary>
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

				// ⚠ Contains, not equality. Strings in these prompts arrive SeString-encoded with
				// link payloads wrapped around them : the meld recording showed item names as
				// "H%I&Vana'dielian Vest of Aiming IH" : so an exact match against a
				// clean roster name never fires even when the name is right there.
				if (text.Contains(name, StringComparison.Ordinal))
					return name;
			}
		}

		// ⭐ Diagnoses itself rather than needing another sniffer. If the name genuinely is not in
		// the values, this says so once, with what WAS there, and the next attempt can read the
		// text nodes instead.
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

		// ⚠ Explicitly zeroed rather than trusting stackalloc. Firing a callback built on whatever
		// happened to be on the stack is not a risk worth taking when the button spends items.
		var values = stackalloc AtkValue[3];
		values[0] = default;
		values[1] = default;
		values[2] = default;
		values[0].SetInt(RepairButton);

		addon->FireCallback(3, values, true);

		// ⚠⚠ Echo, not Chat.Print(string). The plain overload lands in the DEBUG channel, which is
		// hidden by default and is hidden for deserok : so the one notice that says his dark matter
		// was just spent was being written somewhere he could not see it. A message about consuming
		// someone's items has to appear in front of them.
		Plugin.Chat.Print(new XivChatEntry {
			Type = XivChatType.Echo,
			Message = this.requester.Length > 0
				? $"[Repairs] accepted a repair request from {this.requester}."
				: "[Repairs] accepted a repair request.",
		});
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
