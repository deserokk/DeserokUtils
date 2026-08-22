using System;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DeserokUtils.Features.DrawSheathe;

/// <summary>
/// Whether the character actually owns the emote a press would send.
///
/// ## ⚠⚠⚠ Found by Bunny, 2026-08-22, on the first day anyone else ran this
///
/// She owns **Draw Weapon** and not **Sheathe Weapon** -- they are two separate Gold Saucer rewards,
/// emote 238 and emote 237. Drawing worked; sheathing did nothing at all. Not the game's toggle, not
/// an error, nothing.
///
/// ⭐⭐ And this is a **new shape of blind spot**, distinct from the one already recorded in
/// ORIENTATION. That one was "needs a second player". This one is *needs a different account* -- the
/// feature was correct for every state deserok's character can be in, because his character owns
/// both halves and no amount of testing alone can reach the state where it does not. ⚠ The general
/// form is worth remembering: **anything gated on unlocks, progression or purchases is untestable
/// from one account**, and the failure is silent because the game simply refuses the command.
///
/// ⭐ The fix is not a new mechanism. <see cref="DrawSheatheFeature.EmoteRefusedBecause"/> already
/// exists to answer "should the game's own toggle happen here instead", and an emote you do not own
/// is the cleanest possible yes: you asked to sheathe, the fancy version is unavailable, so sheathe
/// normally. It is one more reason string in a predicate built to collect them.
/// </summary>
internal static class EmoteUnlock {
	/// <summary>
	/// Command -&gt; emote, from the Emote sheet's own TextCommand link.
	///
	/// ⚠ Read from the sheet rather than hardcoding 237 and 238, because the two commands are
	/// CONFIGURABLE -- someone can point this at any emote they like, and a hardcoded pair would
	/// check the wrong thing the moment they did. Same reason the emote list was dumped from the
	/// sheet rather than copied off a wiki.
	/// </summary>
	private static Dictionary<string, (ushort Id, string Name)>? byCommand;

	private static Dictionary<string, (ushort Id, string Name)> Map() {
		if (byCommand is not null)
			return byCommand;

		var map = new Dictionary<string, (ushort, string)>(StringComparer.OrdinalIgnoreCase);
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
		if (sheet is not null) {
			foreach (var row in sheet) {
				if (row.RowId > ushort.MaxValue)
					continue;
				string name = row.Name.ExtractText();
				if (name.Length == 0)
					continue;

				// ⚠ Both the command and its alias. 295 of the 334 emotes have a text command; the
				// rest are not typeable and cannot be what someone configured here.
				string command = "", alias = "";
				try {
					var tc = row.TextCommand.ValueNullable;
					if (tc is not null) {
						command = tc.Value.Command.ExtractText();
						alias = tc.Value.Alias.ExtractText();
					}
				}
				catch {
					// ⚠ A broken sheet link is not worth failing the whole map over -- that emote just
					// will not be checkable, which degrades to the old behaviour for it alone.
					continue;
				}

				foreach (string key in new[] { command, alias }) {
					if (key.Length > 0 && !map.ContainsKey(key))
						map[key] = ((ushort)row.RowId, name);
				}
			}
		}

		Plugin.Log.Information($"DrawSheathe: emote command map built, {map.Count} entries.");
		return byCommand = map;
	}

	/// <summary>
	/// Why this command cannot be played, or null if it can be -- or if we cannot tell.
	///
	/// ⚠⚠ NULL MEANS "GO AHEAD" IN THREE DIFFERENT CASES: the emote is owned, the command is not an
	/// emote we recognise, or UIState could not be read. All three keep the pre-existing behaviour,
	/// which is to send the command and let the game answer. ⭐ Refusing on a failed read would turn
	/// a momentary unreadable state into a key that does nothing, which is the exact symptom being
	/// fixed here.
	/// </summary>
	public static unsafe string? LockedBecause(string configuredCommand) {
		// "/sheathe motion" -> "/sheathe". The suffix is the emote's own argument, not part of its name.
		string line = configuredCommand.Trim();
		if (line.Length == 0)
			return null;
		int space = line.IndexOf(' ');
		string verb = space < 0 ? line : line[..space];

		if (!Map().TryGetValue(verb, out var emote))
			return null;

		var ui = UIState.Instance();
		if (ui is null)
			return null;

		return ui->IsEmoteUnlocked(emote.Id)
			? null
			: $"you do not have the {emote.Name} emote";
	}
}
