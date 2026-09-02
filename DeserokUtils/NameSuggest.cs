using System;
using System.Collections.Generic;

namespace DeserokUtils;

/// <summary>
/// "Did you mean Phoenix Down?" -- the nearest real name to one that matched nothing.
///
/// ⚠⚠ WRITTEN BECAUSE A TYPO COST A TEST. deserok's icon test macro was named "Pheonix Down", e and
/// o swapped, and every layer did exactly what it should: the icon resolver found no such item and
/// stayed silent, which reads identically to "the feature does not work". The name was wrong in the
/// macro's body twice more, so /watch and /ifmo would have failed too.
///
/// ⭐ The people this plugin is FOR are not going to suspect their own spelling. Bunny is the test
/// for every message here, and "no usable item named X" invites re-reading the code rather than the
/// word. One suggestion turns a dead end into a fix.
///
/// ⚠ Deliberately NOT fuzzy matching at the lookup itself. Guessing what somebody meant and then
/// USING it is how a macro quietly spends the wrong item; guessing and then saying so is free.
/// </summary>
internal static class NameSuggest {
	/// <summary>
	/// The closest candidate to <paramref name="typed"/>, or null when nothing is close enough.
	///
	/// ⚠ PREFILTERED on first letter and length, and that is a real limit rather than an
	/// optimisation detail: a name whose FIRST character is wrong will never be suggested. The reason
	/// is that one caller is the macro icon resolver, which asks this about every macro that is not
	/// named after an action -- most of them -- from the UI thread. Comparing "Pull timer" against
	/// eight thousand names per macro per login is not a cost worth paying for a hint. Transposition
	/// typos, which are the common case and the one that started this, keep their first letter.
	/// </summary>
	public static string? Closest(string typed, IEnumerable<string> candidates) {
		typed = typed.Trim();
		if (typed.Length < 4)
			return null;

		// ⚠ Scaled with length rather than fixed at 2: "Pheonix Down" is one transposition in twelve
		// characters, while two edits in a five-character name is a different word.
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

	/// <summary>
	/// Levenshtein distance, case-insensitive, abandoning early once the row cannot beat
	/// <paramref name="limit"/>.
	///
	/// ⚠ Two rows rather than a full matrix. The strings are short, but this runs over thousands of
	/// candidates and the allocation is the only part that would show up.
	/// </summary>
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
