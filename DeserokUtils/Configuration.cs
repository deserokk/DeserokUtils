using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Configuration;

using Newtonsoft.Json;

namespace DeserokUtils;

/// <summary>
/// One ring of FATEs that take turns, in one territory.
///
/// ⚠⚠ THE TERRITORY IS PART OF THE ROTATION, not a filter applied to it. The Occult Crescent has two
/// zones with two pot FATEs each, and they are different FATEs on independent rings -- South Horn
/// runs Persistent Pots / Pleading Pots, North Horn runs Daylight Pottery / In a Pot of Bother.
/// A single flat list cannot express that: it would make the ring four long and halve every
/// prediction in both zones.
///
/// ⭐ This is the split DeserokUtils.md predicted and asked for -- "what I track" and "what is in
/// the rotation" were two ideas sharing one list, and the note said to separate them the moment
/// there were two rotations. There are now two.
/// </summary>
[Serializable]
public sealed class FateRotation {
	/// <summary>Human name for the zone, for diagnostics only. Nothing keys off it.</summary>
	public string Zone { get; set; } = string.Empty;

	public uint Territory { get; set; }

	/// <summary>Ring members IN ORDER. The order is which one comes next, so it is load-bearing.</summary>
	public List<string> Members { get; set; } = new();

	/// <summary>
	/// Short label per member for the server bar.
	///
	/// ⚠⚠ NAME COLLISION, and it is a nasty one now: North Horn's labels are "N" and "S" for the
	/// NORTH and SOUTH ends OF THAT ZONE -- they have nothing to do with the zones being called
	/// North Horn and South Horn. A bar reading "36m S" in North Horn means "south side of this
	/// map", not "South Horn". Rename them the moment that reads wrong to anybody.
	/// </summary>
	public Dictionary<string, string> Labels { get; set; } = new();

	/// <summary>
	/// Minutes between consecutive SLOTS -- one pot to the next pot, whichever it turns out to be.
	///
	/// ⚠⚠ NOT the gap between two spawns of the same FATE, which is this times <see cref="Members"/>
	/// count. Both are "the cycle" in English and mixing them doubles or halves every prediction.
	/// </summary>
	public double SlotMinutes { get; set; } = 30.0;
}

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

	public const int CurrentVersion = 5;

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

	/// <summary>
	/// Every FATE ring the plugin knows about, one per territory.
	///
	/// ⚠⚠ These FATEs ALTERNATE within their zone. The 30-minute figure is the gap between
	/// *consecutive members*, so each individual one recurs every 60 minutes. Treating them as two
	/// independent 30-minute timers predicts each at exactly the moment the OTHER is due --
	/// confidently, and wrong every single time.
	///
	/// ⭐ North Horn confirmed from the live FATE table 2026-08-03: Daylight Pottery (north end) at
	/// 21:31:35, In a Pot of Bother (south end) at 22:01:39 -- thirty minutes four seconds apart.
	///
	/// ⚠⚠ South Horn was in this plugin ONCE AND WAS DELETED. v2's migration removed "Persistent
	/// Pots" and "Pleading Pots" as wiki guesses that were "different FATEs entirely". They were
	/// real -- ids 1976 and 1977, confirmed from the Fate sheet 2026-08-12 -- and the wiki was
	/// describing the OTHER ZONE. The migration's guard even asked the right question ("has this
	/// ever spawned?") and got the locally-true, globally-wrong answer, because at that point only
	/// North Horn had ever been visited. Absence of evidence from one territory.
	///
	/// ⚠ South Horn's labels are empty because nobody has recorded where its two pots spawn yet.
	/// Empty labels render fine; fill them in once observed.
	/// </summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<FateRotation> Rotations { get; set; } = new() {
		new FateRotation {
			Zone = "The Occult Crescent: South Horn",
			Territory = 1252,
			Members = new List<string> { "Persistent Pots", "Pleading Pots" },
			SlotMinutes = 30.0,
		},
		new FateRotation {
			Zone = "The Occult Crescent: North Horn",
			Territory = 1346,
			Members = new List<string> { "Daylight Pottery", "In a Pot of Bother" },
			Labels = new Dictionary<string, string> {
				["Daylight Pottery"] = "N",
				["In a Pot of Bother"] = "S",
			},
			SlotMinutes = 30.0,
		},
	};

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
	/// ⚠⚠ SAME-FATE gaps, so ~60 -- a different unit from a rotation's SlotMinutes, which is ~30.
	/// It has to be: RecordSpawn only ever sees one name twice, so a slot gap is not observable
	/// here. Divide by the rotation length before using it as a slot -- FateTracker.EffectiveCycle
	/// is the single place that conversion lives.
	///
	/// ⭐ Keyed by FATE NAME, not by rotation, and that is why the rotation refactor could throw the
	/// old lists away without losing anything: a measured interval is evidence about a FATE, and
	/// stays true no matter how the rings are arranged around it.
	/// </summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public Dictionary<string, List<double>> MeasuredIntervals { get; set; } = new();

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
	/// Go through every step EXCEPT the activation, and log what would have been pressed.
	///
	/// ⚠ OFF as of v1.5.1. It shipped ON in 1.5.0 as a first-release guard, and the guard did its
	/// job -- the dry-run log is what confirmed the row index before anything was consumed. Once the
	/// chain was verified end to end, leaving it on meant a fresh install would tick its buffs,
	/// enable the feature, and silently do nothing until it found this checkbox.
	///
	/// ⚠⚠ NO MIGRATION, on purpose, and this is the rule rather than an oversight: a default is for a
	/// config that does not exist yet. Forcing it off in Migrate() would override anyone who had
	/// deliberately turned it ON -- which is exactly the "rehearse before you trust it" case this
	/// setting exists to serve.
	/// </summary>
	public bool FcBuffsDryRun { get; set; } = false;

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

	// ── DrawSheathe ──────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// ⚠ The literal command line, not an emote name. The feature sends it verbatim through the
	/// chatbox, so whatever works when typed works here.
	///
	/// ⚠⚠ "motion" is a SUBCOMMAND, and the game's own usage text writes it as `/draw [subcommand]`
	/// -- the square brackets mean "optional" and are not typed. `/draw [motion]` is a different
	/// string from `/draw motion`, and the difference is whether the chat line goes out with it.
	/// </summary>
	public const string DefaultDrawCommand = "/draw motion";

	/// <inheritdoc cref="DefaultDrawCommand"/>
	public const string DefaultSheatheCommand = "/sheathe motion";

	/// <summary>
	/// Sent when the weapon is AWAY. See <see cref="DefaultDrawCommand"/>.
	///
	/// ⭐ Configurable for one reason only: it costs a string and it outlives this specific Gold
	/// Saucer purchase. The Emote sheet currently holds exactly two draw/sheathe rows (237 and 238),
	/// so there is nothing else to point it at today -- but "which emote" is not a thing the toggle
	/// logic should have an opinion about.
	///
	/// ⚠ A NEW key, so no migration: absent from an existing config, it keeps this initialiser.
	/// Additions are free; only edits to an existing key need <see cref="Migrate"/>.
	/// </summary>
	public string DrawCommand { get; set; } = DefaultDrawCommand;

	/// <summary>Sent when the weapon is OUT. See <see cref="DrawCommand"/>.</summary>
	public string SheatheCommand { get; set; } = DefaultSheatheCommand;

	/// <summary>
	/// Presses closer together than this many milliseconds are treated as ONE press. 0 disables.
	///
	/// ⚠⚠ AN ACCESSIBILITY SETTING, NOT A TUNING DETAIL. deserok uses a key repeater as an assistive
	/// device because tapping keys is difficult. It works like ordinary auto-repeat -- a tap is one
	/// press, holding past about half a second streams presses at ~10 Hz (102 ms apart, measured
	/// 2026-08-17) until release. A normal tap is therefore unaffected by this setting; the hold is
	/// what produced a dozen weapon toggles a second.
	///
	/// ⚠⚠ And a cooldown alone does NOT solve the hold, which is the trap: gating on the game's
	/// one-second sheathe cooldown turns a dozen toggles into one per second and keeps going for as
	/// long as the key is down. Slower cycling still reads as broken. Only a quiet-gap test collapses
	/// a held key to a single action.
	///
	/// ⭐ 250 is better than 2x the measured 102 ms repeat, and comfortably below the repeater's own
	/// ~500 ms hold-to-repeat delay, so it cannot swallow a deliberate second tap. It lives here
	/// rather than in a constant because repeat rates are a property of somebody's hardware, and a
	/// person whose device repeats slower should be able to say so without a rebuild.
	/// </summary>
	public int DrawSheatheRepeatCollapseMs { get; set; } = 250;

	/// <summary>
	/// When the emote would be refused, use the game's own draw/sheathe instead.
	///
	/// ⚠⚠ ON, because the emote SIMPLY DOES NOTHING in those cases -- refused with no message, so
	/// without this the key is dead exactly when you are running or jumping, which is the failure
	/// the old macro had. Off is the pre-existing emote-only behaviour.
	///
	/// ⚠ Named for the CONDITION, not for movement, and that rename is not cosmetic. It shipped for
	/// an evening as UseDefaultToggleWhileMoving, then jumping turned out to refuse the emote too
	/// while reading IsPlayerMoving == false. A setting named after one case invites the next one to
	/// be bolted on as a second setting; named after the reason, it just grows a new entry in
	/// DrawSheatheFeature.EmoteRefusedBecause.
	///
	/// ⭐ Kept as a switch rather than hardcoded for one specific reason: this branch is the only
	/// part of the feature that calls into the client directly (UIState.WeaponState.SetUnsheathed)
	/// rather than sending a text command, so it is the only part a patch can break outright.
	/// Turning it off restores a working key without waiting on a build -- which is the whole reason
	/// this plugin exists instead of a dependency on somebody else's.
	/// </summary>
	public bool UseDefaultToggleWhenEmoteWouldFail { get; set; } = true;

	// ── EmoteQuiet ───────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Suppress the chat log message on a repeat of an emote you have already announced.
	///
	/// ⚠⚠ OFF by default even though it was asked for, because it changes what OTHER PEOPLE see.
	/// Every other setting in this file affects only deserok's own client; this one edits what
	/// reaches strangers' chat logs, and a plugin that starts doing that on install without being
	/// asked is the wrong shape. It costs one checkbox, once.
	///
	/// ⚠⚠ It does nothing unless the game's own "Display log message" is TICKED in the emote window.
	/// That checkbox is the thing this exists to let you finally leave on -- with it off there is no
	/// message to suppress, and the feature would appear broken while working perfectly.
	/// </summary>
	public bool EmoteQuietEnabled { get; set; } = false;

	/// <summary>
	/// How long after announcing an emote to stay quiet about that same emote, in seconds.
	///
	/// ⚠ PER EMOTE, not global -- clapping must not silence your next /dote. 60 is the figure from
	/// the original ask: "display the first of a given emote, then hide log messages for the same
	/// emote within 1 minute".
	/// </summary>
	public int EmoteQuietWindowSeconds { get; set; } = 60;

	/// <summary>
	/// Hide OTHER people's repeated emote lines in your own log, same window, keyed per sender.
	///
	/// ⚠⚠ OFF by default, and for a different reason than the outgoing half. That one changes what
	/// others see; this one deletes things from your own chat log, and a filter that is too broad
	/// hides its own mistakes by construction. Opt in, then audit it -- the tab counts what it hid.
	///
	/// ⭐ Per sender is not a default to revisit, it is the design: "knowing 10 people clapped is
	/// nice, knowing 1 guy spammed clap 30 times is noise". Audience size is signal, repetition is
	/// noise, and keying on sender-plus-line is exactly the line between them.
	/// </summary>
	public bool EmoteQuietIncomingEnabled { get; set; } = false;

	/// <summary>
	/// Name prefixes whose emotes all count as ONE emote for the quiet window.
	///
	/// ⚠⚠ THE CHEER VARIANTS ARE SIXTEEN SEPARATE EMOTES THAT NOBODY TREATS AS SEPARATE. Observed
	/// behaviour, deserok and others alike: you cycle through them hunting the colour you want and
	/// then settle on one. Keyed per emote id, that cycle announces every step.
	///
	/// ⭐ Confirmed from the sheet 2026-08-17: fifteen rows begin "Cheer " -- On/Wave/Jump/Rhythm/
	/// Light across five colours -- and every one of them renders the IDENTICAL log line, "…cheers to
	/// the rhythm." (their LogMessage rows are byte-for-byte the same). So collapsing them loses
	/// nothing that was ever visible. The trailing space matters: plain "Cheer" is a different emote
	/// with a different line ("…lets out a cheer.") and stays on its own.
	///
	/// ⚠ A hand-written list, deliberately, and NOT derived by grouping emotes that share a log
	/// message. That derivation was tried and is a trap: ExtractText drops the verb -- it is stored as
	/// a singular/plural pair inside the payload -- so Bow, Clap, Wave, Yes, No, Smile and fifteen
	/// others all reduce to ".". Grouping on it would silence your next bow because you clapped, and
	/// do it invisibly. Fifteen names verified by eye beats a clever rule nobody can check.
	///
	/// ⚠ Prefix match on the emote NAME, case-insensitive. Anything not matching is its own family.
	/// </summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<string> EmoteQuietFamilies { get; set; } = new() { "Cheer " };

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
		// ⚠ Dedupe ALWAYS, not only on a version bump. A duplicated member is not cosmetic here: the
		// rotation length is how many members there are, so one accidental repeat doubled every
		// prediction (observed: 117.7 min where 60 was correct). Whatever produced the duplicate,
		// the timing must not depend on nobody ever making one.
		//
		// ⭐ The cause is now fixed -- Newtonsoft was appending saved lists onto their initialisers --
		// but this stays. It cost a day to find once, and the check is three lines.
		foreach (var rot in this.Rotations) {
			int before = rot.Members.Count;
			rot.Members = rot.Members
				.Where(m => !string.IsNullOrWhiteSpace(m))
				.Select(m => m.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (rot.Members.Count != before)
				Plugin.Log.Warning(
					$"FateWatch: removed {before - rot.Members.Count} duplicate member(s) from the {rot.Zone} rotation");
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

		// ⚠⚠ THE v2 "wiki guess" PURGE LIVED HERE AND IS GONE. It removed "Persistent Pots" and
		// "Pleading Pots" on the grounds that they were different FATEs entirely. They are ids 1976
		// and 1977, they are real, and they are South Horn's entire rotation -- the wiki was right
		// and was describing the zone this plugin had not been to.
		//
		// ⭐ Its guard was not careless. It asked "has this ever spawned?" and skipped anything that
		// had. But the answer came from a config that had only ever seen North Horn, so absence of
		// evidence in one territory read as evidence of absence everywhere. A guard can only be as
		// good as the range of the data it consults.
		//
		// ⚠ Nothing replaces it. TrackedFates/FateLabels/CycleMinutes/RotationMode no longer exist,
		// and an unknown key deserialises to nothing and vanishes on the next Save() -- no migration
		// for a deletion. The Rotations default reproduces the old North Horn ring exactly, and
		// LastSeen/MeasuredIntervals are keyed by FATE name, so every measurement carries over.

		if (this.Version < 3) {
			// ⚠ A changed default cannot reach a config that already exists -- the same trap as the
			// FATE names. Diagnostics were on for everyone who installed before this, and would have
			// stayed on forever with the default quietly saying otherwise.
			this.Verbose = false;
		}

		this.Version = CurrentVersion;
		this.Save();
		Plugin.Log.Information($"config migrated to v{CurrentVersion}: FATE rotations are now per-territory");
	}

	public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
