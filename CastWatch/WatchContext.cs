using System;
using System.Collections.Generic;

namespace CastWatch;

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

	public string Summary() {
		string party = this.Party.Count > 0
			? string.Join(", ", System.Linq.Enumerable.Select(this.Party, kv => $"<{kv.Key}>{kv.Value.Name}"))
			: "(no party)";
		return $"mo={(this.MouseOverName.Length > 0 ? this.MouseOverName : "none")}"
			+ $" t={(this.TargetName.Length > 0 ? this.TargetName : "none")}"
			+ $" | {party}";
	}
}
