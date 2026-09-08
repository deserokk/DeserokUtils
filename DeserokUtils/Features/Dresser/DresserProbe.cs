using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Dresser;

internal static unsafe class DresserProbe {

	private static bool armedForTooltip;

	public static void Glyphs(int from, int count) {
		var line = new System.Text.StringBuilder();

		for (var i = 0; i < count; i++) {
			var code = from + i;
			if (code is < 0xE000 or > 0xF8FF) continue;

			line.Append($"{code:X4}:{(char)code}  ");

			if ((i + 1) % 16 != 0) continue;

			Plugin.Chat.Print(line.ToString());
			line.Clear();
		}

		if (line.Length > 0) Plugin.Chat.Print(line.ToString());
	}

	public static void Colours(int from, int count) {
		for (var row = from; row < from + count; row++) {
			var sample = new Dalamud.Game.Text.SeStringHandling.SeString();
			sample.Payloads.Add(
				new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload($"{row,4}: "));
			sample.Payloads.Add(
				new Dalamud.Game.Text.SeStringHandling.Payloads.UIForegroundPayload((ushort)row));
			sample.Payloads.Add(
				new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(
					"✓ You have this — loose in your glamour dresser"));
			sample.Payloads.Add(
				new Dalamud.Game.Text.SeStringHandling.Payloads.UIForegroundPayload(0));

			Plugin.Chat.Print(sample);
		}
	}

	public static void Visible(string why) {
		DresserLog.Step($"  visible {why}:");

		var stage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance();
		if (stage is null || stage->RaptureAtkUnitManager is null) return;

		var units = &stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
		for (var i = 0; i < units->Count; i++) {
			var unit = units->Entries[i].Value;
			if (unit is null || !unit->IsVisible) continue;

			var name = unit->NameString;
			if (!string.IsNullOrEmpty(name)) DresserLog.Step($"        {name}");
		}
	}

	public static void ArmTooltipDump() {
		if (armedForTooltip) return;

		armedForTooltip = true;
		Plugin.AddonLifecycle.RegisterListener(
			Dalamud.Game.Addon.Lifecycle.AddonEvent.PostRefresh, "ItemDetail", OnTooltip);

		Plugin.Chat.Print("Dresser: hover any item once. The tooltip will be written to the log.");
	}

	private static void OnTooltip(
		Dalamud.Game.Addon.Lifecycle.AddonEvent type,
		Dalamud.Game.Addon.Lifecycle.AddonArgTypes.AddonArgs args) {
		Plugin.AddonLifecycle.UnregisterListener(
			Dalamud.Game.Addon.Lifecycle.AddonEvent.PostRefresh, "ItemDetail", OnTooltip);

		var item = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentItemDetail.Instance();
		DresserLog.Step($"  probe ItemDetail: agent item {(item is null ? 0 : item->ItemId)}");

		var unit = (AtkUnitBase*)args.Addon.Address;
		Values(unit, "ItemDetail");
		Nodes(unit, "ItemDetail");

		armedForTooltip = false;

		Plugin.Chat.Print("Dresser: tooltip written to the log.");
	}

	private const int MaxValues = 400;

	public static void Values(string addonName) {
		if (!Plugin.Verbose && !armedForTooltip) return;

		var addon = Plugin.GameGui.GetAddonByName(addonName, 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) {
			DresserLog.Step($"  probe {addonName}: not open");
			return;
		}

		Values((AtkUnitBase*)addon.Address, addonName);
	}

	public static void Values(AtkUnitBase* unit, string addonName) {
		if (!Plugin.Verbose && !armedForTooltip) return;
		if (unit is null) { DresserLog.Step($"  probe {addonName}: null addon"); return; }

		var count = unit->AtkValuesCount;

		DresserLog.Step($"  probe {addonName}: {count} value(s)");

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

	public static void Text(string addonName) {
		if (!Plugin.Verbose && !armedForTooltip) return;

		var addon = Plugin.GameGui.GetAddonByName(addonName, 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return;

		Text((AtkUnitBase*)addon.Address, addonName);
	}

	public static void Text(AtkUnitBase* unit, string addonName) {
		if (!Plugin.Verbose && !armedForTooltip) return;
		if (unit is null) return;

		DresserLog.Step($"  probe {addonName}: text");

		var found = 0;
		for (var i = 0; i < unit->UldManager.NodeListCount && found < 120; i++) {
			var node = unit->UldManager.NodeList[i];
			if (node is null) continue;
			found += Walk(node, 0);
		}
	}

	public static void Nodes(AtkUnitBase* unit, string addonName) {
		if (!Plugin.Verbose && !armedForTooltip) return;
		if (unit is null) return;

		DresserLog.Step($"  probe {addonName}: nodes");

		for (var i = 0; i < unit->UldManager.NodeListCount; i++)
			WalkNode(unit->UldManager.NodeList[i], 0);
	}

	private static void WalkNode(AtkResNode* node, int depth) {
		if (node is null || depth > 8) return;

		var kind = node->Type.ToString();
		var seen = node->IsVisible() ? "shown" : "hidden";
		var pad = new string(' ', depth * 2);

		var extra = string.Empty;
		if (node->Type == NodeType.Text) {
			var text = ((AtkTextNode*)node)->NodeText.ToString();
			if (!string.IsNullOrWhiteSpace(text)) extra = $" \"{text}\"";
		}

		DresserLog.Step($"        {pad}[{node->NodeId}] {kind} {seen}{extra}");

		if ((ushort)node->Type >= 1000) {
			var component = ((AtkComponentNode*)node)->Component;
			if (component is not null) {
				for (var i = 0; i < component->UldManager.NodeListCount; i++)
					WalkNode(component->UldManager.NodeList[i], depth + 1);
			}
		}

		for (var child = node->ChildNode; child is not null; child = child->PrevSiblingNode)
			WalkNode(child, depth + 1);
	}

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
