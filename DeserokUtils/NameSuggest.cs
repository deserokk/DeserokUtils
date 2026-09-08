using System;
using System.Collections.Generic;

namespace DeserokUtils;

internal static class NameSuggest {

	public static string? Closest(string typed, IEnumerable<string> candidates) {
		typed = typed.Trim();
		if (typed.Length < 4)
			return null;

		int limit = Math.Clamp(typed.Length / 5, 1, 3);
		string? best = null;
		int bestDistance = int.MaxValue;

		foreach (string candidate in candidates) {
			if (Math.Abs(candidate.Length - typed.Length) > limit)
				continue;
			if (candidate.Length == 0 || char.ToUpperInvariant(candidate[0]) != char.ToUpperInvariant(typed[0]))
				continue;

			int distance = Distance(typed, candidate, limit);
			if (distance < bestDistance) {
				bestDistance = distance;
				best = candidate;
				if (distance == 1)
					break;
			}
		}

		return bestDistance <= limit ? best : null;
	}

	private static int Distance(string a, string b, int limit) {
		int[] previous = new int[b.Length + 1];
		int[] current = new int[b.Length + 1];

		for (int j = 0; j <= b.Length; j++)
			previous[j] = j;

		for (int i = 1; i <= a.Length; i++) {
			current[0] = i;
			int rowBest = current[0];

			for (int j = 1; j <= b.Length; j++) {
				int cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
				rowBest = Math.Min(rowBest, current[j]);
			}

			if (rowBest > limit)
				return int.MaxValue;

			(previous, current) = (current, previous);
		}

		return previous[b.Length];
	}
}
