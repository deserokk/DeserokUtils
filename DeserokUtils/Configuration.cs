using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Configuration;

using Newtonsoft.Json;

namespace DeserokUtils;

/// <summary>
/// Persisted settings. Dalamud stores this as JSON next to the plugin.
///
/// ⚠ Observed spawn times live here on purpose: a prediction is only as good as its anchor, and an
/// anchor that dies on logout means the first half hour of every session is blind.
/// </summary>
[Serializable]
public sealed class Configuration: IPluginConfiguration {
	/// <summary>
	/// 1 = initial. 2 = real Occult Crescent pot FATE names, replacing wiki guesses that turned out
	/// to be different FATEs entirely.
	/// ⚠ A default only applies to a config that does not exist yet. Once one is on disk, changing
	/// a default here is INERT -- the migration below is what actually reaches an existing install.
	/// </summary>
	public int Version { get; set; } = 1;

	public const int CurrentVersion = 4;

	/// <summary>
	/// ⚠⚠ EVERY LIST IN THIS FILE MUST CARRY THIS, AND HERE IS WHY.
	///
	/// Newtonsoft defaults to ObjectCreationHandling.Auto: when a property's getter already returns
	/// a non-null collection -- which every `= new() {...}` initialiser below guarantees -- it does
	/// not replace that collection, it ADDS the JSON items to it. So each load produced the defaults
	/// PLUS everything on disk, and each save wrote the result back. The lists grew by their own
	/// length on every single plugin load, silently, forever.
	///
	/// Measured 2026-08-07: "Kugane" was in FcBuffSafePlaces 21 times, and AlertMinutes read
	/// [10, 5, 10, 5, 10, 5, ...] on a shipped install that had never been edited.
	///
	/// ⭐⭐ THIS IS WHERE THE FATEWATCH DUPLICATES CAME FROM. TrackedFates was the one list that
	/// looked healthy, and only because Migrate() dedupes it on every load -- a fix written for the
	/// symptom while the cause kept running underneath it, in every other list. The 117.7-minute
	/// prediction that "did not quite land" as 30 x 4 = 120 was this: not four tracked FATEs, but a
	/// measured value doubled by a list that had silently doubled.
	///
	/// ⚠ Replace makes the deserialiser overwrite the initialiser instead of appending to it. The
	/// defaults still apply to a config that does not exist yet, which is the only thing they were
	/// ever for.
	/// </summary>
	private const ObjectCreationHandling ReplaceList = ObjectCreationHandling.Replace;

	/// <summary>
	/// Diagnostic output to the Debug chat channel.
	///
	/// ⚠ OFF by default. It shipped on because everything was being diagnosed, and CastWatch logs a
	/// line per UseAction while armed -- which against a thirteen-line fallback macro is thirteen
	/// lines per press. Correct while hunting a bug, noise once the bug is dead.
	/// </summary>
	public bool Verbose { get; set; } = false;

	// ── FateWatch ────────────────────────────────────────────────────────────────────────────

	public bool FateWatchEnabled { get; set; } = true;

	/// <summary>FATE names to track, matched case-insensitively against the live table.</summary>
	/// <summary>
	/// ⭐ Confirmed from the live FATE table 2026-08-03, not from a wiki. The two rotate: Daylight
	/// Pottery (north) at 21:31:35, In a Pot of Bother (south) at 22:01:39 -- thirty minutes and
	/// four seconds apart, in territory 1346.
	/// </summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<string> TrackedFates { get; set; } = new() {
		"Daylight Pottery",
		"In a Pot of Bother",
	};

	/// <summary>
	/// ⚠⚠ These FATEs ALTERNATE. The 30-minute figure is the gap between *consecutive members*, so
	/// each individual one recurs every 60 minutes. Treating them as two independent 30-minute
	/// timers predicts each at exactly the moment the OTHER is due -- confidently, and wrong every
	/// single time.
	///
	/// So the rotation is the unit, not the FATE. TrackedFates is the ring, in order.
	/// </summary>
	public bool RotationMode { get; set; } = true;

	/// <summary>
	/// Minutes between consecutive SLOTS -- i.e. between one pot and the next pot, whichever FATE
	/// that turns out to be. About 30.
	///
	/// ⚠⚠ NOT the gap between two spawns of the same FATE, which is this times the number of members
	/// in the ring -- about 60. Those two numbers are both "the cycle" in English and mixing them up
	/// doubles or halves every prediction. This comment used to say the wrong one of the two, and
	/// that is precisely how MeasuredIntervals came to be read in the wrong unit.
	///
	/// ⚠ A DEFAULT, not a fact. Nothing published confirms it and the plugin measures the real
	/// interval as spawns accumulate -- see MeasuredIntervals. Change it here if the measurement
	/// disagrees; do not assume this number is right because it is written down.
	/// </summary>
	public double CycleMinutes { get; set; } = 30.0;

	/// <summary>Minutes before a predicted spawn to warn at. Descending.</summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<double> AlertMinutes { get; set; } = new() { 10, 5 };

	/// <summary>Last observed spawn per FATE name, as unix seconds.</summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public Dictionary<string, long> LastSeen { get; set; } = new();

	/// <summary>
	/// Gaps between consecutive observed spawns OF THE SAME FATE, in minutes. Kept rather than
	/// averaged away so an outlier is visible as an outlier instead of quietly dragging the mean.
	///
	/// ⚠⚠ SAME-FATE gaps, so ~60 -- a different unit from <see cref="CycleMinutes"/>, which is ~30.
	/// It has to be: RecordSpawn only ever sees one name twice, so a slot gap is not observable
	/// here. Divide by the rotation length before using it as a slot -- FateTracker.EffectiveCycle
	/// is the single place that conversion lives.
	/// </summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public Dictionary<string, List<double>> MeasuredIntervals { get; set; } = new();

	/// <summary>
	/// Short label per FATE for the server bar -- "N" / "S".
	///
	/// ⚠ Separate dictionary rather than a field on a richer TrackedFates type, purely so an
	/// existing saved config keeps deserialising. Changing the shape of a list that is already on
	/// disk loses whatever was in it, and the spawn anchors are the expensive thing to lose.
	/// </summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public Dictionary<string, string> FateLabels { get; set; } = new() {
		["Daylight Pottery"] = "N",
		["In a Pot of Bother"] = "S",
	};

	/// <summary>Territory each FATE was last seen in, so the bar can hide itself elsewhere.</summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public Dictionary<string, uint> LastSeenTerritory { get; set; } = new();

	/// <summary>
	/// Zone instance each FATE was last seen in, paired with <see cref="LastSeenTerritory"/> to say
	/// exactly WHERE an anchor was made -- which is what makes it possible to notice it no longer
	/// applies.
	///
	/// ⚠ A second dictionary rather than widening LastSeenTerritory's value type, for the same reason
	/// FateLabels is its own dictionary: changing the shape of something already on disk loses what
	/// is in it, and the spawn anchors are the expensive thing to lose.
	///
	/// ⚠ An anchor written before this field existed has no entry here. Missing is treated as
	/// "matches", never as stale -- otherwise updating the plugin while standing in the zone would
	/// throw away a perfectly good anchor on the first tick. No migration needed for the same reason.
	/// </summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public Dictionary<string, uint> LastSeenInstance { get; set; } = new();

	public bool DtrEnabled { get; set; } = true;

	// ── FcBuffs ──────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// ⚠ OFF, and it costs nothing to leave off. A NEW key absent from an existing config keeps
	/// whatever this initialiser sets -- which is the opposite of the trap documented above, where
	/// changing the default on an EXISTING key reaches nobody. Additions are free; edits need
	/// <see cref="Migrate"/>. Both halves of that rule are worth knowing, because only one of them
	/// has ever caused a bug here.
	///
	/// ⚠⚠ And this one is automation: it acts without being asked, which nothing else in this plugin
	/// does. Defaulting it on would switch that behaviour on for an existing install silently.
	/// </summary>
	public bool FcBuffsEnabled { get; set; } = false;

	/// <summary>
	/// Go through every step EXCEPT the button press, and log what would have been pressed.
	///
	/// ⭐ ON by default, and it should stay on until a dry-run log has been read. This is the first
	/// thing in the plugin that acts rather than observes, and the row index it acts on comes from
	/// three measured constants. A dry run costs one line in a log; the alternative costs a wrongly
	/// consumed action and no clear evidence of which step was wrong.
	/// </summary>
	public bool FcBuffsDryRun { get; set; } = true;

	/// <summary>
	/// Company action names to keep running, matched against the CompanyAction sheet.
	///
	/// ⚠ Empty on purpose. The two that get used are Heat of Battle and Reduced Rates, but their
	/// exact row names carry roman numerals and this is not the place to guess at one -- the tab
	/// lists the real names out of the game and they get ticked there.
	/// </summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<string> FcBuffActions { get; set; } = new();

	/// <summary>
	/// Where acting is allowed: cities and residential districts, as PLACE NAMES.
	///
	/// ⭐ Names rather than territory ids. Cities are finite and known, so the list can simply be
	/// written down -- but a column of numbers is unverifiable by eye and silently wrong forever,
	/// whereas a name that does not resolve says so in the tab. The sheet is the authority for the
	/// number; the name is the part a human can check.
	///
	/// ⚠ Seeded from memory, so treat a name that fails to resolve as a typo here rather than as a
	/// missing zone -- and note this list goes stale on the next expansion, which is precisely when
	/// it will be needed. /fcbuffs here adds wherever you are standing without editing anything.
	/// </summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<string> FcBuffSafePlaces { get; set; } = new() {
		"Limsa Lominsa Upper Decks",
		"Limsa Lominsa Lower Decks",
		"New Gridania",
		"Old Gridania",
		"Ul'dah - Steps of Nald",
		"Ul'dah - Steps of Thal",
		"Foundation",
		"The Pillars",
		"Idyllshire",
		"Rhalgr's Reach",
		"Kugane",
		"The Crystarium",
		"Eulmore",
		"Old Sharlayan",
		"Radz-at-Han",
		"Tuliyollal",
		"Solution Nine",
		"Mist",
		"The Lavender Beds",
		"The Goblet",
		"Shirogane",
		"Empyreum",
	};

	// ⚠⚠ FcBuffCreditFloor lived here and was DELETED 2026-08-07. It guarded a cost that does not
	// exist: credits are spent BUYING a company action, which lands it in the FC's "inactive
	// actions" stock. ACTIVATING one is free -- it consumes an item already paid for. This feature
	// only ever activates, so there was never a credit to guard.
	//
	// ⭐ The real finite resource is the STOCK, not the balance -- 14 of a possible 15 held, and
	// every activation spends one. See FcBuffLowStockWarning.

	/// <summary>
	/// Warn when the stock of a wanted buff drops to this or below.
	///
	/// ⚠ This is the constraint the credit floor was mistakenly standing in for. Automatic
	/// activation quietly consumes stock, and the failure mode when it runs out is the plugin going
	/// silently back to doing nothing -- which is indistinguishable from it working, right up until
	/// the eight-hour grind it was built to prevent.
	/// </summary>
	public int FcBuffLowStockWarning { get; set; } = 2;

	/// <summary>
	/// Seconds between checks of whether the buffs are still up.
	///
	/// ⭐ 60 by default, and it could honestly be 300 -- these buffs last 24 hours, so any interval
	/// short of "hours" is over-sampling. The only thing granularity buys is how quickly the "it
	/// just ran out" toast reaches you while you are in the field, and a minute is already far
	/// inside the timescale of the problem this exists to solve.
	///
	/// ⚠ The check itself is now id lookups against the status list, so the interval is a comfort
	/// setting rather than a performance one. Slowing a cheap thing down is not where the cost was.
	/// </summary>
	public int FcBuffCheckSeconds { get; set; } = 60;

	public bool AlertToast { get; set; } = true;
	public bool AlertChat { get; set; } = true;
	public bool AlertSound { get; set; } = true;

	/// <summary>
	/// ⚠⚠ Reaches an EXISTING config, which a changed default cannot. The v1 defaults were two FATE
	/// names taken from a wiki that turned out to be different FATEs; they never spawned, so they
	/// can be removed safely -- but only if they were never actually seen, in case the wiki was
	/// right about somewhere this was not tested.
	/// </summary>
	public void Migrate() {
		// ⚠ Dedupe ALWAYS, not only on a version bump. A duplicated entry is not cosmetic here: the
		// rotation length is derived from how many members there are, so one accidental repeat
		// doubled every prediction (observed: 117.7 min where 60 was correct). Whatever produced
		// the duplicate, the timing must not depend on nobody ever making one.
		int before = this.TrackedFates.Count;
		this.TrackedFates = this.TrackedFates
			.Where(t => !string.IsNullOrWhiteSpace(t))
			.Select(t => t.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (this.TrackedFates.Count != before) {
			Plugin.Log.Warning($"FateWatch: removed {before - this.TrackedFates.Count} duplicate tracked FATE(s)");
			this.Save();
		}

		if (this.Version < 4) {
			// ⚠⚠ Repairs what ObjectCreationHandling.Auto did before the attributes above stopped it.
			// The Replace attribute only prevents FUTURE growth -- it cannot shrink what is already on
			// disk, and what is on disk is 21 copies of every city and a dozen copies of every alert
			// threshold. A cause fixed without cleaning up its damage still leaves the damage.
			int places = this.FcBuffSafePlaces.Count;
			this.FcBuffSafePlaces = this.FcBuffSafePlaces
				.Where(p => !string.IsNullOrWhiteSpace(p))
				.Select(p => p.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			int alerts = this.AlertMinutes.Count;
			// ⚠ Descending, per the field's own contract -- deduping must not quietly reorder it.
			this.AlertMinutes = this.AlertMinutes.Distinct().OrderByDescending(m => m).ToList();

			this.FcBuffActions = this.FcBuffActions
				.Where(a => !string.IsNullOrWhiteSpace(a))
				.Select(a => a.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			if (places != this.FcBuffSafePlaces.Count || alerts != this.AlertMinutes.Count)
				Plugin.Log.Warning(
					$"config repair: safe places {places} -> {this.FcBuffSafePlaces.Count}, "
					+ $"alert thresholds {alerts} -> {this.AlertMinutes.Count} "
					+ "(Newtonsoft was appending defaults to the saved list on every load)");
		}

		if (this.Version >= CurrentVersion)
			return;

		string[] wikiGuesses = { "Persistent Pots", "Pleading Pots" };
		foreach (string stale in wikiGuesses) {
			if (this.LastSeen.ContainsKey(stale))
				continue;   // it really spawned somewhere; leave it alone
			this.TrackedFates.RemoveAll(t => string.Equals(t, stale, StringComparison.OrdinalIgnoreCase));
			this.FateLabels.Remove(stale);
		}

		foreach (var (name, label) in new[] { ("Daylight Pottery", "N"), ("In a Pot of Bother", "S") }) {
			if (!this.TrackedFates.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
				this.TrackedFates.Add(name);
			if (!this.FateLabels.ContainsKey(name))
				this.FateLabels[name] = label;
		}

		if (this.Version < 3) {
			// ⚠ A changed default cannot reach a config that already exists -- the same trap as the
			// FATE names. Diagnostics were on for everyone who installed before this, and would have
			// stayed on forever with the default quietly saying otherwise.
			this.Verbose = false;
		}

		this.Version = CurrentVersion;
		this.Save();
		Plugin.Log.Information($"config migrated to v{CurrentVersion}: pot FATE names corrected");
	}

	public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
