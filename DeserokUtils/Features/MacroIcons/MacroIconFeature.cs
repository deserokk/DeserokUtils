using System;
using System.Collections.Generic;

using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DeserokUtils.Features.MacroIcons;

/// <summary>
/// Give a macro the icon of the item it uses, and -- when it has no <c>/micon</c> at all -- the icon
/// of whatever its NAME says it does.
///
/// ## ⚠⚠ /micon has no `item` category. Measured, not assumed.
///
/// From the game's own TextCommand sheet, 2026-09-02: <c>/micon "icon name" [category]</c> lists
/// fourteen categories -- action, blueaction, pvpaction, general, emote, companion, pet, minion,
/// mount, enemysign, waymark, gearset, classjob, quickchat -- and **item is not one of them**, with
/// the category defaulting to action. So <c>/micon "Phoenix Down"</c> looks for an ACTION by that
/// name, finds none, and the macro keeps the blank M. That is the second half of the same gap as
/// ItemUseFeature: the game will put an item on a hotbar, but not into a macro.
///
/// ⭐⭐ THE FIX IS NOT TO WRITE THE ICON INTO THE MACRO. Macro data is a saved user file that syncs
/// to the server, and nothing here has any business editing it to change a picture. Instead this
/// hooks the function the game already calls to work the icon out -- <c>TryResolveMacroIcon</c> --
/// and answers only when the game has already said "no icon". Its signature is the giveaway that
/// this is the right door: it hands back a <c>HotbarSlotType</c> and an <b>outItemId</b>, so the
/// consumer of the answer can already draw an item. Only the resolver refuses to produce one.
///
/// ⚠ Same shape as ResolvePlaceholder over an enumeration of hover types, one more time: when the
/// game already computes the answer, extend that call rather than re-derive it somewhere else.
///
/// ## ⭐ The name fallback is deserok's idea and it costs a macro line
///
/// *"Is the macro name the same as a spell or item name? use that icon automatically."* A macro
/// called "Phoenix Down" that has no /micon and no hand-picked icon gets the Phoenix Down icon, and
/// nobody types anything.
///
/// ⚠ Gated on <c>IconId == 0</c>, which is the hand-picked icon. Overriding a picture somebody chose
/// on purpose would be a worse failure than the blank one this replaces -- and the diagnostic prints
/// IconId on every miss, so if that guard turns out to read the wrong field it says so in the log
/// rather than quietly repainting a macro book.
/// </summary>
internal sealed unsafe class MacroIconFeature: IDisposable {
	public string TabTitle => "Macro icons";

	private delegate bool TryResolveMacroIconDelegate(
		RaptureMacroModule* module,
		UIModule* uiModule,
		RaptureHotbarModule.HotbarSlotType* outType,
		uint* outRowId,
		int setId,
		uint macroId,
		uint* outItemId);

	private readonly Hook<TryResolveMacroIconDelegate>? hook;

	/// <summary>
	/// Which slot type an item icon is announced as.
	///
	/// ⚠⚠ NAMED RATHER THAN INLINED because it is the one value here that is a reasonable guess rather
	/// than a measurement. The enum offers both <c>Item</c> (2) and <c>InventoryItem</c> (3) and
	/// nothing says which one the icon drawer wants. If item icons come out blank while the log says
	/// an icon was supplied, this constant is the thing to change -- and it is the only thing.
	/// </summary>
	private const RaptureHotbarModule.HotbarSlotType ItemSlot = RaptureHotbarModule.HotbarSlotType.Item;

	/// <summary>What we last answered for each macro, keyed by set and slot.
	///
	/// ⚠ Cached because this runs from the UI, not from a keypress: the same question is asked again
	/// every time a hotbar or the macro list refreshes, and two sheet lookups and a line walk per
	/// macro per refresh is the kind of cost that never shows up as anything but a vague stutter.
	///
	/// ⚠ Bounded by construction -- 100 individual plus 100 shared macros is the whole key space.
	/// </summary>
	private readonly Dictionary<(int Set, uint Id), (string Signature, RaptureHotbarModule.HotbarSlotType Type, uint Row)> cache = new();

	/// <summary>Counted for the tab: how many macros we have supplied an icon for this session.</summary>
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

		// ⭐ The game's answer always wins. We are only ever filling in the case where it had none,
		// so a macro whose /micon already works cannot be changed by anything below.
		if (resolved)
			return true;

		try {
			// ⚠ The out pointers are guarded even though the game surely passes real ones. A detour that
			// writes through a null takes the client with it, and this one runs on every icon refresh --
			// the cost of being wrong here is not a broken icon, it is a crash to desktop.
			if (module is null || outType is null || outRowId is null)
				return false;
			if (!Plugin.Config.MacroItemIcons && !Plugin.Config.MacroNameIcons)
				return false;

			var answer = this.Resolve(module, setId, macroId);
			if (answer is null)
				return false;

			*outType = answer.Value.Type;
			*outRowId = answer.Value.Row;
			// ⚠ Both written. Which of the two the caller reads is not documented and the signature
			// offers both; setting only one would work until it did not, on whichever screen reads the
			// other. An action answer clears the item id rather than leaving whatever was there.
			if (outItemId is not null)
				*outItemId = answer.Value.Type == ItemSlot ? answer.Value.Row : 0;

			return true;
		}
		catch (Exception ex) {
			// An exception out of a detour takes the game with it. Catch, and therefore also log, or a
			// broken resolver is indistinguishable from a silent one.
			Plugin.Log.Error(ex, "MacroIcons: exception in TryResolveMacroIcon detour");
			return false;
		}
	}

	/// <summary>The cached answer for one macro, recomputed when its name or lines have changed.</summary>
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

	/// <summary>
	/// Work out what icon this macro should have, given the game already declined to.
	///
	/// ⚠ The /micon line is searched for rather than assumed to be line one. It usually is, and the
	/// game itself only honours the first instance -- but "usually" is not a parser.
	/// </summary>
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

			// ⚠ An EXPLICIT category that is not "item" is left alone. If somebody wrote
			// `/micon "X" action` and the game could not find action X, quietly handing back an item
			// called X answers a question they did not ask.
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

		// No /micon at all: fall back to the macro's own name, if it was never given a picked icon.
		if (!Plugin.Config.MacroNameIcons || name.Length == 0)
			return null;

		if (macro->IconId != 0) {
			Plugin.Diag($"MacroIcons: macro {setId}/{macroId} \"{name}\" has a chosen icon "
				+ $"(IconId {macro->IconId}); left alone.");
			return null;
		}

		// ⚠ Items before actions, because an item name that is ALSO an action name is vanishingly
		// rare and a macro named after an item is the case this was asked for. CastWatch reports the
		// collision instead of choosing; here the wrong answer is a picture, so choosing is fine.
		if (ItemUse.ItemLookup.Resolve(name) is uint namedItem) {
			this.Note($"\"{name}\" -> item icon, from the macro name");
			return (ItemSlot, namedItem);
		}

		// ⭐ Reusing IfMouseover's action map rather than walking the Action sheet a third time. It is
		// a pure name lookup with no feature state in it, and a third copy of that walk is a real cost
		// here -- this runs from the UI, where CastWatch's per-/watch walk would be felt.
		if (IfMouseover.ActionLookup.Resolve(name, IfMouseover.ActionLookup.InPvp) is { } action) {
			this.Note($"\"{name}\" -> action icon, from the macro name");
			return (RaptureHotbarModule.HotbarSlotType.Action, action.Id);
		}

		return null;
	}

	private void Note(string what) {
		this.supplied++;
		this.lastSupplied = what;
		Plugin.Diag($"MacroIcons: {what}");
	}

	/// <summary>
	/// Read <c>/micon "Phoenix Down" item</c> into a name and an optional category.
	///
	/// ⚠ Both the quoted and unquoted forms, since /micon accepts both and people write both.
	/// </summary>
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

			// Unquoted: the game reads a single word as the name, and anything after it as the
			// category. A multi-word unquoted name is not something /micon accepts either.
			int space = rest.IndexOf(' ');
			return space < 0 ? (rest, null) : (rest[..space], rest[(space + 1)..].Trim());
		}

		return null;
	}

	// ── the tab ──────────────────────────────────────────────────────────────────────────────

	public void DrawTab() {
		ImGui.TextWrapped(
			"/micon has no \"item\" category -- the game lists fourteen and item is not among them -- "
			+ "so a macro that uses a Phoenix Down keeps the blank M. This fills that in.");
		ImGui.Spacing();

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

		ImGui.Spacing();
		ImGui.TextWrapped(
			"Nothing is written into your macros. The game is asked first and its answer always wins; "
			+ "this only fills in the case where it had no icon to give. A macro with an icon you "
			+ "picked by hand is left alone.");

		ImGui.Spacing();
		ImGui.TextDisabled($"icons supplied this session: {this.supplied}");
		ImGui.TextDisabled($"last: {this.lastSupplied}");
	}

	public void Dispose() {
		this.hook?.Disable();
		this.hook?.Dispose();
	}
}
