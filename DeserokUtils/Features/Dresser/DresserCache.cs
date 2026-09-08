using System;
using System.Collections.Generic;
using System.IO;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

using Newtonsoft.Json;

namespace DeserokUtils.Features.Dresser;

internal sealed class DresserCache {

	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;

	public ulong ContentId { get; set; }

	public DateTime TakenAt { get; set; }

	public bool MaybeStale { get; set; }

	public string CharacterName { get; set; } = string.Empty;

	public HashSet<uint> LoosePieces { get; set; } = new();

	public Dictionary<uint, List<int>> OutfitSlots { get; set; } = new();

	public HashSet<uint> Armoire { get; set; } = new();

	public static uint PureItemId(uint itemId) => itemId > 1_000_000 ? itemId - 1_000_000 : itemId;

	public int Used { get; set; }

	public int Capacity { get; set; }

	private static string Path
		=> System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "dresser-cache.json");

	private static DresserCache? loaded;
	private static bool tried;

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

	public static void Forget() {
		loaded = null;
		tried = false;
	}

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

			return cache.Version == CurrentVersion ? cache : null;
		}
		catch (Exception ex) {
			Plugin.Log.Warning(ex, "Could not read the dresser cache.");
			return null;
		}
	}

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

	private static unsafe ulong LocalContentId() {
		var state = PlayerState.Instance();
		return state is null ? 0ul : state->ContentId;
	}

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
