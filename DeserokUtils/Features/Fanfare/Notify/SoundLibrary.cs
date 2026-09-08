using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DeserokUtils.Features.Fanfare.Notify;

internal readonly record struct SoundEntry(string Name, string Path, bool Personal);

internal sealed class SoundLibrary {
	internal const string FolderName = "Sounds";

	internal IReadOnlyList<SoundEntry> Entries { get; private set; } = [];

	private readonly string pluginRoot;

	internal string PersonalFolder { get; }

	internal SoundLibrary() {
		this.pluginRoot = Path.GetDirectoryName(Plugin.PluginInterface.AssemblyLocation.FullName) ?? string.Empty;
		this.PersonalFolder = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, FolderName);

		try {
			Directory.CreateDirectory(this.PersonalFolder);
		} catch (Exception ex) {
			Plugin.Log.Error(ex, "sounds: could not create the personal folder");
		}

		this.Rescan();
	}

	internal void Rescan() {
		var found = new List<SoundEntry>();

		Scan(Path.Combine(this.pluginRoot, FolderName), bundled: true);
		Scan(this.PersonalFolder, bundled: false);

		this.Entries = found;
		Plugin.Log.Information(
			$"sounds: {found.Count(e => !e.Personal)} bundled, {found.Count(e => e.Personal)} personal.");

		void Scan(string directory, bool bundled) {
			try {
				if (!Directory.Exists(directory))
					return;

				foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f)) {
					var extension = Path.GetExtension(file).ToLowerInvariant();
					if (extension is not (".wav" or ".mp3" or ".aiff" or ".aif" or ".wma"))
						continue;

					var name = Prettify(Path.GetFileNameWithoutExtension(file));
					found.Add(bundled
						? new SoundEntry(name, $"{FolderName}/{Path.GetFileName(file)}", false)
						: new SoundEntry($"{name} (yours)", file, true));
				}
			} catch (Exception ex) {
				Plugin.Log.Error(ex, $"sounds: could not scan {directory}");
			}
		}
	}

	internal string Resolve(string path) {
		if (string.IsNullOrWhiteSpace(path))
			return string.Empty;
		return Path.IsPathRooted(path)
			? path
			: Path.Combine(this.pluginRoot, path.Replace('/', Path.DirectorySeparatorChar));
	}

	internal string DisplayName(string path) {
		if (string.IsNullOrWhiteSpace(path))
			return "None";

		foreach (var entry in this.Entries) {
			if (string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
				return entry.Name;
		}

		return $"Elsewhere: {Path.GetFileName(path)}";
	}

	internal bool IsKnown(string path)
		=> this.Entries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

	private static string Prettify(string fileName) {
		var spaced = new System.Text.StringBuilder(fileName.Length + 4);
		for (var i = 0; i < fileName.Length; i++) {
			var c = fileName[i];

			if (i > 0 && char.IsUpper(c) && !char.IsUpper(fileName[i - 1]) && fileName[i - 1] != ' ')
				spaced.Append(' ');

			spaced.Append(c == '-' || c == '_' ? ' ' : c);
		}

		var words = spaced.ToString().Trim();
		return words.Length == 0
			? fileName
			: CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words.ToLowerInvariant());
	}
}
