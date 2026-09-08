using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.MeldFlow;

internal sealed unsafe class MeldWindowKeeper: IDisposable {
	public string TabTitle => "MeldWindow";

	public string Summary => "Keeps the meld window open when melding for someone else, the way it already does for your own gear.";

	private static readonly TimeSpan DialogWindow = TimeSpan.FromSeconds(3);

	private DateTime lastDialogAt = DateTime.MinValue;

	private AgentMateriaAttach.FilterCategory lastCategory = AgentMateriaAttach.FilterCategory.None;

	private int openDialogs;

	private bool meldFinished;

	private int restoreTicks;

	private AgentMateriaAttach.FilterCategory categoryToRestore = AgentMateriaAttach.FilterCategory.None;

	public MeldWindowKeeper() {
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "MateriaAttachDialog", this.OnDialog);
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MateriaAttachDialog", this.OnDialogClosing);
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MateriaAttach", this.OnWindowClosing);
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "MateriaAttach", this.OnWindowOpened);
		Plugin.Framework.Update += this.OnUpdate;

	}

	public void Dispose() {
		Plugin.AddonLifecycle.UnregisterListener(this.OnDialog);
		Plugin.AddonLifecycle.UnregisterListener(this.OnWindowClosing);
		Plugin.AddonLifecycle.UnregisterListener(this.OnWindowOpened);
		Plugin.AddonLifecycle.UnregisterListener(this.OnDialogClosing);
		Plugin.Framework.Update -= this.OnUpdate;
	}

	private const int IncomingRequest = 4;

	private bool acceptPending;

	private void OnDialog(AddonEvent type, AddonArgs args) {
		this.lastDialogAt = DateTime.UtcNow;
		this.openDialogs++;

		if (!Plugin.Config.MeldAutoAccept || args.Addon.IsNull)
			return;

		var addon = (AtkUnitBase*)args.Addon.Address;
		if (addon == null || addon->AtkValues == null || addon->AtkValuesCount == 0)
			return;

		if (addon->AtkValues[0].Int != IncomingRequest)
			return;

		this.acceptPending = true;
	}

	private void AcceptIncoming() {
		var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("MateriaAttachDialog").Address;
		if (addon == null || !addon->IsVisible || addon->AtkValues == null || addon->AtkValuesCount == 0)
			return;

		if (addon->AtkValues[0].Int != IncomingRequest)
			return;

		var values = stackalloc AtkValue[3];
		values[0].SetInt(0);
		values[1].SetInt(0);
		values[2].SetInt(0);
		addon->FireCallback(3, values, true);
	}

	private void OnWindowClosing(AddonEvent type, AddonArgs args) {
		if (!Plugin.Config.MeldWindowKeepOpen)
			return;

		if (DateTime.UtcNow - this.lastDialogAt > DialogWindow)
			return;

		this.meldFinished = true;
	}

	private void OnDialogClosing(AddonEvent type, AddonArgs args) {
		if (this.openDialogs > 0)
			this.openDialogs--;

		if (!this.meldFinished || this.openDialogs > 0)
			return;

		this.meldFinished = false;
		this.Reopen();
	}

	private ulong crafterId;

	private void OnWindowOpened(AddonEvent type, AddonArgs args) {

		var target = Plugin.Targets.Target;
		if (target is { ObjectKind: ObjectKind.Pc })
			this.crafterId = target.GameObjectId;
	}

	private void Reopen() {
		if (this.crafterId == 0) {
			Plugin.Log.Information("[MeldWindow] no crafter recorded; not reopening.");
			return;
		}

		var crafter = Plugin.Objects.SearchById(this.crafterId);
		if (crafter == null) {
			Plugin.Log.Information("[MeldWindow] crafter is no longer nearby; not reopening.");
			return;
		}

		Plugin.Targets.Target = crafter;
		GameCommands.Queue("/meldrequest");
		this.categoryToRestore = this.lastCategory;
		this.restoreTicks = 30;
	}

	private static void SelectTab(AgentMateriaAttach.FilterCategory category) {

		var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("MateriaAttach").Address;
		if (addon == null || !addon->IsVisible)
			return;

		var values = stackalloc AtkValue[2];
		values[0].SetInt(0);
		values[1].SetInt((int)category);
		addon->FireCallback(2, values, true);
	}

	private void OnUpdate(IFramework framework) {
		if (this.acceptPending) {
			this.acceptPending = false;
			this.AcceptIncoming();
		}

		var agent = AgentMateriaAttach.Instance();
		if (agent == null)
			return;

		if (this.restoreTicks == 0) {
			if (agent->Category != AgentMateriaAttach.FilterCategory.None)
				this.lastCategory = agent->Category;

			return;
		}

		if (--this.restoreTicks > 0)
			return;

		if (this.categoryToRestore == AgentMateriaAttach.FilterCategory.None)
			return;

		SelectTab(this.categoryToRestore);
		this.lastCategory = this.categoryToRestore;

	}

	public void DrawTab() {
		bool enabled = Plugin.Config.MeldWindowKeepOpen;
		if (ImGui.Checkbox("Reopen the meld window after each meld", ref enabled)) {
			Plugin.Config.MeldWindowKeepOpen = enabled;
			Plugin.Config.Save();
		}

		ImGui.TextDisabled("Closing it yourself still closes it. Only a completed meld reopens it.");

		ImGui.Spacing();

		bool autoAccept = Plugin.Config.MeldAutoAccept;
		if (ImGui.Checkbox("Accept incoming meld requests automatically", ref autoAccept)) {
			Plugin.Config.MeldAutoAccept = autoAccept;
			Plugin.Config.Save();
		}
	}

	public static bool AnyEnabled
		=> Plugin.Config.MeldWindowKeepOpen || Plugin.Config.MeldAutoAccept;

	public static void SetEnabled(bool on) {
		Plugin.Config.MeldWindowKeepOpen = on;
		if (!on)
			Plugin.Config.MeldAutoAccept = false;

		Plugin.Config.Save();
	}
}
