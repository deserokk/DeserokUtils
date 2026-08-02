using System;
using System.Collections.Generic;

namespace DeserokUtils.Features.CastWatch;

/// <summary>
/// Which targets count as a hit.
///
/// ⚠ Only EXACT tests here, deliberately. "Friendly" and "hostile" inferred from ObjectKind put
/// pets, chocobos, friendly NPCs and event objects in a grey zone; identity with yourself and
/// membership in the party roster are id comparisons that cannot be wrong.
/// </summary>
internal enum TargetFilter {
	/// <summary>Any target at all. The default.</summary>
	Any,
	/// <summary>Only counts when it went to you.</summary>
	Self,
	/// <summary>Only counts when it went to anyone BUT you -- this is what catches a self-redirect.</summary>
	NotSelf,
	/// <summary>Only counts when it went to a party member other than you.</summary>
	Party,
}

/// <summary>
/// Who the targeting placeholders pointed at, captured the moment /watch ran.
///
/// ⚠ Why a snapshot and not a live read: a fallback macro resolves in well under a second, but by
/// the time it reaches a later line the mouse has moved. Reading &lt;mo&gt; at check time answers
/// "where is the cursor now", which is a different question from "who did the spell go to".
///
/// deserok's scoping, and it is the right one: a long cast-sequence macro is out of scope for this,
/// and should wrap the watch tightly around the one action it cares about instead.
/// </summary>
internal sealed class WatchContext {
	public ulong MouseOverId { get; init; }
	public string MouseOverName { get; init; } = string.Empty;

	public ulong TargetId { get; init; }
	public string TargetName { get; init; } = string.Empty;

	public ulong FocusId { get; init; }
	public string FocusName { get; init; } = string.Empty;

	/// <summary>Party slots as the game numbers them: index 1 is &lt;1&gt; (you), 2 is &lt;2&gt;.</summary>
	public IReadOnlyDictionary<int, (ulong Id, string Name)> Party { get; init; }
		= new Dictionary<int, (ulong, string)>();

	public static WatchContext Capture() {
		var party = new Dictionary<int, (ulong, string)>();
		for (int i = 0; i < Plugin.Party.Length; i++) {
			var member = Plugin.Party[i];
			if (member is null)
				continue;
			// <1> is the first slot, so game-facing numbering is 1-based.
			party[i + 1] = (member.GameObject?.GameObjectId ?? member.ContentId, member.Name.TextValue);
		}

		var mo = Plugin.Targets.MouseOverTarget;
		var tgt = Plugin.Targets.Target;
		var foc = Plugin.Targets.FocusTarget;

		return new WatchContext {
			MouseOverId = mo?.GameObjectId ?? 0,
			MouseOverName = mo?.Name.TextValue ?? string.Empty,
			TargetId = tgt?.GameObjectId ?? 0,
			TargetName = tgt?.Name.TextValue ?? string.Empty,
			FocusId = foc?.GameObjectId ?? 0,
			FocusName = foc?.Name.TextValue ?? string.Empty,
			Party = party,
		};
	}

	/// <summary>
	/// Which placeholder the given id corresponds to, as the macro would have written it. This is
	/// the thing a fallback macro cannot tell you on its own: whether it hit your mouseover or fell
	/// through to a party slot.
	/// </summary>
	public string Describe(ulong id) {
		if (id is 0 or 0xE0000000)
			return "none";

		List<string> hits = new();
		if (id == this.MouseOverId)
			hits.Add($"<mo> {this.MouseOverName}");
		if (id == this.TargetId)
			hits.Add($"<t> {this.TargetName}");
		if (id == this.FocusId)
			hits.Add($"<f> {this.FocusName}");
		foreach (var (slot, member) in this.Party) {
			if (id == member.Id)
				hits.Add($"<{slot}> {member.Name}");
		}

		// A single id legitimately matches several placeholders at once -- mousing over the person
		// who is also party slot 2 is normal. Report all of them rather than picking one and
		// implying the macro resolved through that route.
		return hits.Count > 0 ? string.Join(" = ", hits) : $"0x{id:X}";
	}

	/// <summary>
	/// Does this target satisfy the filter?
	///
	/// ⚠⚠ Evaluated per ATTEMPT, not once at the end. deserok's fallback macro tries &lt;mo&gt;
	/// seven times then &lt;2&gt; six times; if the mouseover is invalid the game may redirect the
	/// early attempts to you, and the later one reaches the healer. Judging only the first success
	/// would call that a self-cast and suppress a callout that should have gone out.
	/// </summary>
	public bool Passes(TargetFilter filter, ulong targetId, ulong selfId) {
		if (filter == TargetFilter.Any)
			return true;

		bool isSelf = targetId == selfId;
		bool hasTarget = targetId is not (0 or 0xE0000000);

		return filter switch {
			TargetFilter.Self => isSelf,
			TargetFilter.NotSelf => hasTarget && !isSelf,
			TargetFilter.Party => hasTarget && !isSelf && this.IsPartyMember(targetId),
			_ => true,
		};
	}

	private bool IsPartyMember(ulong id) {
		foreach (var (_, member) in this.Party) {
			if (member.Id == id)
				return true;
		}
		return false;
	}

	/// <summary>
	/// Just the name, for putting into a chat line. Describe() is for diagnostics and says too much
	/// ("&lt;mo&gt; Peachi Bunni = &lt;2&gt; Peachi Bunni"); this is what a person would type.
	/// </summary>
	public string NameOf(ulong id) {
		if (id is 0 or 0xE0000000)
			return string.Empty;

		if (id == this.MouseOverId && this.MouseOverName.Length > 0)
			return this.MouseOverName;
		if (id == this.TargetId && this.TargetName.Length > 0)
			return this.TargetName;
		if (id == this.FocusId && this.FocusName.Length > 0)
			return this.FocusName;
		foreach (var (_, member) in this.Party) {
			if (id == member.Id && member.Name.Length > 0)
				return member.Name;
		}

		// Not in the snapshot -- an out-of-party target picked after arming, most likely. The live
		// object table still knows them.
		foreach (var obj in Plugin.Objects) {
			if (obj.GameObjectId == id) {
				string name = obj.Name.TextValue;
				if (name.Length > 0)
					return name;
			}
		}

		return string.Empty;
	}

	public string Summary() {
		string party = this.Party.Count > 0
			? string.Join(", ", System.Linq.Enumerable.Select(this.Party, kv => $"<{kv.Key}>{kv.Value.Name}"))
			: "(no party)";
		return $"mo={(this.MouseOverName.Length > 0 ? this.MouseOverName : "none")}"
			+ $" t={(this.TargetName.Length > 0 ? this.TargetName : "none")}"
			+ $" | {party}";
	}
}
