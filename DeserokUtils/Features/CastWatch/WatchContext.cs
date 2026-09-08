using System;
using System.Collections.Generic;

namespace DeserokUtils.Features.CastWatch;

internal enum TargetFilter {

	Any,

	Self,

	NotSelf,

	Party,
}

internal sealed class WatchContext {
	public ulong MouseOverId { get; init; }
	public string MouseOverName { get; init; } = string.Empty;

	public ulong TargetId { get; init; }
	public string TargetName { get; init; } = string.Empty;

	public ulong FocusId { get; init; }
	public string FocusName { get; init; } = string.Empty;

	public IReadOnlyDictionary<int, (ulong Id, string Name)> Party { get; init; }
		= new Dictionary<int, (ulong, string)>();

	public static WatchContext Capture() {
		var party = new Dictionary<int, (ulong, string)>();
		for (int i = 0; i < Plugin.Party.Length; i++) {
			var member = Plugin.Party[i];
			if (member is null)
				continue;

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

		return hits.Count > 0 ? string.Join(" = ", hits) : $"0x{id:X}";
	}

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
