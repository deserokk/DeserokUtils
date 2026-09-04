using System;
using System.Collections.Generic;
using System.IO;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

using Newtonsoft.Json;

namespace DeserokUtils.Features.Dresser;

/// <summary>
/// What was in the glamour dresser the last time anybody looked, kept on disk.
///
/// ⭐⭐⭐ THE POINT IS WHERE THE ANSWER IS AVAILABLE, not what it costs to compute. The dresser is
/// only readable while you are standing at one — <c>PrismBoxLoaded</c> stays false until the game
/// has sent the contents, and it sends them when you open the thing. So "do I already own this
/// coat?" is a question you can currently only ask in the exact place where you can already see the
/// answer, and never in the place where you need it: a vendor list, the market board, a loot roll.
///
/// Writing the scan down once turns a question you can only ask at the dresser into one you can ask
/// anywhere. Everything built on top of this is presentation.
///
/// ⭐ Q and Bunny's suggestion, 2026-09-04: *"since we're scanning anyways, cache what is in the
/// dresser and then mark what we have OR what we need to complete a set."*
///
/// ## ⚠ Per character, and the file says which
///
/// A glamour dresser belongs to a character, not to an account. A cache that forgot that would
/// confidently tell you your alt owns things it has never seen — plausible, wrong, and silent,
/// which is the failure this project keeps meeting. The content id is the key.
///
/// ## ⭐⭐ Staleness is a FLAG, not a timestamp
///
/// The dresser cannot change unless its window is open, and this plugin already knows when that is
/// because it draws a button on it. So the age of a snapshot says nothing: one from three weeks ago
/// is exactly correct if nobody has opened the thing since. <see cref="MaybeStale"/> carries the
/// only fact that matters — opened, and not rescanned — which means a tooltip stays silent in the
/// case that is always true and speaks in the one case that is not.
/// </summary>
internal sealed class DresserCache {
	/// <summary>⚠ Bumped when the shape changes, so an old file is ignored rather than misread.</summary>
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;

	/// <summary>Which character this belongs to. ⚠ 0 means a file written before we knew.</summary>
	public ulong ContentId { get; set; }

	/// <summary>When the scan was taken. ⚠ UTC. Diagnostic only — see MaybeStale.</summary>
	public DateTime TakenAt { get; set; }

	/// <summary>
	/// The dresser has been opened since this was written, and not rescanned.
	///
	/// ⭐⭐⭐ deserok, 2026-09-04, and it replaces a worse design outright: *"this is one
	/// snapshot that only can be stale if the user opens the glamour chest, puts something in and
	/// doesn't scan, and we know when the dresser is open because we have to draw the button... if we
	/// make a flag that indicates 'might be stale' that's only cleared when a scan happens, we can
	/// skip the verbose 'as of x' indication."*
	///
	/// He is right, and the reason is worth keeping: **the dresser cannot change unless its window
	/// is open**, and this plugin already knows when that is, because it draws a button on it. So
	/// staleness is not a function of elapsed time at all — a cache from three weeks ago is exactly
	/// correct if nobody has opened the thing since. Stamping "as of 14 hours ago" on every tooltip
	/// would be noise dressed as diligence: technically true, never actionable, and shown a hundred
	/// times more often than the one case it exists for.
	///
	/// ⚠ Which makes this flag the honest version of the same care. Silent when it cannot be wrong;
	/// says so when it can.
	/// </summary>
	public bool MaybeStale { get; set; }

	public string CharacterName { get; set; } = string.Empty;

	/// <summary>Every loose piece sitting in the dresser, by item id.</summary>
	public HashSet<uint> LoosePieces { get; set; } = new();

	/// <summary>
	/// Packed outfits, and which of their slots are filled.
	///
	/// ⭐ The slots matter as much as the set does. "You own the Rebel Set" is not the useful fact
	/// when you are looking at a pair of Rebel Boots in a vendor list — "your Rebel Set is missing
	/// exactly that slot" is.
	/// </summary>
	public Dictionary<uint, List<int>> OutfitSlots { get; set; } = new();

	/// <summary>Armoire entries already stored. ⚠ Cabinet row ids, not item ids.</summary>
	public HashSet<uint> Armoire { get; set; } = new();

	/// <summary>
	/// Strip the high-quality marker off an item id.
	///
	/// ⭐⭐ The convention is <c>id + 1,000,000</c> for HQ, taken from Seventhxiv/Collections, which
	/// normalises every id it handles. ⚠ NOT a bug in the dresser scan: 491 entries of deserok's real
	/// dresser log, checked 2026-09-04, contain no id above a million, so the dresser stores them
	/// plain.
	///
	/// ⚠⚠ It matters for what comes NEXT. The flagged form is what the item-detail agent carries,
	/// so the moment anything reads "which item is the player hovering", an HQ item arrives as
	/// itemId + 1000000 and every lookup against a sheet or against this cache misses. That failure
	/// is silent and looks exactly like "we do not own it".
	/// </summary>
	public static uint PureItemId(uint itemId) => itemId > 1_000_000 ? itemId - 1_000_000 : itemId;

	/// <summary>How many dresser entries were in use, for the "as of" line.</summary>
	public int Used { get; set; }

	public int Capacity { get; set; }

	private static string Path
		=> System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "dresser-cache.json");

	/// <summary>
	/// The cache as loaded this session. ⚠ Null until something asks, and null again if the file is
	/// for somebody else.
	/// </summary>
	private static DresserCache? loaded;
	private static bool tried;

	/// <summary>
	/// What we know about this character's dresser, or null.
	///
	/// ⚠ Returns null for a cache belonging to a DIFFERENT character rather than falling back to it.
	/// An answer from the wrong character is worse than no answer.
	/// </summary>
	public static DresserCache? Current {
		get {
			if (!tried) {
				tried = true;
				loaded = Read();
			}

			if (loaded is null) return null;

			var me = LocalContentId();
			if (me != 0 && loaded.ContentId != 0 && loaded.ContentId != me) return null;

			return loaded;
		}
	}

	/// <summary>⚠ Called on a character switch: the next read has to go back to disk.</summary>
	public static void Forget() {
		loaded = null;
		tried = false;
	}

	/// <summary>Write a fresh scan down. ⚠ Never throws at the caller; a cache is not worth a crash.</summary>
	public static void Save(DresserScan.Result r) {
		if (!r.Loaded) return;

		try {
			var cache = new DresserCache {
				ContentId = LocalContentId(),
				CharacterName = Plugin.Objects.LocalPlayer?.Name.TextValue ?? string.Empty,
				TakenAt = DateTime.UtcNow,
				Used = r.Used,
				Capacity = r.Capacity,
			};

			foreach (var piece in r.LoosePieceIds) cache.LoosePieces.Add(piece);

			foreach (var outfit in r.Packed) {
				var filled = new List<int>();
				foreach (var (slot, _, isFilled) in outfit.Slots) {
					if (isFilled) filled.Add(slot);
				}

				// ⚠ Last one wins if a set somehow appears twice. Two outfits of the same set is a
				// state this tool used to CREATE, so it is not hypothetical.
				cache.OutfitSlots[outfit.ItemId] = filled;
			}

			foreach (var row in r.ArmoireRows) cache.Armoire.Add(row);

			File.WriteAllText(Path, JsonConvert.SerializeObject(cache, Formatting.Indented));

			loaded = cache;
			tried = true;
		}
		catch (Exception ex) {
			Plugin.Log.Warning(ex, "Could not write the dresser cache.");
		}
	}

	private static DresserCache? Read() {
		try {
			if (!File.Exists(Path)) return null;

			var cache = JsonConvert.DeserializeObject<DresserCache>(File.ReadAllText(Path));
			if (cache is null) return null;

			// ⚠ An older shape is discarded, not migrated. There is nothing here worth migrating —
			// standing at the dresser once rebuilds all of it.
			return cache.Version == CurrentVersion ? cache : null;
		}
		catch (Exception ex) {
			Plugin.Log.Warning(ex, "Could not read the dresser cache.");
			return null;
		}
	}

	/// <summary>
	/// Mark the cache as possibly behind, and write that down.
	///
	/// ⚠ Persisted rather than kept in memory: the case this exists for is somebody rearranging
	/// their dresser, closing the game, and coming back tomorrow. A flag that died with the session
	/// would be clear exactly when it should not be.
	/// </summary>
	public static void MarkStale() {
		var cache = Current;
		if (cache is null || cache.MaybeStale) return;

		cache.MaybeStale = true;

		try {
			File.WriteAllText(Path, JsonConvert.SerializeObject(cache, Formatting.Indented));
		}
		catch (Exception ex) {
			Plugin.Log.Warning(ex, "Could not flag the dresser cache as stale.");
		}
	}

	/// <summary>
	/// Which character is playing. ⚠ From ClientStructs, because this Dalamud's IClientState
	/// exposes neither a content id nor a local player — checked, not assumed.
	/// </summary>
	private static unsafe ulong LocalContentId() {
		var state = PlayerState.Instance();
		return state is null ? 0ul : state->ContentId;
	}

	/// <summary>How old the snapshot is. ⚠ Diagnostics and the Dresser tab only — never a tooltip.</summary>
	public string Age {
		get {
			var span = DateTime.UtcNow - this.TakenAt;

			if (span < TimeSpan.FromMinutes(2)) return "just now";
			if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} minutes ago";
			if (span < TimeSpan.FromHours(36)) return $"{(int)span.TotalHours} hours ago";

			return $"{(int)span.TotalDays} days ago";
		}
	}
}
