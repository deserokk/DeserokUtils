using System;
using System.IO;

namespace DeserokUtils;

/// <summary>
/// Where recordings go, which is deliberately not Dalamud's log.
///
/// ⭐⭐⭐ BECAUSE THE ROLLING LOG ATE ONE. The outfit-unpack chain was recorded on 2026-09-03,
/// written to dalamud.log, and by 2026-09-05 both that file and its .old sibling had rotated past
/// it — a payload captured once, from a real click, gone. It survived only because a session
/// happened to have read it into memory that same day.
///
/// deserok, 2026-09-05: *"we should not use dalamud's log, our sniffers usually output to a
/// different file for that reason."* The Dresser already does this and it is why every packing
/// failure this week was diagnosable. A recording is the most expensive kind of data here — somebody
/// has to stand in a specific place and do a specific thing by hand to produce one — and the
/// cheapest possible mistake is putting it somewhere that throws it away.
///
/// ⚠ Appends, never truncates. Two recordings of the same action taken an hour apart are a
/// comparison, and a comparison is how a guess becomes a fact.
/// </summary>
internal static class SniffLog {
	public static string Path
		=> System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "sniff.log");

	public static void Write(string line) {
		try {
			File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
		}
		catch (Exception ex) {
			// ⚠ Never take the game with a diagnostic. This runs inside a UI callback hook.
			Plugin.Log.Error(ex, "Could not write to the sniff log.");
		}
	}

	/// <summary>A banner, so a run can be told from the one before it at a glance.</summary>
	public static void Mark(string what) {
		Write(string.Empty);
		Write($"=== {what} — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
	}
}
