using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace DeserokUtils.Features.EphemeralMarks;

internal enum ContentGroup { None, Pvp, FieldOps, AllianceRaid, DeepDungeon }

internal sealed class MarkTracker: IDisposable {

	private const string PopAddon = "ContentsFinderConfirm";

	private readonly List<(string Name, uint World, bool Leader, int Slot)> snapshot = new();
	private readonly List<(ulong Id, bool Leader, string Tag, string Key)> resolved = new();
	private readonly Dictionary<uint, ContentGroup> groupCache = new();

	private DateTime lastResolve = DateTime.MinValue;
	private static readonly TimeSpan ResolveInterval = TimeSpan.FromSeconds(1);

	public ContentGroup Group { get; private set; } = ContentGroup.None;
	public bool Active { get; private set; }
	public IReadOnlyList<(string Name, uint World, bool Leader, int Slot)> Snapshot => this.snapshot;
	public int ResolvedCount => this.resolved.Count;
	public DateTime Captured { get; private set; } = DateTime.MinValue;

	public string? Idle { get; private set; } = "not in tracked content";

	public MarkTracker() {
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, PopAddon, this.OnPop);
		Plugin.ClientState.TerritoryChanged += this.OnTerritoryChanged;

		Plugin.ClientState.Login += this.Restore;

		this.Restore();

		this.OnTerritoryChanged(Plugin.ClientState.TerritoryType);
	}

	private void Restore() {

		if (this.snapshot.Count > 0)
			return;

		var saved = Plugin.Config.MarksLastParty;
		if (saved is null || saved.Members.Count == 0)
			return;

		if (saved.Owner != LocalContentId()) {
			Plugin.Log.Information("EphemeralMarks: the saved party belongs to another character; ignoring it.");
			return;
		}

		this.snapshot.Clear();
		foreach (var member in saved.Members)
			this.snapshot.Add((member.Name, member.World, member.Leader, member.Slot));

		this.Captured = saved.CapturedUtc;

		Plugin.Log.Information(
			$"EphemeralMarks: restored {this.snapshot.Count} name(s) captured "
			+ $"{(int)(DateTime.UtcNow - saved.CapturedUtc).TotalMinutes} minute(s) ago: "
			+ string.Join(", ", this.snapshot.Select(m => m.Name)));

		this.Evaluate();
	}

	private void Persist() {
		try {
			Plugin.Config.MarksLastParty = new MarkSnapshot {
				Owner = LocalContentId(),
				CapturedUtc = this.Captured,
				Members = this.snapshot
					.Select(m => new MarkSnapshotMember {
						Name = m.Name, World = m.World, Leader = m.Leader, Slot = m.Slot,
					})
					.ToList(),
			};

			Plugin.Config.Save();
		}
		catch (Exception ex) {

			Plugin.Log.Error(ex, "EphemeralMarks: could not write the party down.");
		}
	}

	private static unsafe ulong LocalContentId() {
		var state = PlayerState.Instance();
		return state is null ? 0ul : state->ContentId;
	}

	private void OnPop(AddonEvent type, AddonArgs args) {
		try {
			this.snapshot.Clear();
			uint self = Plugin.Objects.LocalPlayer?.EntityId ?? 0;

			uint leaderIndex = Plugin.Party.PartyLeaderIndex;

			for (int i = 0; i < Plugin.Party.Length; i++) {
				var member = Plugin.Party[i];
				if (member is null || member.EntityId == self)
					continue;
				string name = member.Name.TextValue;
				if (name.Length > 0)
					this.snapshot.Add((name, member.World.RowId, (uint)i == leaderIndex, i + 1));
			}

			var source = "party list";

			if (this.snapshot.Count == 0)
				source = this.CaptureCrossRealm(self);

			this.Captured = DateTime.UtcNow;
			Plugin.Log.Information(
				$"EphemeralMarks: queue popped in territory {Plugin.ClientState.TerritoryType}, "
				+ $"captured {this.snapshot.Count} name(s) via {source}: "
				+ (this.snapshot.Count > 0 ? string.Join(", ", this.snapshot.Select(s => s.Name)) : "(solo)"));

			this.Persist();

			SniffLog.Write(
				$"marks pop: territory={Plugin.ClientState.TerritoryType} "
				+ $"partyList={Plugin.Party.Length} crossRealm={CrossRealmCount()} "
				+ $"isCrossRealmParty={IsCrossRealmParty()} captured={this.snapshot.Count} via {source}");
		}
		catch (Exception ex) {

			Plugin.Log.Error(ex, "EphemeralMarks: failed to capture the party at the queue pop.");
		}
	}

	private unsafe string CaptureCrossRealm(uint self) {
		try {
			var proxy = InfoProxyCrossRealm.Instance();
			if (proxy is null)
				return "party list (empty, no proxy)";

			var count = InfoProxyCrossRealm.GetPartyMemberCount();
			if (count == 0)
				return "party list (empty, proxy empty)";

			for (uint i = 0; i < count; i++) {
				var member = InfoProxyCrossRealm.GetGroupMember(i, proxy->LocalPlayerGroupIndex);
				if (member is null || member->EntityId == self)
					continue;

				var name = member->NameString;
				if (!string.IsNullOrEmpty(name))
					this.snapshot.Add((name, (uint)member->HomeWorld, member->IsPartyLeader, (int)i + 1));
			}

			return "cross-world proxy";
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "EphemeralMarks: cross-world party read failed.");
			return "party list (empty, proxy threw)";
		}
	}

	private static unsafe int CrossRealmCount() {
		try {
			return InfoProxyCrossRealm.GetPartyMemberCount();
		}
		catch {
			return -1;
		}
	}

	private static unsafe string IsCrossRealmParty() {
		try {
			return InfoProxyCrossRealm.IsCrossRealmParty().ToString();
		}
		catch {
			return "?";
		}
	}

	private void OnTerritoryChanged(uint territory) {
		this.Group = this.GroupFor(territory);
		this.resolved.Clear();
		this.Evaluate();
	}

	private void Evaluate() {

		bool enabled = this.Group switch {
			ContentGroup.Pvp => Plugin.Config.MarksInPvp,
			ContentGroup.FieldOps => Plugin.Config.MarksInFieldOps,
			ContentGroup.AllianceRaid => Plugin.Config.MarksInAllianceRaid,
			ContentGroup.DeepDungeon => Plugin.Config.MarksInDeepDungeon,
			_ => false,
		};

		if (!enabled) {
			this.Active = false;
			this.Idle = this.Group == ContentGroup.None ? "not in tracked content" : $"{this.Group} is switched off";
			return;
		}

		if (this.snapshot.Count > Plugin.Config.MarksMaxGroupSize) {
			this.Active = false;
			this.Idle = $"queued with {this.snapshot.Count} people (over the {Plugin.Config.MarksMaxGroupSize} limit)";
			return;
		}

		if (this.snapshot.Count == 0) {
			this.Active = false;
			this.Idle = "queued alone";
			return;
		}

		this.Active = true;
		this.Idle = null;
	}

	public void Tick() {
		this.Evaluate();
		if (!this.Active)
			return;

		if (DateTime.UtcNow - this.lastResolve < ResolveInterval)
			return;
		this.lastResolve = DateTime.UtcNow;
		this.Resolve();
	}

	private void Resolve() {
		this.resolved.Clear();
		foreach (var obj in Plugin.Objects) {
			if (obj is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc)
				continue;

			foreach (var entry in this.snapshot) {
				if (entry.World == pc.HomeWorld.RowId
					&& string.Equals(entry.Name, pc.Name.TextValue, StringComparison.Ordinal)) {

					string world = "";
					try { world = pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? ""; } catch { }
					this.resolved.Add((pc.GameObjectId, entry.Leader, Tag(entry.Name, entry.Slot),
						$"{pc.Name.TextValue}@{world}"));
					break;
				}
			}
		}
	}

	private static string Tag(string name, int slot) {
		string initial = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
		return slot > 0 ? $"{initial}{slot}" : initial;
	}

	public IEnumerable<(Dalamud.Game.ClientState.Objects.Types.IGameObject Object, bool Leader, string Tag, string Key)> Marked() {
		foreach (var (id, leader, tag, key) in this.resolved) {
			var obj = Plugin.Objects.SearchById(id);
			if (obj is not null && obj.IsValid())
				yield return (obj, leader, tag, key);
		}
	}

	private ContentGroup GroupFor(uint territory) {
		if (this.groupCache.TryGetValue(territory, out var cached))
			return cached;

		uint use = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
			?.GetRowOrDefault(territory)?.TerritoryIntendedUse.RowId ?? uint.MaxValue;

		ContentGroup group = use switch {
			18 or 28 or 37 or 39 => ContentGroup.Pvp,
			26 or 41 or 47 or 48 or 52 or 53 or 60 or 61 => ContentGroup.FieldOps,
			8 => ContentGroup.AllianceRaid,

			31 => ContentGroup.DeepDungeon,

			_ => ContentGroup.None,
		};

		Plugin.Log.Information($"EphemeralMarks: territory {territory} intendedUse={use} -> {group}");
		return this.groupCache[territory] = group;
	}

	public void Dispose() {
		Plugin.AddonLifecycle.UnregisterListener(this.OnPop);
		Plugin.ClientState.TerritoryChanged -= this.OnTerritoryChanged;
		Plugin.ClientState.Login -= this.Restore;
	}
}
