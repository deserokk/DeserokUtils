using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeserokUtils.Features.PvpEffects;

internal static class PvpVfx {

	private static readonly Regex CommonKey = new(@"^(?:ability|magic|ws)/pvp_common/[a-z]+\d+(?:_[a-z0-9]+)?$",
	                                              RegexOptions.Compiled);

	private static readonly Regex ChargeTime = new(@"Gauge Charge Time:\s*(\d+)s", RegexOptions.Compiled);

	internal sealed record Move(uint Key, string Name, IReadOnlyList<string> AnimationKeys) {

		public int Charge { get; init; }

		public bool SharedWithPve { get; init; }
	}

	internal sealed record JobEntry(uint JobId, string Job, Move LimitBreak, IReadOnlyList<Move> Extras);

	private static List<JobEntry>? entries;

	internal static IReadOnlyList<JobEntry> Jobs => entries ??= Build();

	private sealed record Ability(uint Id, string Name, List<string> Keys, int Charge);

	private static List<JobEntry> Build() {
		var found = new List<JobEntry>();

		try {
			var byJob = new Dictionary<uint, (string Job, List<Ability> Abilities)>();

			foreach (var action in Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>()) {
				if (!action.IsPvP) continue;

				var jobId = action.ClassJob.RowId;
				if (jobId == 0) continue;

				var name = action.Name.ExtractText();
				if (name.Length == 0) continue;

				var keys = Animations(action).Where(Usable).Distinct().ToList();
				if (keys.Count == 0) continue;

				var job = action.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? string.Empty;
				if (!byJob.TryGetValue(jobId, out var entry)) entry = (job, []);

				if (entry.Abilities.FirstOrDefault(a => a.Name == name) is { } already) {
					foreach (var key in keys.Where(key => !already.Keys.Contains(key))) already.Keys.Add(key);
				}
				else {
					entry.Abilities.Add(new Ability(action.RowId, name, keys, ChargeSeconds(action.RowId)));
				}

				byJob[jobId] = entry;
			}

			foreach (var (jobId, entry) in byJob) {

				var main = entry.Abilities.FirstOrDefault(a => a.Charge > 0);
				if (main is null) continue;

				var limitBreak = new Move(main.Id, main.Name, main.Keys) { Charge = main.Charge };

				var extras = entry.Abilities.Where(a => a != main)
				                  .Select(a => new Move(a.Id, a.Name, a.Keys) {
					                  Charge = a.Charge,
					                  SharedWithPve = !a.Keys.All(key => CommonKey.IsMatch(key)),
				                  })
				                  .OrderBy(a => a.SharedWithPve)
				                  .ThenBy(a => a.Name, StringComparer.Ordinal)
				                  .ToList();

				found.Add(new JobEntry(jobId, entry.Job, limitBreak, extras));
			}
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "PvP effects: could not read the limit break table.");
		}

		found.Sort((a, b) => string.CompareOrdinal(a.Job, b.Job));
		return found;
	}

	private static bool Usable(string key)
		=> key.Length > 0 && !key.StartsWith("normal_hit", StringComparison.Ordinal)
		                  && !key.StartsWith("ability/no_mot", StringComparison.Ordinal);

	private static IEnumerable<string> Animations(Lumina.Excel.Sheets.Action action) {
		yield return action.AnimationEnd.ValueNullable?.Key.ExtractText() ?? string.Empty;
		yield return action.ActionTimelineHit.ValueNullable?.Key.ExtractText() ?? string.Empty;
	}

	private static int ChargeSeconds(uint actionId) {
		var match = ChargeTime.Match(Description(actionId));
		return match.Success && int.TryParse(match.Groups[1].Value, out var seconds) ? seconds : 0;
	}

	private static string Description(uint actionId) {
		try {
			return Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.ActionTransient>()
			                  .GetRowOrDefault(actionId)?.Description.ExtractText() ?? string.Empty;
		}
		catch {
			return string.Empty;
		}
	}
}
