using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Dresser;

/// <summary>
/// Writes what a game window is actually holding into the dresser log.
///
/// ⭐⭐⭐ THIS EXISTS BECAUSE THE PACKER HAS BEEN DRIVING BLIND. Every button it presses was
/// recorded from a real click and replays correctly, but it has never once been able to READ the
/// dialog it is pressing buttons on — so "did the cogwheel open the right set?" and "is the entry I
/// am about to pick my item or a Remove option?" are both unanswerable, and both turned out to
/// matter. Three ghost outfits and a fistful of duplicates came out of guessing at them.
///
/// ⚠ Read-only. It fires nothing and changes nothing; it only says what is on screen.
///
/// ⭐ Under Verbose only, and once per thing rather than once per tick — a full dump of the
/// inventory list is several hundred lines.
/// </summary>
internal static unsafe class DresserProbe {
	/// <summary>⚠ Deep lists get long. Enough to cover a full bag, not enough to fill the disk.</summary>
	private const int MaxValues = 400;

	/// <summary>Every AtkValue the named window is carrying, typed, with strings extracted.</summary>
	public static void Values(string addonName) {
		if (!Plugin.Verbose) return;

		var addon = Plugin.GameGui.GetAddonByName(addonName, 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) {
			DresserLog.Step($"  probe {addonName}: not open");
			return;
		}

		var unit = (AtkUnitBase*)addon.Address;
		var count = unit->AtkValuesCount;

		DresserLog.Step($"  probe {addonName}: {count} value(s)");

		// ⚠ In chunks. One line of four hundred values is a line nobody can read and grep cannot
		// usefully cut up either.
		var sb = new StringBuilder();
		var shown = 0;
		for (var i = 0u; i < count && i < MaxValues; i++) {
			if (sb.Length > 0) sb.Append(", ");
			sb.Append($"{i}:");
			Append(sb, unit->AtkValues[i]);
			shown++;

			if (shown % 10 == 0) {
				DresserLog.Step($"        {sb}");
				sb.Clear();
			}
		}

		if (sb.Length > 0) DresserLog.Step($"        {sb}");
		if (count > MaxValues) DresserLog.Step($"        ...{count - MaxValues} more");
	}

	/// <summary>
	/// Every piece of text the window is displaying.
	///
	/// ⭐ The values array is the better answer when it carries item ids, but a window that keeps
	/// its list in a component rather than in values still SHOWS the names — and a name is enough to
	/// tell "this dialog is for the Rebel Set" from "this dialog is for something else entirely".
	/// </summary>
	public static void Text(string addonName) {
		if (!Plugin.Verbose) return;

		var addon = Plugin.GameGui.GetAddonByName(addonName, 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return;

		var unit = (AtkUnitBase*)addon.Address;
		DresserLog.Step($"  probe {addonName}: text");

		var found = 0;
		for (var i = 0; i < unit->UldManager.NodeListCount && found < 120; i++) {
			var node = unit->UldManager.NodeList[i];
			if (node is null) continue;
			found += Walk(node, 0);
		}
	}

	/// <summary>⚠ Depth-capped. A component tree can nest further than is ever interesting here.</summary>
	private static int Walk(AtkResNode* node, int depth) {
		if (node is null || depth > 6) return 0;

		var found = 0;

		if (node->Type == NodeType.Text) {
			var text = ((AtkTextNode*)node)->NodeText.ToString();
			if (!string.IsNullOrWhiteSpace(text)) {
				DresserLog.Step($"        [{node->NodeId}]{new string(' ', depth)} {text}");
				found++;
			}
		}

		if ((ushort)node->Type >= 1000) {
			// A component node: its children live in its own ULD manager, not in ChildNode.
			var component = ((AtkComponentNode*)node)->Component;
			if (component is not null) {
				for (var i = 0; i < component->UldManager.NodeListCount && found < 120; i++)
					found += Walk(component->UldManager.NodeList[i], depth + 1);
			}
		}

		for (var child = node->ChildNode; child is not null && found < 120; child = child->PrevSiblingNode)
			found += Walk(child, depth + 1);

		return found;
	}

	private static void Append(StringBuilder sb, AtkValue v) {
		switch (v.Type) {
			case AtkValueType.Int: sb.Append(v.Int); break;
			case AtkValueType.UInt: sb.Append(v.UInt); break;
			case AtkValueType.Bool: sb.Append(v.Bool); break;
			case AtkValueType.Float: sb.Append(v.Float); break;
			case AtkValueType.String:
			case AtkValueType.ConstString:
			case AtkValueType.ManagedString:
				sb.Append('"').Append(v.String.ToString()).Append('"');
				break;
			case AtkValueType.Undefined: sb.Append('-'); break;
			default: sb.Append(v.Int); break;
		}
	}
}
