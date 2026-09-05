using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using FFXIVClientStructs.FFXIV.Client.Game;

using Lumina.Excel.Sheets;

namespace DeserokUtils.Features.Dresser;

/// <summary>
/// Writes the whole scan to a file.
///
/// ⭐⭐ Because a yes/no relayed through a person drops exactly the detail the probe existed to
/// capture. Two things in this feature could not be settled from documentation, and both are
/// answerable from a real dresser in one run — but only if the raw values reach the person who has
/// to interpret them, rather than a summary of them.
///
/// The two questions:
///
///  1. **Does <c>IsSetSlotUnlocked</c> mean "this slot is filled" or "this slot is free"?** They are
///     opposites, and getting it backwards makes the tool offer to add pieces already packed. The
///     log prints the raw boolean per slot beside the item name, against an outfit deserok can see
///     in game.
///  2. **Which Stain rows are Jet Black and Pure White?** Rather than guessing, every distinct dye
///     found in the dresser is listed with its id and name. The answer is then read off, not
///     assumed.
///
/// ⚠ Appends rather than overwrites, so a second run can be compared against the first.
/// </summary>
internal static unsafe class DresserLog {
	public static string Path
		=> System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "dresser.log");

	public static string? Write(DresserScan.Result r) {
		var sb = new StringBuilder();

		sb.AppendLine($"===== dresser scan {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");

		if (r.Problem is { } problem) {
			sb.AppendLine($"PROBLEM: {problem}");
			return Flush(sb);
		}

		sb.AppendLine($"used {r.Used}/{r.Capacity}");

		// ⚠ First, and named. These are outfits this tool emptied; they need to be visible in
		// the log without scrolling past everything that went right.
		if (r.EmptyOutfits.Count > 0)
			sb.AppendLine($"EMPTY OUTFITS ({r.EmptyOutfits.Count}): {string.Join(", ", r.EmptyOutfits)}");

		if (r.FullyArmoireOutfits.Count > 0) {
			sb.AppendLine($"OUTFITS THE ARMOIRE WOULD TAKE WHOLE ({r.FullyArmoireOutfits.Count}):");
			foreach (var (name, pieces) in r.FullyArmoireOutfits)
				sb.AppendLine($"  {name} ({pieces} piece(s))");
		}

		if (r.RedundantWithOutfit.Count > 0)
			sb.AppendLine($"spare pieces their outfit already holds ({r.RedundantWithOutfit.Count}): "
			              + string.Join(", ", r.RedundantWithOutfit));

		sb.AppendLine();

		// Sanity on the sheet read itself. If this is zero the rest is meaningless, and it would
		// otherwise look like an unusually tidy dresser.
		var sets = Plugin.Data.GetExcelSheet<MirageStoreSetItem>();
		sb.AppendLine($"MirageStoreSetItem rows: {sets?.Count.ToString() ?? "SHEET MISSING"}");
		sb.AppendLine();

		// ── Question 1: what does IsSetSlotUnlocked actually mean ────────────────────────
		sb.AppendLine("--- packed outfits, raw slot flags ---");
		sb.AppendLine("(compare against the game: a slot you own should read one way consistently)");

		foreach (var o in r.Packed) {
			var filled = o.Slots.Count(x => x.Filled);
			sb.AppendLine($"  [{o.Index}] {o.Name}  (item {o.ItemId})  flagged-true {filled}/{o.Slots.Count}");
			foreach (var (slot, item, flag) in o.Slots)
				sb.AppendLine($"        slot {slot,2} {DresserScan.SlotNames[slot],-10} " +
				              $"IsSetSlotUnlocked={flag,-5} {item}");
		}

		if (r.Packed.Count == 0) sb.AppendLine("  (none)");
		sb.AppendLine();

		// ── Question 2: which stain ids are the expensive ones ───────────────────────────
		sb.AppendLine("--- every dye present in the dresser ---");
		sb.AppendLine("(the expensive list is resolved from the sheet by English name, not hardcoded)");

		var mirage = MirageManager.Instance();
		if (mirage is not null) {
			var ids = mirage->PrismBoxItemIds;
			var s0 = mirage->PrismBoxStain0Ids;
			var s1 = mirage->PrismBoxStain1Ids;

			var seen = new Dictionary<uint, int>();
			for (var i = 0; i < ids.Length; i++) {
				if (ids[i] == 0) continue;
				foreach (var stain in new uint[] { s0[i], s1[i] }) {
					if (stain == 0) continue;
					seen[stain] = seen.GetValueOrDefault(stain) + 1;
				}
			}

			foreach (var (stain, count) in seen.OrderBy(x => x.Key)) {
				var name = Plugin.Data.GetExcelSheet<Stain>()?.GetRowOrDefault(stain)?.Name.ExtractText();
				sb.AppendLine($"  stain {stain,3}  x{count,-4} {name ?? "?"}");
			}

			if (seen.Count == 0) sb.AppendLine("  (nothing dyed)");
		}

		sb.AppendLine();

		// ── The findings, so the arithmetic can be checked by hand ───────────────────────
		sb.AppendLine("--- would add to an outfit already packed ---");
		foreach (var a in r.Additions) {
			sb.AppendLine($"  [{a.OutfitIndex}] {a.OutfitName}");
			foreach (var p in a.Pieces)
				sb.AppendLine($"        + slot {p.Slot,2} {p.Name}  (dresser index {p.Index})");
		}
		if (r.Additions.Count == 0) sb.AppendLine("  (none)");
		sb.AppendLine();

		sb.AppendLine("--- would become a new outfit ---");
		foreach (var o in r.NewOutfits) {
			sb.AppendLine($"  {o.SetName}  (set item {o.SetItemId})  {o.Pieces.Count} pieces, saves {o.Pieces.Count - 1}");
			foreach (var p in o.Pieces)
				sb.AppendLine($"        slot {p.Slot,2} {p.Name}  (dresser index {p.Index})");
		}
		if (r.NewOutfits.Count == 0) sb.AppendLine("  (none)");
		sb.AppendLine();

		sb.AppendLine("--- duplicates ---");
		foreach (var d in r.Duplicates)
			sb.AppendLine($"  {d.Name} x{d.Indices.Count}  (item {d.ItemId}, indices {string.Join(",", d.Indices)})");
		if (r.Duplicates.Count == 0) sb.AppendLine("  (none)");
		sb.AppendLine();

		sb.AppendLine($"additions {r.SlotsFromAdditions} + new {r.SlotsFromNewOutfits} " +
		              $"+ dupes {r.SlotsFromDuplicates} = {r.SlotsRecoverable} recoverable");
		sb.AppendLine($"prisms ~{r.PrismsNeeded}, free bag slots needed {r.FreeSlotsNeeded}");
		sb.AppendLine();

		return Flush(sb);
	}

	/// <summary>
	/// One line of the packing trace.
	///
	/// ⭐⭐ The scan dumps are a snapshot; this is the sequence. When a run stops halfway, the
	/// question is always *which step, with what values* — and that is precisely what a person
	/// relaying "it stopped" cannot tell me. Timestamped so a stall is visible as a gap rather than
	/// having to be described.
	/// </summary>
	/// <summary>An outcome. Always written — this runs unattended.</summary>
	public static void Step(string line) {
		try {
			var dir = Plugin.PluginInterface.ConfigDirectory;
			if (!dir.Exists) dir.Create();
			File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
		}
		catch {
			// A trace that throws would be worse than no trace.
		}
	}

	/// <summary>A step of the machinery. ⚠ Only when Verbose is on; hundreds of lines a run.</summary>
	public static void Trace(string line) {
		if (!Plugin.Verbose) return;
		Step(line);
	}

	private static string? Flush(StringBuilder sb) {
		try {
			var dir = Plugin.PluginInterface.ConfigDirectory;
			if (!dir.Exists) dir.Create();
			File.AppendAllText(Path, sb.ToString() + Environment.NewLine);
			return Path;
		}
		catch (Exception ex) {
			Plugin.Log.Warning($"Could not write the dresser log: {ex.Message}");
			return null;
		}
	}
}
