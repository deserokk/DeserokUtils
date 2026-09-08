using System;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Fanfare.Achievements;

internal sealed unsafe class PopupSuppressor: IDisposable {

	internal const string AddonName = "_TextAchievementUnlocked";

	private readonly IAddonLifecycle lifecycle;

	internal DateTime LastSuppressedAt { get; private set; } = DateTime.MinValue;

	internal PopupSuppressor(IAddonLifecycle lifecycle) {
		this.lifecycle = lifecycle;

		this.lifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, this.Suppress);
		this.lifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, this.Suppress);
		this.lifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, this.Suppress);

		Plugin.Log.Information($"suppressor: watching {AddonName}.");
	}

	private void Suppress(AddonEvent type, AddonArgs args) {
		if (!Plugin.Config.Fanfare.SuppressDefaultPopup)
			return;

		try {
			if (args.Addon.IsNull)
				return;

			var addon = (AtkUnitBase*)args.Addon.Address;
			if (addon is null || !addon->IsVisible)
				return;

			addon->IsVisible = false;
			this.LastSuppressedAt = DateTime.UtcNow;
		} catch (Exception ex) {

			Plugin.Log.Error(ex, "suppressor: failed to hide the popup");
		}
	}

	public void Dispose() => this.lifecycle.UnregisterListener(this.Suppress);
}
