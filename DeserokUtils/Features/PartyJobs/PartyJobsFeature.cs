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

/// <summary>
/// Fill in the job icon the party list leaves blank for members who are not in your zone.
///
/// ## The complaint
///
/// A party member elsewhere shows as <c>Lv??? Peachi Bunni</c> with an empty job slot, so a roulette
/// starts with *"which job are you? are you tanking?"* in Discord every single time. deserok, on the
/// irony: *"cross world parties already do this natively lol, but then you can't do world things
/// together... job updates real time in that system"*. The one kind of party that can do overworld
/// content together is the one the game refuses to tell you about.
///
/// ## ⭐⭐ Nothing here is written into the party list
///
/// His design, and it is the safer one by a distance: *"what if we don't put it in the party frame
/// directly, instead simply overlaying the job icon in the blank spot."* We never touch the addon's
/// nodes -- we read where the blank icon WOULD be and draw over it. A read that fails draws nothing;
/// a write that fails fights every other plugin that touches the party list, and breaks on patches.
///
/// ## ⭐ Everything below was measured on 2026-08-28, not assumed
///
/// <code>
/// blank slot   iconId == 0, node hidden but still positioned
/// draw at      node ScreenX/ScreenY, 32x32 (pre-resolved, no HUD-scale maths)
/// draw what    62100 + job    (job 19 -> 62119, job 34 -> 62134, both rows agreed)
/// match rows   EndsWith -- the row reads "??? Peachi Bunni", the proxy says "Peachi Bunni"
/// </code>
///
/// ## ⚠⚠ The proxy is STALE until asked, and that is the whole design constraint
///
/// <see cref="InfoProxyPartyMember"/> does not update on its own. Not job, not even location:
/// measured with a party member who changed job AND zone, the proxy went on reporting the old job
/// and the old territory until <c>RequestData()</c> was called, at which point both corrected within
/// seconds.
///
/// ⚠ An earlier reading of this was WRONG and is worth recording as such: location appeared to
/// update by itself once, which was almost certainly an unnoticed Social window doing the same call.
/// One observation, uncontrolled. The real rule is that nothing refreshes without asking.
///
/// ⚠⚠ And <c>RequestData()</c> goes to the SERVER. So it is never on a timer. It fires only when
/// something happened that makes the answer matter, and only when there is a blank slot that wants
/// filling -- if everybody is in your zone, this feature makes no requests at all, ever.
/// </summary>
internal sealed class PartyJobsFeature: IDisposable {
	public string TabTitle => "PartyJobs";

	/// <summary>⭐ Measured, not the assumed 062100 formula -- an in-zone row reported exactly this.</summary>
	private const uint JobIconBase = 62100;

	/// <summary>
	/// ⚠ A floor on how often the server can be asked, independent of how many things ask. Every
	/// trigger below is an event rather than a poll, but events can arrive in bursts -- zoning into a
	/// duty fires several at once -- and the floor is what turns a burst into one request.
	/// </summary>
	private static readonly TimeSpan RequestFloor = TimeSpan.FromSeconds(10);

	private DateTime lastRequest = DateTime.MinValue;
	private string lastNote = "nothing yet";
	private int drawnLastFrame;

	/// <summary>⭐ Shown in the tab so a scale problem is VISIBLE rather than something you squint at.
	/// At 100% it should read 32x32; change the party list scale and it should follow.</summary>
	private Vector2 lastSize;

	public PartyJobsFeature() {
		Plugin.PluginInterface.UiBuilder.Draw += this.Draw;
		Plugin.ClientState.TerritoryChanged += this.OnTerritoryChanged;

		// ⭐ deserok's suggestion, and it is the right trigger rather than a convenient one: *"it's
		// relevant when the duty window is being opened so maybe hook onto that instead of polling"*.
		// This is the exact moment the answer is wanted, and it is a few seconds before it is needed,
		// which matters because the refresh is a round trip rather than a read.
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsFinder", this.OnDutyWindow);
	}

	private void OnTerritoryChanged(uint territory) => this.Request("you changed zone");

	private void OnDutyWindow(AddonEvent type, AddonArgs args) => this.Request("duty finder opened");

	/// <summary>
	/// Ask the server for current party details.
	///
	/// ⚠ Refuses if nothing is out of zone. The only reason to spend a request is a slot we cannot
	/// otherwise fill, so a party standing together never generates traffic.
	/// </summary>
	private unsafe void Request(string why) {
		if (!Plugin.Config.PartyJobsEnabled)
			return;

		if (DateTime.UtcNow - this.lastRequest < RequestFloor) {
			Plugin.Diag($"PartyJobs: skipping refresh ({why}) -- within {RequestFloor.TotalSeconds:0}s of the last.");
			return;
		}

		if (!AnyoneAway()) {
			Plugin.Diag($"PartyJobs: no refresh needed ({why}) -- nobody is out of zone.");
			return;
		}

		var proxy = InfoProxyPartyMember.Instance();
		if (proxy is null)
			return;

		this.lastRequest = DateTime.UtcNow;
		bool ok = proxy->RequestData();
		this.lastNote = $"refreshed ({why}) -> {ok}";
		Plugin.Diag($"PartyJobs: {this.lastNote}");
	}

	/// <summary>True if any party row is missing its job icon, which is the only thing worth asking about.</summary>
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

		var unit = Plugin.GameGui.GetAddonByName("_PartyList");

		// ⚠⚠ IsVisible is what keeps a job icon from being painted over a cutscene or a loading
		// screen. The party list going away is the ONLY signal we get that the HUD is hidden, since
		// we draw to the background list rather than into the addon.
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

			// ⭐ Built once, and only if a blank row actually turned up. A party standing together
			// costs nothing but the loop above.
			jobs ??= ReadProxy();
			if (jobs.Count == 0)
				return;

			string rowText = rows[i].Name->NodeText.ExtractText();
			if (!TryMatch(jobs, rowText, out byte job) || job == 0)
				continue;

			var node = icon->AtkResNode;
			var texture = Plugin.Textures.GetFromGameIcon(new GameIconLookup(JobIconBase + job));
			var wrap = texture.GetWrapOrEmpty();

			// ⚠⚠ SCALE, and only the SIZE needs it. deserok flagged this before it shipped: *"we'll
			// need to watch for is making sure the drawn icons match the chosen UI scale for the party
			// element."* ScreenX/ScreenY are already resolved, so the position is right at any scale --
			// but Width and Height are the node's own unscaled 32x32, so at 90% or 120% HUD scale the
			// icon would land in exactly the right place at exactly the wrong size, which reads as a
			// misalignment rather than as a scaling bug.
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

	/// <summary>
	/// ⚠ EndsWith, not equality. The party list row carries the level placeholder inside the same text
	/// node -- <c>"??? Peachi Bunni"</c> out of zone, <c>" Nuvok Stone"</c> in it -- so an equality
	/// check matches nothing at all and the feature simply never draws, with no error anywhere.
	/// </summary>
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
		ImGui.TextWrapped(
			"The party list leaves the job icon blank for anyone outside your zone. This draws it back "
			+ "in, so nobody has to ask who is tanking before a roulette.");
		ImGui.Spacing();

		bool on = Plugin.Config.PartyJobsEnabled;
		if (ImGui.Checkbox("Show jobs for party members elsewhere##partyjobs", ref on)) {
			Plugin.Config.PartyJobsEnabled = on;
			Plugin.Config.Save();
		}

		ImGui.Spacing();
		ImGui.TextWrapped(
			"The game only tells the client who is playing what when asked, so this asks when you open "
			+ "the duty finder or change zone, and never while everyone is together.");
		ImGui.Spacing();
		ImGui.TextDisabled($"drawing {this.drawnLastFrame} icon(s) at {this.lastSize.X:0}x{this.lastSize.Y:0}px | {this.lastNote}");

		if (ImGui.Button("Refresh now##partyjobs_refresh")) {
			this.lastRequest = DateTime.MinValue;
			this.Request("asked in the tab");
		}
	}

	public void Dispose() {
		Plugin.PluginInterface.UiBuilder.Draw -= this.Draw;
		Plugin.ClientState.TerritoryChanged -= this.OnTerritoryChanged;
		Plugin.AddonLifecycle.UnregisterListener(this.OnDutyWindow);
	}
}
