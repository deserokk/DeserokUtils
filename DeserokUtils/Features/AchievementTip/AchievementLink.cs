using System;
using System.Text;

using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace DeserokUtils.Features.AchievementTip;

/// <summary>
/// Reads the achievement out of a chat link.
///
/// ## ⭐⭐ The format, measured 2026-08-23 rather than looked up
///
/// Dalamud has no achievement payload class -- its <c>EmbeddedInfoType</c> lists PlayerName, Item,
/// Map, Quest, PartyFinder, Status and DalamudLink, and simply has no entry for this -- so the link
/// arrives as an untyped <see cref="RawPayload"/> and has to be decoded by hand.
///
/// <code>
/// 02 27 2F 06 F2 04 B3 01 01 FF 27 "Mapping the Realm: Dravanian Forelands" 03
/// │  │  │  │  └──┬──┘              └─ 0xFF = string follows, 0x27 = its length
/// │  │  │  │     └ 0xF2 = two-byte integer -> 0x04B3 = 1203 = the achievement ROW ID
/// │  │  │  └ link type 6 = achievement
/// │  │  └ chunk length
/// │  └ 0x27 = Interactable chunk
/// └ START_BYTE
/// </code>
///
/// ⭐ Verified end to end: 1203 is *Mapping the Realm: Dravanian Forelands*, which is the achievement
/// that produced the line it was read from.
///
/// ⚠ Link type 6 is <c>EmbeddedInfoType</c> numbering. The same link seen through
/// <c>LinkData.LinkType</c> reads 5, because that enum is offset by one -- confirmed across player
/// (1/0), item (3/2), achievement (6/5) and status (9/8).
/// </summary>
internal static class AchievementLink {
	private const byte StartByte = 0x02;
	private const byte Interactable = 0x27;
	private const byte AchievementType = 0x06;
	private const byte StringMarker = 0xFF;

	/// <summary>What a chat link turned out to be, when it was an achievement.</summary>
	public readonly record struct Parsed(uint Id, string Name);

	/// <summary>
	/// ⚠ Null for everything that is not an achievement link, which is almost everything. Items,
	/// players and statuses all arrive as typed payloads and never reach here.
	/// </summary>
	public static Parsed? Read(RawPayload payload) {
		byte[] d = payload.Data;
		if (d.Length < 6 || d[0] != StartByte || d[1] != Interactable)
			return null;

		// d[2] is the chunk length; d[3] is the link type.
		if (d[3] != AchievementType)
			return null;

		int at = 4;
		uint? id = ReadPackedInt(d, ref at);
		if (id is null || id == 0)
			return null;

		return new Parsed(id.Value, ReadEmbeddedName(d, at));
	}

	/// <summary>
	/// FFXIV's packed integer encoding.
	///
	/// ⚠⚠ ONLY THE FORMS THAT CAN CARRY AN ACHIEVEMENT ID ARE IMPLEMENTED, and anything else is
	/// reported rather than guessed. Ids run to about 4000, so a literal, one byte or two bytes covers
	/// every value that can occur -- the wider markers exist in the format but cannot appear here, and
	/// inventing a reading for them would be a decode that looks confident and is untested.
	/// </summary>
	private static uint? ReadPackedInt(byte[] d, ref int at) {
		if (at >= d.Length)
			return null;

		byte marker = d[at++];

		// ⚠ Values below 0xF0 are stored literally, biased by one. A raw zero cannot be encoded, which
		// is why the bias exists and why it must not be forgotten.
		if (marker < 0xF0)
			return (uint)(marker - 1);

		switch (marker) {
			case 0xF0 when at < d.Length:
				return d[at++];
			case 0xF1 when at < d.Length:
				return (uint)(d[at++] << 8);
			case 0xF2 when at + 1 < d.Length:
				return (uint)((d[at] << 8) | d[at + 1]);
			default:
				Plugin.Log.Warning(
					$"AchievementTip: unhandled packed-int marker 0x{marker:X2} in an achievement link. "
					+ "The id was not read; please report the chat line.");
				return null;
		}
	}

	/// <summary>
	/// The achievement name the game embedded in the link.
	///
	/// ⭐ Read purely so the id can be CHECKED against it. The name from the sheet and the name in the
	/// payload must agree, and if they do not the decode is wrong somewhere -- which is worth knowing
	/// loudly rather than showing somebody a confident description of the wrong achievement.
	///
	/// ⚠ Returns empty rather than throwing on anything unexpected. A missing cross-check is a reason
	/// to skip the check, not to lose the link.
	/// </summary>
	private static string ReadEmbeddedName(byte[] d, int at) {
		while (at < d.Length && d[at] != StringMarker)
			at++;

		if (at + 1 >= d.Length)
			return string.Empty;

		at++;

		// ⚠⚠ THE LENGTH IS A PACKED INTEGER TOO, so it carries the same +1 bias as the id and must be
		// decoded the same way -- 0x29 means 40 bytes, not 41. Reading it raw appended the chunk's
		// trailing 0x03 to the name, which then failed the cross-check against two strings that print
		// IDENTICALLY. A whole diagnostic round was spent on "these are the same string" because the
		// difference was an invisible control character.
		uint? length = ReadPackedInt(d, ref at);
		if (length is null or 0 || at + length > d.Length)
			return string.Empty;

		return Encoding.UTF8.GetString(d, at, (int)length.Value);
	}

	/// <summary>Every achievement link in a message, with the payload it came from.</summary>
	public static (RawPayload Payload, Parsed Achievement)? FindIn(SeString message) {
		foreach (var payload in message.Payloads) {
			if (payload is not RawPayload raw)
				continue;
			if (Read(raw) is { } found)
				return (raw, found);
		}

		return null;
	}
}
