using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Dresser;

/// <summary>
/// Reads the game's "glamour-ready items" list, so the packer can cog the row holding the piece it
/// wants instead of guessing which row that is.
///
/// ⭐⭐⭐ THIS IS THE FIX FOR EVERY DRESSER FAILURE SO FAR. The cogwheel callback takes a row number,
/// and the packer had no way to know which row was its piece — so it used row 0, then walked rows
/// hoping one would work. Both are guesses, and a guess here does not fail quietly: it opens the
/// outfit dialog for somebody ELSE'S set and builds that instead. That is where the duplicate
/// Vanguard Attire of Scouting came from on 2026-09-03, and the empty outfits before it.
///
/// ⚠⚠ THE ROW IS NOT THE POSITION ON SCREEN. That list interleaves slot headers — "Main Hand",
/// "Body", "Hands" — with the items under them, and the headers do NOT consume a row number. Rows
/// run 0..n-1 across the ITEMS only. Every attempt to reason about the visible layout was wrong
/// because of this.
///
/// ## Where the layout came from
///
/// ⭐⭐ MEASURED, not derived. DresserProbe dumped the window's AtkValues mid-run on 2026-09-03 and
/// the shape was plain: from index 9, six values per entry, the first being the row number and the
/// second an icon id.
/// <code>
///    9: 0   10: 36115  ...   row 0, Veldian Bayonet
///   15: 0   16: 0      ...   a gap (all zero)
///   21: 1   22: 43844  ...   row 1, Whisperfine Woolen Coat
///   27: 0   28: 0      ...   a gap
///   33: 2   34: 56279  ...   row 2
///   39: 3   40: 56281  ...   row 3
/// </code>
/// Row numbers ran 0..8 for nine items with four all-zero entries interleaved, and cogging row 1
/// opened the Whisperfine Wool Attire dialog — which reported icon 43844, matching. That closes the
/// loop: the row here is the row the cogwheel wants.
///
/// ⚠ AN ICON, NOT AN ITEM ID. Nothing in the values array is the item id, so the match has to go
/// through Item.Icon. Two different items can share an icon; that is why the packer still verifies
/// which set the dialog opened before committing anything. This narrows the guess to almost
/// nothing, and the check after it removes the rest.
/// </summary>
internal static unsafe class DresserList {
	/// <summary>⚠ Measured. The first nine values are the window's own header — prism count and such.</summary>
	private const int FirstEntry = 9;

	/// <summary>⚠ Measured. Six values per entry: row, icon, and four whose meaning is unknown.</summary>
	private const int Stride = 6;

	/// <summary>A ceiling, so a misread array cannot spin. Far more than any real bag holds.</summary>
	private const int MaxEntries = 400;

	/// <summary>Which row of the list holds the item with this icon. ⚠ -1 when it is not there.</summary>
	public static int RowForIcon(uint icon) {
		if (icon == 0) return -1;

		var rows = Rows();
		var found = -1;

		foreach (var (row, rowIcon) in rows) {
			if (rowIcon != icon) continue;

			if (found >= 0) {
				// ⚠ Two rows showing the same icon. Either two copies of the item — in which case
				// both rows lead to the same set and either will do — or two different items that
				// share artwork. The set check in DresserPacker settles which.
				DresserLog.Trace($"  list: icon {icon} appears on rows {found} and {row}; taking {found}");
				break;
			}

			found = row;
		}

		return found;
	}

	/// <summary>Every item row the window is currently showing, as (row, icon).</summary>
	public static List<(int Row, uint Icon)> Rows() {
		var rows = new List<(int, uint)>();

		var addon = Plugin.GameGui.GetAddonByName("MiragePrismPrismBoxCrystallize", 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return rows;

		var unit = (AtkUnitBase*)addon.Address;
		var count = unit->AtkValuesCount;

		for (var i = 0; i < MaxEntries; i++) {
			var at = FirstEntry + (i * Stride);
			if (at + 1 >= count) break;

			var icon = Number(unit->AtkValues[at + 1]);

			// ⚠ Gaps are all-zero entries, and their row number is zero too — which would otherwise
			// read as a perfectly good "row 0". The icon is what says whether an entry is real.
			if (icon == 0) continue;

			rows.Add(((int)Number(unit->AtkValues[at]), icon));
		}

		return rows;
	}

	/// <summary>⚠ The game is inconsistent about Int versus UInt for the same field.</summary>
	private static uint Number(AtkValue v) => v.Type switch {
		AtkValueType.UInt => v.UInt,
		AtkValueType.Int => v.Int < 0 ? 0u : (uint)v.Int,
		_ => 0u,
	};
}
