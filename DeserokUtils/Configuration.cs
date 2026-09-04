using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Configuration;

using Newtonsoft.Json;

namespace DeserokUtils;

/// <summary>
/// One status worth marking over somebody's head.
/// </summary>
public sealed class DebuffMark {
	public bool Enabled { get; set; } = true;

	/// <summary>The name as typed, kept so the box can show it back and the id can be re-resolved.</summary>
	public string Status { get; set; } = string.Empty;

	// ⚠⚠ A SINGLE StatusId USED TO LIVE HERE AND IT WAS WRONG. Status names are not unique -- there
	// are three separate statuses called "Reprisal" (753, 1193, 2101) -- so resolving a name to one id
	// silently watches the wrong one and the feature just never fires. Nothing is persisted now except
	// the name; ids are resolved at runtime, as a SET. Same lesson as ActionLookup's first-wins.

	/// <summary>
	/// ⚠⚠ ON by default, and the default matters. Kuzushi, the status this feature was built for, reads
	/// *"damage taken from the samurai who applied this effect"* -- so somebody else's copy of the same
	/// status is the WRONG answer. Defaulting this off would produce a marker that is confidently wrong
	/// exactly when a limit break is spent on it.
	/// </summary>
	public bool MineOnly { get; set; } = true;

	public Features.EphemeralMarks.MarkShape Shape { get; set; } = Features.EphemeralMarks.MarkShape.Icon;

	public int Glyph { get; set; } = (int)Dalamud.Interface.FontAwesomeIcon.Skull;

	public System.Numerics.Vector4 Colour { get; set; } = new(1f, 0.35f, 0.2f, 1f);
}

/// <summary>
/// A glyph chosen for one specific character. See <see cref="Configuration.MarksOverrides"/> for the
/// limits on what this is allowed to influence.
/// </summary>
public sealed class MarkOverride {
	/// <summary>⚠ <c>Name@World</c> as typed. Matched case-insensitively; never parsed into an id.</summary>
	public string Who { get; set; } = string.Empty;

	public Features.EphemeralMarks.MarkShape Shape { get; set; } = Features.EphemeralMarks.MarkShape.Icon;

	/// <summary>Font Awesome codepoint, used when <see cref="Shape"/> is Icon. Defaults to a heart,
	/// which is the reason this whole feature exists.</summary>
	public int Glyph { get; set; } = (int)Dalamud.Interface.FontAwesomeIcon.Heart;

	/// <summary>
	/// This person's own colour, or null to use the shared marker colour.
	///
	/// ⭐ NULLABLE RATHER THAN ALWAYS SET, deliberately. If every override carried a colour, it would
	/// be seeded from the shared one at the moment it was created and then silently stop tracking it
	/// -- so changing the marker colour later would move everybody except the people you had
	/// customised, which is the opposite of what "I only changed their shape" implies.
	/// </summary>
	public System.Numerics.Vector4? Colour { get; set; }
}

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
	/// ⚠ A default only applies to a config that does not exist yet. Once one is on disk, changing
	/// a default here is INERT -- the migration below is what actually reaches an existing install.
	/// </summary>
	public int Version { get; set; } = 1;

	public const int CurrentVersion = 6;

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

	/// <summary>
	/// Note glamour-dresser ownership on the game's item tooltips.
	///
	/// ⚠⚠⚠ OFF, AND IT STAYS OFF UNTIL THE WRITE IS PROVEN. The first version of this hard-crashed
	/// the game — not a managed exception Dalamud could catch, an access violation inside
	/// StringArrayData.SetValue — on hovering a market board search result, 2026-09-04. Cause: the
	/// addon's OnRequestedUpdate is handed StringArrayData**, the whole table of string arrays, and
	/// it was cast as a single StringArrayData*. One level of indirection, and every field offset
	/// after it was measured from nonsense.
	///
	/// ⚠ A crash is the one failure this project must not ship. Everything else here fails by doing
	/// nothing; this took the client with it.
	/// </summary>
	public bool DresserTooltip { get; set; } = false;

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
	/// ⚠⚠ THIS SETTING SHOULD NOT EXIST, and the sentence that used to sit here is why it does.
	/// It read "the Emote sheet currently holds exactly two draw/sheathe rows (237 and 238)", which
	/// is true and reads as "two to choose from". It is not. It is two rows TOTAL, one per action:
	///
	///     Emote[238] "Draw Weapon"     /draw
	///     Emote[237] "Sheathe Weapon"  /sheathe
	///
	/// One option per action is no option at all. deserok, on finding the free-text boxes in the
	/// tab: *"there needs to be no option at all for draw and sheathe, there is only /draw and
	/// /sheathe. If they don't want to use those then they'd simply toggle off that util."*
	///
	/// ⭐ DELETE BOTH FIELDS IN THE DESIGN PASS. Not "turn them into a picker" -- a picker with one
	/// entry is the same mistake with a nicer widget. A later "play an emote AFTER drawing" idea
	/// would need its own field for a genuinely open choice; it is not a reason to keep these.
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
	/// Whether a completed player-to-player meld reopens the meld window on the tab it was using.
	/// </summary>
	public bool MeldWindowKeepOpen { get; set; } = true;

	/// <summary>
	/// Whether an INCOMING materia meld request from another player is accepted automatically.
	///
	/// ⚠ Defaults OFF. Answering a prompt on someone's behalf is not a thing to switch on for them
	/// without asking, and this plugin's releases land on other people's machines.
	/// </summary>
	public bool MeldAutoAccept { get; set; } = false;

	/// <summary>
	/// Whether an incoming repair request is accepted automatically. Defaults OFF: it spends dark
	/// matter, so it is not a thing to switch on for someone unasked.
	/// </summary>
	public bool RepairAutoAccept { get; set; } = false;

	// ── Macro icons ───────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Whether <c>/micon</c> may name an item. ⚠ ON by default: the game's own /micon has no item
	/// category at all, so this can only ever fill in a macro that currently shows the blank M. It
	/// cannot change one that already has an icon.
	/// </summary>
	public bool MacroItemIcons { get; set; } = true;

	/// <summary>
	/// Whether a macro with no <c>/micon</c> and no hand-picked icon takes the icon of the action or
	/// item its NAME matches. deserok's idea, and it costs a macro line.
	///
	/// ⚠ ON by default, but it is the more opinionated of the two -- it applies to macros nobody
	/// wrote a /micon into, which is most of them. The guard is <c>IconId == 0</c>, so a picked icon
	/// wins; if that guard is ever wrong, this is the switch that turns the symptom off.
	/// </summary>
	public bool MacroNameIcons { get; set; } = true;

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

	// ── EphemeralMarks ───────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Mark the people you came into large content with. Client-side only; nobody else sees it.
	///
	/// ⚠ ON by default, unlike FcBuffs and EmoteQuiet. Those act on the world -- one spends stock,
	/// the other changes what strangers see. This draws a triangle on your own screen and is invisible
	/// to everyone else, so there is nothing to opt into.
	/// </summary>
	public bool MarksEnabled { get; set; } = true;

	/// <summary>
	/// If you came in with more than this many people, mark nobody.
	///
	/// ⭐⭐ THE DEGENERACY GUARD, and deserok's simplification of a fifteen-checkbox content list.
	/// When the premade IS the whole group -- Chaotic Raid, a static, a premade dungeon -- marking
	/// everyone is the same as marking nobody. The content this exists for caps premades anyway:
	/// Frontlines at 4, Crystalline Conflict at 2, and full-8 alliance premades are rare.
	///
	/// ⭐ So 5 clears every legitimate case and excludes content you enter as a whole group -- which
	/// means **Chaotic Raid needs no special case**. It is excluded by the reason it should be,
	/// rather than by somebody remembering to type it into a list.
	/// </summary>
	public int MarksMaxGroupSize { get; set; } = 5;

	/// <summary>
	/// ⭐ Three grouped toggles rather than one per content type. `TerritoryIntendedUse` does the
	/// classification underneath, so a new field operation sorts itself; the reader sees three boxes.
	///
	/// ⚠ Dungeons and normal raids are deliberately absent — four or eight names is easy to read, and
	/// with one friend you are already half the group.
	/// </summary>
	public bool MarksInPvp { get; set; } = true;

	/// <inheritdoc cref="MarksInPvp"/>
	public bool MarksInFieldOps { get; set; } = true;

	/// <summary>
	/// 24 players who all look alike. ⭐ Included after Bunny argued it is a PvE Frontline and was
	/// right: nameplate colour separates party from alliance, NOT your friends from the randoms in
	/// your own party. Cover is the sharpest case -- it can kill the caster, so which ally is under
	/// the cursor changes whether the risk is worth taking.
	/// </summary>
	public bool MarksInAllianceRaid { get; set; } = true;

	/// <summary>
	/// Deep dungeons -- Palace of the Dead, Heaven-on-High, Eureka Orthos, Pilgrim's Traverse.
	///
	/// ⚠⚠ THIS ONE IS IN FOR A DIFFERENT REASON THAN THE OTHERS, and the difference is worth keeping
	/// straight. Every group above answers *which of these identical allies is my friend* -- the
	/// marker discriminates. A deep dungeon party is four people who are all your friends, so by that
	/// test it should be excluded. deserok, 2026-08-22, after a Heaven-on-High run: *"we lose each
	/// other constantly."*
	///
	/// ⭐ The value there is **wayfinding, not identification**. The floors are dark randomised mazes
	/// and the marker says where somebody IS, which is a separate job the same overlay happens to do.
	///
	/// ⭐ Note the size threshold never fires here and needs no exception: a deep dungeon party caps at
	/// four, so the snapshot is at most three, always under the limit of five. The guard that exists
	/// to catch "the premade is the whole group" would have excluded this on principle -- it does not,
	/// only because the numbers happen to fall the right way. Worth knowing before anyone raises that
	/// limit.
	///
	/// ⭐ New key, so it reaches existing configs at this default -- additions are free, edits are what
	/// need a migration.
	/// </summary>
	public bool MarksInDeepDungeon { get; set; } = true;

	/// <summary>Mark anyone carrying a status you are watching. See DebuffMarksFeature.</summary>
	public bool DebuffMarksEnabled { get; set; } = true;

	/// <summary>
	/// ⚠ 1.0, not the 2.0 EphemeralMarks uses. A solid Font Awesome glyph reads far larger than an
	/// outlined reticle at the same nominal size -- deserok, on the first skull: *"massive though, 2x
	/// is a bad default for these."*
	///
	/// ⚠ This reaches new installs only. A changed default cannot touch a config that already exists,
	/// which is the trap recorded in ORIENTATION -- deserok sets his own back to 1.
	/// </summary>
	public float DebuffMarksScale { get; set; } = 1f;

	/// <inheritdoc cref="MarksHeight"/>
	public float DebuffMarksHeight { get; set; } = 2.0f;

	/// <inheritdoc cref="MarksLift"/>
	public float DebuffMarksLift { get; set; } = 34f;

	/// <summary>
	/// Draw a marker on YOURSELF, so the height and clearance can be set without hunting for a target.
	///
	/// ⭐ deserok's idea, from having actually configured the other one: *"to configure, we had to go to
	/// an explorer mode to render the marks, when a simple show on self toggle for configuring would be
	/// ideal."* Tuning a position against a thing you can only see in specific content is a bad loop.
	///
	/// ⚠ Persisted and defaulting OFF. It is deliberately not auto-cleared when the window closes --
	/// that would be surprising, and someone may want to walk around checking it.
	/// </summary>
	public bool DebuffMarksPreview { get; set; }

	/// <inheritdoc cref="DebuffMarksPreview"/>
	public bool MarksPreview { get; set; }

	/// <summary>
	/// ⚠ Empty by default, so the feature costs one bool test until somebody actually wants it.
	/// </summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<DebuffMark> DebuffMarks { get; set; } = new();


	/// <summary>
	/// The glyph for the party leader, and for everyone else. Bunny, #dev-notes 2026-08-22:
	/// *"Add different symbols for marks please :) Heart is needed^"*
	/// </summary>
	public Features.EphemeralMarks.MarkShape MarksLeaderShape { get; set; } =
		Features.EphemeralMarks.MarkShape.Star;

	/// <inheritdoc cref="MarksLeaderShape"/>
	public Features.EphemeralMarks.MarkShape MarksMemberShape { get; set; } =
		Features.EphemeralMarks.MarkShape.Reticle;

	/// <summary>Font Awesome codepoints for the two defaults, used when their shape is Icon.</summary>
	public int MarksLeaderGlyph { get; set; } = (int)Dalamud.Interface.FontAwesomeIcon.Star;

	/// <inheritdoc cref="MarksLeaderGlyph"/>
	public int MarksMemberGlyph { get; set; } = (int)Dalamud.Interface.FontAwesomeIcon.Heart;

	/// <summary>
	/// Per-person glyph overrides, keyed <c>Name@World</c>, case-insensitive.
	///
	/// ## ⚠⚠ THIS IS THE ONE THING IN MARKS THAT IS WRITTEN TO DISK. Read before extending it.
	///
	/// Everything else about this feature is deliberately ephemeral -- the snapshot dies when you
	/// leave, and the design notes say *"nothing is persisted"*. That sentence is now qualified rather
	/// than true, so it needs to be qualified HERE, where the exception lives.
	///
	/// ⭐ What makes it acceptable is the narrow shape deserok drew, 2026-08-23: *"We don't apply said
	/// marks all the time, or change how the plugin works, we simply allow a shape override to exist
	/// when they would already be applied."*
	///
	/// ⚠⚠ So: **an entry here can never cause somebody to be marked.** It only changes the glyph for
	/// someone already in the snapshot. Wire it anywhere into the decision of WHO gets a marker and it
	/// becomes the "list of people you care about" that this feature rejected twice on the record --
	/// bigger, and a roster to maintain.
	///
	/// ⚠ A name and a home world, typed by the user, and nothing else. Never an account or content id.
	/// </summary>
	[JsonProperty(ObjectCreationHandling = ReplaceList)]
	public List<MarkOverride> MarksOverrides { get; set; } = new();

	/// <summary>
	/// Show a Helldivers-style tag -- first initial plus party slot, "P2" -- under the marker.
	///
	/// ⭐ A NEW KEY replacing MarksShowNames rather than a changed default, on purpose. The name
	/// default was flipped to false and could not reach an existing config -- exactly the trap this
	/// file opens with. A new key takes its initialiser; additions are free, edits need a migration.
	///
	/// ⚠ OFF. The shapes carry the signal and a variable-width name jitters as people move; two glyphs
	/// never do. Worth turning on once there are three friends and only two shapes.
	/// </summary>
	public bool MarksShowTag { get; set; } = false;

	/// <summary>
	/// Ignore the content gate entirely. ⚠ FOR TESTING THE MARKER ITSELF -- shape, size, head height
	/// -- somewhere convenient rather than waiting for a Frontline.
	///
	/// ⚠ Deliberately NOT implemented by adding Trial to the content list. That list says where the
	/// feature is useful; adding a content type to make testing easy would leave it also saying where
	/// it was once convenient, and those are different facts that would drift apart.
	///
	/// ⭐ The group-size guard still applies, so this is a gate bypass rather than a "show everything"
	/// switch.
	/// </summary>
	public bool MarksEverywhere { get; set; } = false;

	/// <summary>
	/// How far above a character the marker floats, in yalms.
	///
	/// ⚠ YALMS, not pixels, so it tracks distance the way the nameplate does -- a pixel offset would
	/// clear the name up close and collide with it far away. Configurable because races differ by a
	/// lot between Lalafell and Roegadyn, and 2.4 was picked rather than measured.
	/// </summary>
	public float MarksHeight { get; set; } = 2.0f;

	/// <summary>
	/// Extra clearance above the anchor, in SCREEN PIXELS (scaled with the marker).
	///
	/// ⚠⚠ Both are needed and they do different jobs. A world offset shrinks with distance, so on its
	/// own the marker collapses onto the character exactly when they are far away and small -- landing
	/// on the nameplate, which is the moment it most needs to be clear. A pixel offset never shrinks.
	/// deserok, seeing it at range: *"anchoring to head position was bad anyways, because of distance
	/// causing overlap with the frame."*
	/// </summary>
	public float MarksLift { get; set; } = 34f;

	/// <summary>
	/// Marker size, on top of an automatic scale derived from the viewport height.
	///
	/// ⚠⚠ SIZE IN RAW PIXELS IS WRONG ACROSS SCREENS. The same 26px marker is a larger fraction of a
	/// 1080p screen than a 1440p one -- and the three people who will use this run 1440p large, 1440p
	/// small, and 1080p. So the drawing divides by viewport height against a 1440 baseline, which makes
	/// the default already correct on all three, and this multiplier is left for taste.
	///
	/// ⚠ Viewing distance is unknowable, which is why there is a slider at all rather than the scaling
	/// trying to be clever about physical screen size.
	/// </summary>
	/// ⭐ 2.0 by default: what deserok settled on after seeing it in game at 1440p, where the
	/// automatic viewport scale is exactly 1. New installs start where he ended up rather than at a
	/// size he already rejected as too small.
	public float MarksScale { get; set; } = 2f;

	/// <summary>⚠ A persisted Vector4, so it needs no Replace attribute -- that trap is collections.</summary>
	/// <summary>
	/// ⚠⚠ NOT yellow, blue or red -- those are the three Frontlines team colours, and a marker that
	/// matches a team colour is worse than no marker: it reads as faction information, which is the
	/// one thing you must not misread in PvP. deserok caught this before it shipped.
	///
	/// ⚠ Orange was the suggested alternative and is also avoided, narrowly: it sits between red and
	/// yellow and is close to Immortal Flames' branding.
	///
	/// ⭐ Magenta instead. It is the colour furthest from all three team colours, no FFXIV UI element
	/// uses it, and it is not the white of a target reticle -- so it cannot be mistaken for "this is
	/// my current target" either. Picker in the tab for anyone who disagrees.
	/// </summary>
	public System.Numerics.Vector4 MarksColour { get; set; } = new(1f, 0.32f, 0.85f, 1f);

	public bool AlertToast { get; set; } = true;
	public bool AlertChat { get; set; } = true;
	public bool AlertSound { get; set; } = true;

	// ── Interact ─────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Answer the dungeon-gimmick yes/no box that the interact key just caused.
	///
	/// ⭐ ON by default, and the <see cref="FcBuffsEnabled"/> precedent deliberately does NOT apply.
	/// That one defaults off because it acts without being asked -- it fires on a timer, and switching
	/// that on for an existing install would be a surprise. This one only ever finishes an action you
	/// started a second earlier by pressing the key, and without it the key is simply broken on any
	/// client not running YesAlready. A default that makes a feature work is a different thing from a
	/// default that starts new behaviour.
	///
	/// ⚠ What it will and will not answer is <see cref="Features.Interact.GimmickConfirm"/>, not a
	/// list here. The gate is a game sheet.
	/// </summary>
	public bool InteractAnswerGimmicks { get; set; } = true;

	/// <summary>
	/// Advance the NPC dialogue box on a press, the way Confirm does.
	///
	/// ⭐ ON by default. Bunny asked for it because without it the key is a downgrade during every
	/// quest -- she had spammed Confirm through dialogue for years. Restoring behaviour the real key
	/// already has is not the same kind of default as starting behaviour nothing had.
	///
	/// ⚠ Choice lists are NOT included and are not going to be. See
	/// <see cref="Features.Interact.TalkAdvance"/>.
	/// </summary>
	public bool InteractAdvanceTalk { get; set; } = true;

	/// <summary>
	/// With nothing to interact with, climb onto a nearby party member's mount.
	///
	/// ⭐ ON, because it is RESTORING something rather than adding it. deserok had this in his macro
	/// and a keybind silently dropped it -- see <see cref="Features.Interact.PillionRider"/>. Party
	/// members only.
	/// </summary>
	public bool InteractRidePillion { get; set; } = true;

	/// <summary>
	/// The key that operates things, bound directly rather than through a macro.
	///
	/// ⚠⚠ UNBOUND by default, and it must stay that way. A plugin that claims a key on install is a
	/// plugin that silently breaks somebody's existing bind, and this one loads on two other people's
	/// machines every time the feed is pushed.
	/// </summary>
	public Input.Keybind InteractKey { get; set; } = new();

	/// <summary>⚠ Unbound by default, like every keybind here.</summary>
	public Input.Keybind DrawSheatheKey { get; set; } = new();

	/// <summary>⚠ Unbound by default, like every keybind here.</summary>
	public Input.Keybind OpenWindowKey { get; set; } = new();

	// ── PartyJobs ───────────────────────────────────────────────────────────────────

	/// <summary>
	/// Draw the job icon the party list leaves blank for members outside your zone.
	///
	/// ⭐ ON. It only draws, and it asks the server nothing at all while the party is together -- see
	/// <see cref="Features.PartyJobs.PartyJobsFeature"/> for why that gate exists.
	/// </summary>
	public bool PartyJobsEnabled { get; set; } = true;


	/// <summary>
	/// ⚠⚠ Reaches an EXISTING config, which a changed default cannot.
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

		if (this.Version < 6) {
			// ⚠⚠ MarkShape 2-6 were hand-rolled Heart/Circle/Triangle/Square/Cross and are gone. The
			// numbers cannot simply be reused: a saved 2 would become whatever now sits at 2. So the
			// five are rewritten onto the Font Awesome glyph that replaced them.
			//
			// ⭐ Only deserok and Bunny ever ran those values, and only in a dev build -- but the whole
			// point of the lesson in ORIENTATION is that an enum's MEANING changing cannot reach an
			// existing config on its own, exactly like a changed default cannot.
			var retired = new Dictionary<int, Dalamud.Interface.FontAwesomeIcon> {
				[2] = Dalamud.Interface.FontAwesomeIcon.Heart,
				[3] = Dalamud.Interface.FontAwesomeIcon.Circle,
				[4] = Dalamud.Interface.FontAwesomeIcon.Play,
				[5] = Dalamud.Interface.FontAwesomeIcon.Square,
				[6] = Dalamud.Interface.FontAwesomeIcon.Times,
			};

			if (retired.TryGetValue((int)this.MarksLeaderShape, out var lead)) {
				this.MarksLeaderShape = Features.EphemeralMarks.MarkShape.Icon;
				this.MarksLeaderGlyph = (int)lead;
			}

			if (retired.TryGetValue((int)this.MarksMemberShape, out var member)) {
				this.MarksMemberShape = Features.EphemeralMarks.MarkShape.Icon;
				this.MarksMemberGlyph = (int)member;
			}

			foreach (var over in this.MarksOverrides) {
				if (!retired.TryGetValue((int)over.Shape, out var glyph))
					continue;
				over.Shape = Features.EphemeralMarks.MarkShape.Icon;
				over.Glyph = (int)glyph;
			}
		}

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

	/// <summary>
	/// Leave dyed pieces out of the dresser packing.
	///
	/// ⚠ Off by default. Packing destroys the dye either way, and the person running this is at
	/// 400-plus items and has long since made peace — see the Dresser notes. The switch exists for
	/// the minority who have not, and because a tool that runs habitually should not quietly eat
	/// something somebody did care about.
	/// </summary>
	public bool DresserSkipDyed { get; set; } = false;



	public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
