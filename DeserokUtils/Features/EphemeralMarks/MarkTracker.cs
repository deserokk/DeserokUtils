using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace DeserokUtils.Features.EphemeralMarks;

/// <summary>Which broad kind of content you are standing in, for the three grouped toggles.</summary>
internal enum ContentGroup { None, Pvp, FieldOps, AllianceRaid }

/// <summary>
/// Remembers who you queued with, and finds them again once you are inside.
///
/// ## ⭐⭐ ONE READ, AT ONE EVENT. No polling at all.
///
/// **The game freezes your party the moment you queue.** deserok: *"you cannot change a party after
/// you join the queue, adding a party member cancels the queue, and removing a party member does the
/// same."* So there is nothing to poll for -- the party between queueing and entering is immutable,
/// and a single read at the pop is the entire mechanism.
///
/// ⭐ That also makes the event choice low-risk rather than critical. Since the party cannot change
/// in that window, a snapshot at queue-join, at the pop, or at accept would all be the SAME DATA.
/// The hook only has to exist; it does not have to be the precise right moment.
///
/// ⭐ And a rejected queue -- somebody did not click accept -- simply re-pops and overwrites with
/// identical data. No rejection handling, no withdrawal handling, no staleness. The states that would
/// need cleaning up are states where the data is the same anyway.
///
/// ⚠⚠ THE FIRST VERSION POLLED, AND IT WAS WRONG TWICE OVER. It rebuilt the party list every frame
/// -- ~480 string allocations a second for data that changes once an hour, the per-frame audit
/// committed again in a file whose comments cite it -- and it was solving a problem the game already
/// solves. deserok caught the polling before it ever ran, then removed the need for it entirely:
/// *"what we want is the thinnest version of each feature."*
///
/// ## ⚠⚠ Names and home worlds. Never content or account ids.
///
/// Nothing is persisted. That keeps this a HUD aid -- it shows where somebody standing in front of
/// you is, which the party list already tells you -- rather than anything that could know where a
/// person is later. See the plugin-safety section of ORIENTATION.md.
/// </summary>
internal sealed class MarkTracker: IDisposable {
	/// <summary>
	/// The duty-ready dialog. ⚠ If this addon name is wrong the snapshot simply never fills and the
	/// tab says "came in alone" -- which is why <see cref="Captured"/> is logged loudly when it fires.
	/// </summary>
	private const string PopAddon = "ContentsFinderConfirm";

	private readonly List<(string Name, uint World, bool Leader)> snapshot = new();
	private readonly List<(ulong Id, bool Leader, string Tag)> resolved = new();
	private readonly Dictionary<uint, ContentGroup> groupCache = new();

	private DateTime lastResolve = DateTime.MinValue;
	private static readonly TimeSpan ResolveInterval = TimeSpan.FromSeconds(1);

	public ContentGroup Group { get; private set; } = ContentGroup.None;
	public bool Active { get; private set; }
	public IReadOnlyList<(string Name, uint World, bool Leader)> Snapshot => this.snapshot;
	public int ResolvedCount => this.resolved.Count;
	public DateTime Captured { get; private set; } = DateTime.MinValue;

	/// <summary>Why marks are not showing, or null if they are.</summary>
	public string? Idle { get; private set; } = "not in tracked content";

	public MarkTracker() {
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, PopAddon, this.OnPop);
		Plugin.ClientState.TerritoryChanged += this.OnTerritoryChanged;
		this.OnTerritoryChanged(Plugin.ClientState.TerritoryType);
	}

	/// <summary>
	/// The queue popped. Read the party once.
	///
	/// ⚠ Overwrites unconditionally, including on a re-pop after somebody declined. That is correct
	/// rather than lazy: the party cannot have changed, so the new read is identical.
	/// </summary>
	private void OnPop(AddonEvent type, AddonArgs args) {
		try {
			this.snapshot.Clear();
			uint self = Plugin.Objects.LocalPlayer?.EntityId ?? 0;

			// ⭐ Leader recorded HERE, at capture, rather than tracked live. It cannot change anyway --
			// the party is frozen from the moment you queue -- so a live read would be extra machinery
			// for an answer that is already fixed.
			uint leaderIndex = Plugin.Party.PartyLeaderIndex;

			for (int i = 0; i < Plugin.Party.Length; i++) {
				var member = Plugin.Party[i];
				if (member is null || member.EntityId == self)
					continue;
				string name = member.Name.TextValue;
				if (name.Length > 0)
					this.snapshot.Add((name, member.World.RowId, (uint)i == leaderIndex));
			}

			this.Captured = DateTime.UtcNow;
			Plugin.Log.Information($"EphemeralMarks: queue popped, captured {this.snapshot.Count} name(s): "
				+ (this.snapshot.Count > 0 ? string.Join(", ", this.snapshot.Select(s => s.Name)) : "(solo)"));
		}
		catch (Exception ex) {
			// ⚠ Never throw into an addon callback. Log rather than swallow, or a broken capture is
			// indistinguishable from queueing alone.
			Plugin.Log.Error(ex, "EphemeralMarks: failed to capture the party at the queue pop.");
		}
	}

	/// <summary>
	/// ⭐ Event-driven, so nothing asks "where am I" per frame. The group is recomputed exactly when
	/// the answer can change.
	/// </summary>
	private void OnTerritoryChanged(uint territory) {
		this.Group = this.GroupFor(territory);
		this.resolved.Clear();
		this.Evaluate();
	}

	private void Evaluate() {
		// ⚠ A TESTING OVERRIDE, and deliberately not a content type. Tuning the marker's shape, size
		// and head height needs somewhere convenient with another player in it -- a Trial, say -- and
		// adding Trial to the real list to get that would leave the list describing where the feature
		// is useful AND where it was once convenient to test. Those are different facts.
		bool enabled = Plugin.Config.MarksEverywhere || this.Group switch {
			ContentGroup.Pvp => Plugin.Config.MarksInPvp,
			ContentGroup.FieldOps => Plugin.Config.MarksInFieldOps,
			ContentGroup.AllianceRaid => Plugin.Config.MarksInAllianceRaid,
			_ => false,
		};

		if (!enabled) {
			this.Active = false;
			this.Idle = this.Group == ContentGroup.None ? "not in tracked content" : $"{this.Group} is switched off";
			return;
		}

		// ⚠ The degeneracy guard. When the premade IS the whole group -- Chaotic Raid, a static, a
		// premade dungeon -- marking everyone is the same as marking nobody. This is why Chaotic Raid
		// needs no special case: it is excluded by the reason it should be.
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

	/// <summary>
	/// ⚠ Does nothing at all unless marks are actually showing -- one bool test per frame outside
	/// tracked content. The resolve inside is 1 Hz, and only runs where the feature is live.
	///
	/// ⭐ Re-evaluated each call so a checkbox flipped in the tab takes effect without zoning.
	/// </summary>
	public void Tick() {
		this.Evaluate();
		if (!this.Active)
			return;

		if (DateTime.UtcNow - this.lastResolve < ResolveInterval)
			return;
		this.lastResolve = DateTime.UtcNow;
		this.Resolve();
	}

	/// <summary>
	/// Name + world -> object id, once a second while active.
	///
	/// ⚠⚠ NOT PER FRAME. ~600 object-table entries with a string compare each, every frame, is
	/// exactly the shape the per-frame audit in DeserokUtils.md exists to catch. The draw path only
	/// looks objects up by id.
	/// </summary>
	private void Resolve() {
		this.resolved.Clear();
		foreach (var obj in Plugin.Objects) {
			if (obj is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc)
				continue;
			// ⚠ Home world as well as name. Frontlines is cross-world, so duplicate names are possible
			// -- rare, free to exclude, and a wrong match would mark a stranger.
			foreach (var entry in this.snapshot) {
				if (entry.World == pc.HomeWorld.RowId
					&& string.Equals(entry.Name, pc.Name.TextValue, StringComparison.Ordinal)) {
					this.resolved.Add((pc.GameObjectId, entry.Leader, Tag(entry.Name, pc.EntityId)));
					break;
				}
			}
		}
	}

	/// <summary>
	/// A Helldivers-style identifier -- first initial plus party slot, so "P2" rather than
	/// "Peachi Bunni".
	///
	/// ⭐ deserok's call after seeing a full name floating over her head: *"we do not want or need a
	/// name over their head."* He is right, and it is not only clutter -- a name is variable width, so
	/// a row of marked players jitters as they move, while two glyphs never do.
	///
	/// ⚠ The slot is read LIVE rather than captured, so it matches the party list you are looking at.
	/// The instance rebuilds your party, so the number at the queue pop is not the number you see
	/// inside. Costs eight comparisons per marked player, once a second, on a pass that is already
	/// walking the object table.
	///
	/// ⚠ Falls back to the initial alone when they are not in your current party -- which happens
	/// whenever the content splits a premade, and is precisely when the marker matters most. Better a
	/// bare "P" than a confidently wrong number.
	/// </summary>
	private static string Tag(string name, uint entityId) {
		string initial = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
		for (int i = 0; i < Plugin.Party.Length; i++) {
			if (Plugin.Party[i]?.EntityId == entityId)
				return $"{initial}{i + 1}";
		}
		return initial;
	}

	public IEnumerable<(Dalamud.Game.ClientState.Objects.Types.IGameObject Object, bool Leader, string Tag)> Marked() {
		foreach (var (id, leader, tag) in this.resolved) {
			var obj = Plugin.Objects.SearchById(id);
			if (obj is not null && obj.IsValid())
				yield return (obj, leader, tag);
		}
	}

	/// <summary>
	/// ⭐ Classified from `TerritoryType.TerritoryIntendedUse` -- a RULE rather than a zone list, so a
	/// new field operation sorts itself provided it reuses an existing value. Memoised, though this is
	/// now only called on a territory change anyway.
	/// </summary>
	private ContentGroup GroupFor(uint territory) {
		if (this.groupCache.TryGetValue(territory, out var cached))
			return cached;

		uint use = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
			?.GetRowOrDefault(territory)?.TerritoryIntendedUse.RowId ?? uint.MaxValue;

		ContentGroup group = use switch {
			18 or 28 or 37 or 39 => ContentGroup.Pvp,                    // Frontline, CC, CC custom, Rival Wings
			26 or 41 or 47 or 48 or 52 or 53 or 60 or 61 => ContentGroup.FieldOps,
			8 => ContentGroup.AllianceRaid,
			_ => ContentGroup.None,
		};

		Plugin.Log.Information($"EphemeralMarks: territory {territory} intendedUse={use} -> {group}");
		return this.groupCache[territory] = group;
	}

	public void Dispose() {
		Plugin.AddonLifecycle.UnregisterListener(this.OnPop);
		Plugin.ClientState.TerritoryChanged -= this.OnTerritoryChanged;
	}
}
