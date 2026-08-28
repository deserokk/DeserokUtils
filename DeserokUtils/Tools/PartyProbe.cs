using System;

using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace DeserokUtils.Tools;

/// <summary>
/// ⚠⚠ THROWAWAY. Answers four questions about showing party jobs for members who are not in your
/// zone, then gets deleted along with <c>/dsu party</c>. Same rule the collection dump got.
///
/// The complaint: the party list shows <c>Lv??? Peachi Bunni</c> with a blank job slot when someone
/// is elsewhere, so a roulette starts with *"which job are you? are you tanking?"* in Discord. The
/// Social window shows the job perfectly well, so the client HAS it.
///
/// deserok's approach, and it is the safer one: do not write into the party list's nodes, just
/// *"overlay the job icon in the blank spot"*. A read that fails draws nothing; a write that fails
/// fights every other plugin that touches the party list.
///
/// ## What this has to find out before any of that gets built
///
/// 1. Does <see cref="InfoProxyPartyMember"/> hold the data WITHOUT opening the Social window? It has
///    a RequestData() and a HandleZoneInitPacket, so it may well be filled on demand -- and an
///    overlay reading a stale proxy would be confidently wrong, which is worse than blank.
/// 2. What icon id does the game use for a job? ⭐ Not guessed: <c>PartyClassJobIconId</c> reports it
///    per row, so an in-zone member teaches us the convention exactly.
/// 3. Does an out-of-zone row report icon id 0, giving a clean "this slot is blank" test?
/// 4. Are the node coordinates usable? <c>ScreenX/ScreenY</c> are pre-resolved, so if they look sane
///    there is no scale or HUD-layout maths to get wrong.
/// </summary>
internal static class PartyProbe {
	public static unsafe void Run() {
		Say("--- InfoProxyPartyMember (what the Social window reads) ---");
		var proxy = InfoProxyPartyMember.Instance();
		if (proxy is null) {
			Say("  proxy is null");
		}
		else {
			uint count = proxy->EntryCount;
			Say($"  EntryCount={count}");
			for (uint i = 0; i < count; i++) {
				var entry = proxy->GetEntry(i);
				if (entry is null) {
					Say($"  [{i}] null entry");
					continue;
				}
				Say($"  [{i}] \"{entry->NameString}\" job={entry->Job} location={entry->Location} "
					+ $"state={entry->State} world={entry->CurrentWorld}/{entry->HomeWorld}");
			}
		}

		Say("--- _PartyList rows ---");
		var unit = Plugin.GameGui.GetAddonByName("_PartyList");
		if (unit.IsNull) {
			Say("  _PartyList not loaded");
			return;
		}

		var addon = (AddonPartyList*)(nint)unit;
		Say($"  visible={unit.IsVisible} MemberCount={addon->MemberCount} TrustCount={addon->TrustCount} "
			+ $"RowHeight={addon->RowHeight}");

		var iconIds = addon->PartyClassJobIconId;
		var rows = addon->PartyMembers;

		for (int i = 0; i < addon->MemberCount && i < rows.Length; i++) {
			var row = rows[i];
			string name = row.Name is null ? "<no node>" : row.Name->NodeText.ExtractText();
			uint iconId = i < iconIds.Length ? iconIds[i] : 0;

			var icon = row.ClassJobIcon;
			string where = icon is null
				? "<no icon node>"
				: $"screen=({icon->AtkResNode.ScreenX:0},{icon->AtkResNode.ScreenY:0}) "
					+ $"size={icon->AtkResNode.Width}x{icon->AtkResNode.Height} "
					+ $"visible={icon->AtkResNode.IsVisible()}";

			Say($"  [{i}] \"{name}\" iconId={iconId} {where}");
		}
	}

	/// <summary>⚠ Plain chat, not Diag. The point is that he can read it without turning anything on.</summary>
	private static void Say(string line) => Plugin.Chat.Print(line);
}
