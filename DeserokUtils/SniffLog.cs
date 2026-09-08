using System;
using System.IO;

namespace DeserokUtils;

internal static class SniffLog {
	public static string Path
		=> System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "sniff.log");

	public static void Write(string line) {
		try {
			File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
		}
		catch (Exception ex) {

			Plugin.Log.Error(ex, "Could not write to the sniff log.");
		}
	}

	public static void Mark(string what) {
		Write(string.Empty);
		Write($"=== {what} — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
	}
}
