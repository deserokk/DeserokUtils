using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace DeserokUtils.Features.Fanfare.Notify;

internal sealed class Rarity {
	private const string ResourceName = "Fanfare.rarity.txt";

	private readonly Dictionary<uint, float> owned = new();

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
				if (line.Length == 0 || line[0] == '#')
					continue;

				var split = line.IndexOf(':');
				if (split <= 0)
					continue;

				if (!uint.TryParse(line.AsSpan(0, split), out var id))
					continue;
				if (!float.TryParse(line.AsSpan(split + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
					continue;

				this.owned[id] = percent;
				values.Add(percent);
			}

			values.Sort();
			this.sorted = values.ToArray();

			Plugin.Log.Information($"rarity: loaded {this.owned.Count} achievements.");
		} catch (Exception ex) {

			Plugin.Log.Error(ex, "rarity: failed to load; continuing without it.");
		}
	}

	internal float? PercentOwned(uint achievementId)
		=> this.owned.TryGetValue(achievementId, out var percent) ? percent : null;

	internal List<uint> IdsAtOrBelow(float threshold) {
		var ids = new List<uint>();
		foreach (var (id, percent) in this.owned) {
			if (percent <= threshold)
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
