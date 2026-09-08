using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Textures;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace DeserokUtils.Features.PartyJobs;

internal sealed class PartyJobsFeature: IDisposable {
	public string TabTitle => "PartyJobs";

	public string Summary => "Fills in the job icon the party list leaves blank for members who are not in your zone.";

	private const uint JobIconBase = 62100;

	private static readonly TimeSpan RequestFloor = TimeSpan.FromSeconds(10);

	private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

	private DateTime lastRequest = DateTime.MinValue;
	private string lastNote = "nothing yet";
	private int drawnLastFrame;

	private Vector2 lastSize;

	public PartyJobsFeature() {
		Plugin.PluginInterface.UiBuilder.Draw += this.Draw;
		Plugin.ClientState.TerritoryChanged += this.OnTerritoryChanged;

		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsFinder", this.OnDutyWindow);
	}

	private void OnTerritoryChanged(uint territory) => this.Request("you changed zone");

	private void OnDutyWindow(AddonEvent type, AddonArgs args) => this.Request("duty finder opened");

	private unsafe void Request(string why) {
		if (!Plugin.Config.PartyJobsEnabled)
			return;

		if (DateTime.UtcNow - this.lastRequest < RequestFloor) {
			Plugin.Diag($"PartyJobs: skipping refresh ({why}) -- within {RequestFloor.TotalSeconds:0}s of the last.");
			return;
		}

		if (!AnyoneAway()) return;

		var proxy = InfoProxyPartyMember.Instance();
		if (proxy is null)
			return;

		this.lastRequest = DateTime.UtcNow;
		bool ok = proxy->RequestData();
		this.lastNote = $"refreshed ({why}) -> {ok}";
		Plugin.Diag($"PartyJobs: {this.lastNote}");
	}

	private static unsafe bool AnyoneAway() {
		var unit = Plugin.GameGui.GetAddonByName("_PartyList");
		if (unit.IsNull || !unit.IsVisible)
			return false;

		var addon = (AddonPartyList*)(nint)unit;
		var iconIds = addon->PartyClassJobIconId;

		for (int i = 0; i < addon->MemberCount && i < iconIds.Length; i++) {
			if (iconIds[i] == 0)
				return true;
		}
		return false;
	}

	private unsafe void Draw() {
		this.drawnLastFrame = 0;

		if (!Plugin.Config.PartyJobsEnabled)
			return;

		if (Plugin.PluginInterface.UiBuilder.CutsceneActive)
			return;

		if (Plugin.Config.PartyJobsPoll && DateTime.UtcNow - this.lastRequest >= PollInterval)
			this.Request("2-minute poll");

		var unit = Plugin.GameGui.GetAddonByName("_PartyList");

		if (unit.IsNull || !unit.IsVisible)
			return;

		var addon = (AddonPartyList*)(nint)unit;
		var iconIds = addon->PartyClassJobIconId;
		var rows = addon->PartyMembers;

		Dictionary<string, byte>? jobs = null;
		var list = ImGui.GetBackgroundDrawList();

		for (int i = 0; i < addon->MemberCount && i < rows.Length && i < iconIds.Length; i++) {
			if (iconIds[i] != 0)
				continue;

			var icon = rows[i].ClassJobIcon;
			if (icon is null || rows[i].Name is null)
				continue;

			jobs ??= ReadProxy();
			if (jobs.Count == 0)
				return;

			string rowText = rows[i].Name->NodeText.ExtractText();
			if (!TryMatch(jobs, rowText, out byte job) || job == 0)
				continue;

			var node = icon->AtkResNode;

			if (!Plugin.Textures.TryGetFromGameIcon(new GameIconLookup(JobIconBase + job), out var texture))
				continue;
			var wrap = texture.GetWrapOrEmpty();

			float scale = addon->AtkUnitBase.Scale * node.ScaleX;
			float scaleY = addon->AtkUnitBase.Scale * node.ScaleY;

			var min = new Vector2(node.ScreenX, node.ScreenY);
			var max = min + new Vector2(node.Width * scale, node.Height * scaleY);
			list.AddImage(wrap.Handle, min, max);

			this.lastSize = max - min;
			this.drawnLastFrame++;
		}
	}

	private static unsafe Dictionary<string, byte> ReadProxy() {
		var map = new Dictionary<string, byte>(StringComparer.Ordinal);
		var proxy = InfoProxyPartyMember.Instance();
		if (proxy is null)
			return map;

		for (uint i = 0; i < proxy->EntryCount; i++) {
			var entry = proxy->GetEntry(i);
			if (entry is null)
				continue;
			string name = entry->NameString;
			if (name.Length > 0)
				map[name] = entry->Job;
		}
		return map;
	}

	private static bool TryMatch(Dictionary<string, byte> jobs, string rowText, out byte job) {
		foreach (var pair in jobs) {
			if (rowText.EndsWith(pair.Key, StringComparison.Ordinal)) {
				job = pair.Value;
				return true;
			}
		}
		job = 0;
		return false;
	}

	public void DrawTab() {
		var poll = Plugin.Config.PartyJobsPoll;
		if (ImGui.Checkbox("Refresh every 2 minutes while someone is away##partyjobs_poll", ref poll)) {
			Plugin.Config.PartyJobsPoll = poll;
			Plugin.Config.Save();
		}

		if (ImGui.Button("Refresh now##partyjobs_refresh")) {
			this.lastRequest = DateTime.MinValue;
			this.Request("asked in the tab");
		}
	}

	public void DrawDiagnostics()
		=> ImGui.TextDisabled(
			$"drawing {this.drawnLastFrame} icon(s) at {this.lastSize.X:0}x{this.lastSize.Y:0}px"
			+ $" | {this.lastNote}");

	public void Dispose() {
		Plugin.PluginInterface.UiBuilder.Draw -= this.Draw;
		Plugin.ClientState.TerritoryChanged -= this.OnTerritoryChanged;
		Plugin.AddonLifecycle.UnregisterListener(this.OnDutyWindow);
	}
}
