using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace DeserokUtils.Features.Fanfare.Notify;

internal sealed class Rarity {
	private const string ResourceName = "Fanfare.rarity.txt";

	private readonly Dictionary<uint, float> owned = new();

	private readonly HashSet<uint> tooNew = new();

	private DateTime? newUntil;

	private readonly float[] sorted = [];

	internal int Total => this.owned.Count;

	internal bool Loaded => this.owned.Count > 0;

	internal Rarity() {
		try {
			using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
			if (stream is null) {
				Plugin.Log.Error($"rarity: embedded resource '{ResourceName}' not found; rarity disabled.");
				return;
			}

			using var reader = new StreamReader(stream);
			var values = new List<float>();

			while (reader.ReadLine() is { } line) {
				if (line.Length == 0)
					continue;

				if (line[0] == '#') {
					const string marker = "# NewUntil: ";
					if (line.StartsWith(marker, StringComparison.Ordinal)
						&& DateTime.TryParseExact(
							line.AsSpan(marker.Length, Math.Min(10, line.Length - marker.Length)),
							"yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
							out var until))
						this.newUntil = until;

					continue;
				}

				var split = line.IndexOf(':');
				if (split <= 0)
					continue;

				if (!uint.TryParse(line.AsSpan(0, split), out var id))
					continue;

				var rest = line.AsSpan(split + 1);
				var flag = rest.IndexOf(':');
				var isNew = false;
				if (flag >= 0) {
					isNew = rest[(flag + 1)..].Trim().SequenceEqual("new");
					rest = rest[..flag];
				}

				if (!float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
					continue;

				this.owned[id] = percent;

				if (isNew) {
					this.tooNew.Add(id);
					continue;
				}

				values.Add(percent);
			}

			values.Sort();
			this.sorted = values.ToArray();

			var flagged = this.newUntil is DateTime d
				? $", {this.tooNew.Count} too new to rate until {d:yyyy-MM-dd}"
				: string.Empty;

			Plugin.Log.Information($"rarity: loaded {this.owned.Count} achievements{flagged}.");
		} catch (Exception ex) {

			Plugin.Log.Error(ex, "rarity: failed to load; continuing without it.");
		}
	}

	internal float? PercentOwned(uint achievementId)
		=> this.TooNew(achievementId) ? null
			: this.owned.TryGetValue(achievementId, out var percent) ? percent : null;

	internal bool TooNew(uint achievementId)
		=> this.newUntil is DateTime until
			&& DateTime.UtcNow.Date < until
			&& this.tooNew.Contains(achievementId);

	internal List<uint> IdsAtOrBelow(float threshold) {
		var ids = new List<uint>();
		foreach (var (id, percent) in this.owned) {
			if (percent <= threshold && !this.TooNew(id))
				ids.Add(id);
		}

		return ids;
	}

	internal int CountAtOrBelow(float threshold) {
		if (this.sorted.Length == 0)
			return 0;

		int low = 0, high = this.sorted.Length;
		while (low < high) {
			var mid = (low + high) / 2;
			if (this.sorted[mid] <= threshold)
				low = mid + 1;
			else
				high = mid;
		}

		return low;
	}
}
