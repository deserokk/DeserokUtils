using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DeserokUtils.Features.ChatColours;

internal static class ChatPalette {

	private const int Buckets = 16;

	private static IReadOnlyList<(ushort Key, Vector3 Rgb)>? cached;

	public static IReadOnlyList<(ushort Key, Vector3 Rgb)> Colours => cached ??= Build();

	public static ushort KeyFor(int index) {
		var all = Colours;
		return all.Count == 0 ? (ushort)0 : all[((index % all.Count) + all.Count) % all.Count].Key;
	}

	public static Vector3 RgbFor(ushort key) {
		foreach (var (k, rgb) in Colours) {
			if (k == key) return rgb;
		}

		foreach (var (k, rgb) in Every) {
			if (k == key) return rgb;
		}

		return Vector3.One;
	}

	private static IReadOnlyList<(ushort Key, Vector3 Rgb)>? everyCached;

	public static IReadOnlyList<(ushort Key, Vector3 Rgb)> Every => everyCached ??= BuildEvery();

	private static IReadOnlyList<(ushort, Vector3)> BuildEvery() {
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.UIColor>();
		if (sheet is null)
			return Array.Empty<(ushort, Vector3)>();

		var all = new List<(ushort, Vector3)>();
		var seen = new HashSet<uint>();

		foreach (var row in sheet) {
			if (row.RowId > ushort.MaxValue)
				continue;

			var packed = row.Dark;
			var rgb = new Vector3(
				((packed >> 24) & 0xFF) / 255f,
				((packed >> 16) & 0xFF) / 255f,
				((packed >> 8) & 0xFF) / 255f);

			var luminance = (0.2126f * rgb.X) + (0.7152f * rgb.Y) + (0.0722f * rgb.Z);
			if (luminance < 0.30f)
				continue;

			if (!seen.Add(packed))
				continue;

			all.Add(((ushort)row.RowId, rgb));
		}

		return all;
	}

	public static ushort NearestKey(Vector3 wanted) {
		var best = KeyFor(0);
		var bestDistance = float.MaxValue;

		foreach (var (key, rgb) in Every) {
			var d = Vector3.DistanceSquared(rgb, wanted);
			if (d >= bestDistance)
				continue;

			bestDistance = d;
			best = key;
		}

		return best;
	}

	private static IReadOnlyList<(ushort, Vector3)> Build() {
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.UIColor>();
		if (sheet is null)
			return Array.Empty<(ushort, Vector3)>();

		var candidates = new List<(ushort Key, Vector3 Rgb, float Hue)>();

		foreach (var row in sheet) {
			if (row.RowId > ushort.MaxValue)
				continue;

			var packed = row.Dark;
			var rgb = new Vector3(
				((packed >> 24) & 0xFF) / 255f,
				((packed >> 16) & 0xFF) / 255f,
				((packed >> 8) & 0xFF) / 255f);

			var max = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
			var min = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));

			var luminance = (0.2126f * rgb.X) + (0.7152f * rgb.Y) + (0.0722f * rgb.Z);
			if (luminance is < 0.42f or > 0.93f)
				continue;

			var saturation = max <= 0f ? 0f : (max - min) / max;
			if (saturation < 0.35f)
				continue;

			candidates.Add(((ushort)row.RowId, rgb, Hue(rgb, max, min)));
		}

		var picked = candidates
			.GroupBy(c => Math.Clamp((int)(c.Hue * Buckets), 0, Buckets - 1))
			.OrderBy(g => g.Key)
			.Select(g => g.OrderBy(c => c.Key).First())
			.Select(c => (c.Key, c.Rgb))
			.ToList();

		Plugin.Log.Information($"ChatColours: palette of {picked.Count} from {candidates.Count} candidates.");
		return picked;
	}

	private static float Hue(Vector3 c, float max, float min) {
		var delta = max - min;
		if (delta <= 0f)
			return 0f;

		float h;
		if (Math.Abs(max - c.X) < float.Epsilon) h = ((c.Y - c.Z) / delta) % 6f;
		else if (Math.Abs(max - c.Y) < float.Epsilon) h = ((c.Z - c.X) / delta) + 2f;
		else h = ((c.X - c.Y) / delta) + 4f;

		h /= 6f;
		return h < 0f ? h + 1f : h;
	}
}
