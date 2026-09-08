using System;
using System.Collections.Generic;

using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DeserokUtils.Features.MacroIcons;

internal sealed unsafe class MacroIconFeature: IDisposable {
	public string TabTitle => "Macro icons";

	public string Summary => "Gives a macro the icon of the item it uses, or of whatever its name says it does.";

	private delegate bool TryResolveMacroIconDelegate(
		RaptureMacroModule* module,
		UIModule* uiModule,
		RaptureHotbarModule.HotbarSlotType* outType,
		uint* outRowId,
		int setId,
		uint macroId,
		uint* outItemId);

	private readonly Hook<TryResolveMacroIconDelegate>? hook;

	private const RaptureHotbarModule.HotbarSlotType ItemSlot = RaptureHotbarModule.HotbarSlotType.Item;

	private const uint DefaultMacroIcon = 66001;

	private readonly Dictionary<(int Set, uint Id), (string Signature, RaptureHotbarModule.HotbarSlotType Type, uint Row)> cache = new();

	private int supplied;
	private string lastSupplied = "none yet";

	public MacroIconFeature() {
		nint addr = RaptureMacroModule.Addresses.TryResolveMacroIcon.Value;
		if (addr == 0) {
			Plugin.Log.Error("MacroIcons: could not resolve TryResolveMacroIcon. Item icons will not work.");
			return;
		}

		this.hook = Plugin.Interop.HookFromAddress<TryResolveMacroIconDelegate>(addr, this.Detour);
		this.hook.Enable();
		Plugin.Log.Information($"MacroIcons: hooked TryResolveMacroIcon at 0x{addr:X}");
	}

	private bool Detour(
		RaptureMacroModule* module,
		UIModule* uiModule,
		RaptureHotbarModule.HotbarSlotType* outType,
		uint* outRowId,
		int setId,
		uint macroId,
		uint* outItemId) {

		bool resolved = this.hook!.Original(module, uiModule, outType, outRowId, setId, macroId, outItemId);

		if (resolved)
			return true;

		try {

			if (module is null || outType is null || outRowId is null)
				return false;
			if (!Plugin.Config.MacroItemIcons && !Plugin.Config.MacroNameIcons)
				return false;

			var answer = this.Resolve(module, setId, macroId);
			if (answer is null)
				return false;

			*outType = answer.Value.Type;
			*outRowId = answer.Value.Row;

			if (outItemId is not null)
				*outItemId = answer.Value.Type == ItemSlot ? answer.Value.Row : 0;

			return true;
		}
		catch (Exception ex) {

			Plugin.Log.Error(ex, "MacroIcons: exception in TryResolveMacroIcon detour");
			return false;
		}
	}

	private (RaptureHotbarModule.HotbarSlotType Type, uint Row)? Resolve(RaptureMacroModule* module, int setId, uint macroId) {
		var macro = module->GetMacro((uint)setId, macroId);
		if (macro is null)
			return null;

		string name = macro->Name.ToString();
		string first = macro->Lines.Length > 0 ? macro->Lines[0].ToString() : string.Empty;
		string signature = $"{name}|{first}|{macro->IconId}";

		if (this.cache.TryGetValue((setId, macroId), out var cached) && cached.Signature == signature)
			return cached.Type == RaptureHotbarModule.HotbarSlotType.Empty ? null : (cached.Type, cached.Row);

		var answer = this.Compute(macro, name, setId, macroId);
		this.cache[(setId, macroId)] = (signature,
			answer?.Type ?? RaptureHotbarModule.HotbarSlotType.Empty, answer?.Row ?? 0);
		return answer;
	}

	private (RaptureHotbarModule.HotbarSlotType Type, uint Row)? Compute(
		RaptureMacroModule.Macro* macro, string name, int setId, uint macroId) {

		var lines = macro->Lines;
		for (int i = 0; i < lines.Length; i++) {
			string line = lines[i].ToString().Trim();
			if (line.Length == 0)
				continue;

			(string Icon, string? Category)? micon = ParseMicon(line);
			if (micon is null)
				continue;

			if (micon.Value.Category is not null
				&& !micon.Value.Category.Equals("item", StringComparison.OrdinalIgnoreCase))
				return null;

			if (!Plugin.Config.MacroItemIcons)
				return null;

			uint? item = ItemUse.ItemLookup.Resolve(micon.Value.Icon);
			if (item is null) {
				Plugin.Log.Information($"MacroIcons: macro {setId}/{macroId} names \"{micon.Value.Icon}\" "
					+ "in /micon, which is neither an action the game found nor a usable item.");
				return null;
			}

			string label = name.Length > 0 ? name : $"macro {macroId}";
			this.Note($"{label} -> item icon for \"{micon.Value.Icon}\"");
			return (ItemSlot, item.Value);
		}

		if (!Plugin.Config.MacroNameIcons || name.Length == 0)
			return null;

		if (macro->IconId != 0 && macro->IconId != DefaultMacroIcon) {
			Plugin.Diag($"MacroIcons: macro {setId}/{macroId} \"{name}\" has a chosen icon "
				+ $"(IconId {macro->IconId}, MacroIconRowId {macro->MacroIconRowId}); left alone.");
			return null;
		}

		if (ItemUse.ItemLookup.Resolve(name) is uint namedItem) {
			this.Note($"\"{name}\" -> item icon, from the macro name");
			return (ItemSlot, namedItem);
		}

		if (IfMouseover.ActionLookup.Resolve(name, IfMouseover.ActionLookup.InPvp) is { } action) {
			this.Note($"\"{name}\" -> action icon, from the macro name");
			return (RaptureHotbarModule.HotbarSlotType.Action, action.Id);
		}

		string near = ItemUse.ItemLookup.Suggest(name)
			?? IfMouseover.ActionLookup.Suggest(name)
			?? string.Empty;
		Plugin.Diag($"MacroIcons: macro {setId}/{macroId} \"{name}\" -- no /micon, IconId {macro->IconId}, "
			+ $"and the name matches no action or usable item."
			+ (near.Length > 0 ? $" Closest is \"{near}\"." : string.Empty));
		return null;
	}

	private void Note(string what) {
		this.supplied++;
		this.lastSupplied = what;
		Plugin.Diag($"MacroIcons: {what}");
	}

	private static (string Icon, string? Category)? ParseMicon(string line) {
		string[] verbs = { "/macroicon ", "/micon " };
		foreach (string verb in verbs) {
			if (!line.StartsWith(verb, StringComparison.OrdinalIgnoreCase))
				continue;

			string rest = line[verb.Length..].Trim();
			if (rest.Length == 0)
				return null;

			if (rest[0] is '"' or '\'') {
				char quote = rest[0];
				int close = rest.IndexOf(quote, 1);
				if (close <= 1)
					return null;
				string category = rest[(close + 1)..].Trim();
				return (rest[1..close], category.Length > 0 ? category : null);
			}

			int space = rest.IndexOf(' ');
			return space < 0 ? (rest, null) : (rest[..space], rest[(space + 1)..].Trim());
		}

		return null;
	}

	public void DrawTab() {
		bool items = Plugin.Config.MacroItemIcons;
		if (ImGui.Checkbox("Let /micon name an item", ref items)) {
			Plugin.Config.MacroItemIcons = items;
			Plugin.Config.Save();
			this.cache.Clear();
		}
		ImGui.TextDisabled("    /micon \"Phoenix Down\"  or  /micon \"Phoenix Down\" item");

		bool names = Plugin.Config.MacroNameIcons;
		if (ImGui.Checkbox("Use the macro's name when it has no /micon", ref names)) {
			Plugin.Config.MacroNameIcons = names;
			Plugin.Config.Save();
			this.cache.Clear();
		}
		ImGui.TextDisabled("    a macro called \"Phoenix Down\" gets that icon with no line at all");

	}

	public void DrawDiagnostics() {
		ImGui.TextDisabled($"icons supplied this session: {this.supplied}");
		ImGui.TextDisabled($"last: {this.lastSupplied}");
	}

	public void Dispose() {
		this.hook?.Disable();
		this.hook?.Dispose();
	}
}
