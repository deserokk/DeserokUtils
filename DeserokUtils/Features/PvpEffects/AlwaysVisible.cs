using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DeserokUtils.Features.PvpEffects;

internal static class TmbPatch {
	private const string Magic = "C012";

	private const int EntrySize = 0x48;
	private const int VisibilityAt = 0x40;

	private const byte Always = 3;

	internal static (byte[] Bytes, int Changed)? Patch(byte[] original) {
		var bytes = (byte[])original.Clone();
		var magic = Encoding.ASCII.GetBytes(Magic);
		var changed = 0;

		for (var i = 0; i + EntrySize <= bytes.Length; i++) {
			if (bytes[i] != magic[0] || bytes[i + 1] != magic[1] || bytes[i + 2] != magic[2] || bytes[i + 3] != magic[3])
				continue;

			var size = BitConverter.ToInt32(bytes, i + 4);
			if (size != EntrySize) continue;

			if (bytes[i + VisibilityAt] == Always) continue;

			bytes[i + VisibilityAt] = Always;
			changed++;
		}

		return changed == 0 ? null : (bytes, changed);
	}

	internal static string GamePath(string animationKey) => $"chara/action/{animationKey}.tmb";

	internal static string Folder
		=> Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "pvpvisible");

	internal static string DiskPath(string animationKey)
		=> Path.Combine(Folder, animationKey.Replace('/', '_') + ".tmb");

	internal static (string Game, string Disk)? Prepare(string animationKey) {
		try {
			var game = GamePath(animationKey);
			var file = Plugin.Data.GetFile(game);
			if (file is null) {
				Plugin.Log.Warning($"PvP visibility: the game has no {game}.");
				return null;
			}

			var patched = Patch(file.Data);
			if (patched is not { } result) return null;

			Directory.CreateDirectory(Folder);
			var disk = DiskPath(animationKey);

			if (File.Exists(disk) && File.ReadAllBytes(disk).AsSpan().SequenceEqual(result.Bytes))
				return (game, disk);

			try {
				File.WriteAllBytes(disk, result.Bytes);
			}
			catch (IOException) when (File.Exists(disk)) {

				Plugin.Log.Debug($"PvP visibility: {disk} is in use, keeping the copy already there.");
			}

			return (game, disk);
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, $"PvP visibility: could not prepare {animationKey}.");
			return null;
		}
	}
}
