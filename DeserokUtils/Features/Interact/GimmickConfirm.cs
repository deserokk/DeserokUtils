using System;
using System.Collections.Generic;
using System.Text;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.UI;

namespace DeserokUtils.Features.Interact;

/// <summary>
/// Answers the yes/no box that the interact key just caused, and nothing else.
///
/// ## Why this exists
///
/// The interact key was built and tested on deserok's client, where YesAlready was already running
/// and silently swallowing every dungeon gimmick prompt. On a vanilla client the same key press
/// stops at a confirmation box you have to click -- which is exactly the mouse trip to a menu the
/// feature was built to avoid. Bunny found this: *"on my system I can simply pick up the items, on
/// hers, she gets a confirmation box to click through"*. So the box was load-bearing all along, and
/// nobody noticed because one machine was hiding it.
///
/// ## ⭐⭐ The gate is game sheets, not a list somebody curated
///
/// Credit where it is due: YesAlready found this, and it is the whole trick. The game ships a sheet
/// literally called <c>GimmickYesNo</c> -- 171 rows, 161 with text, three columns (Message, and the
/// labels for its two buttons). It is Square Enix's own register of "this yes/no box is a dungeon
/// gimmick": row 13 is *"Pick up the key?"*, row 18 *"Use a tiny key to unlock the door?"*, row 102
/// *"Open the treasure coffer?"*.
///
/// That is why *"Discard [item]?"* still asks you. It is not held back by an exception we wrote and
/// have to maintain -- it is simply **not in the sheet**. An allowlist we curated would rot; this
/// one is patched by the people who add the gimmicks.
///
/// ## ⚠⚠ The second sheet can quote you a price, and that is a deliberate, narrow exception
///
/// <c>Warp.Question</c> covers area transitions, and 47 of its 544 rows are paid transport: *"Travel
/// to Gridania for 120 gil?"*. There is no cost column to filter on -- the two ferry rows carry a
/// price with the same flags as the free ones, so the only signal is the digits in the text. Two
/// filters were considered and both rejected: matching the word "gil" breaks in any other client
/// language, and dropping every prompt containing a digit would also drop *"Proceed to zone 1?"* and
/// *"Proceed to A-4 Research?"*, which are exactly the field-op transitions this key is for.
///
/// So paid transport IS auto-confirmed, at 40 to 120 gil, and it is written down here rather than
/// discovered later. It is the one place this feature spends money, it is bounded by the fact that
/// you walked to a ferryman and pressed interact, and it is reversed by unticking the box.
///
/// ⚠ Their implementation could not be copied even if we wanted to. The YesAlready repository has
/// no LICENSE file, which makes it all-rights-reserved by default, and its
/// <c>YesButton->Click()</c> comes from ECommons rather than FFXIVClientStructs -- our
/// AtkComponentButton has no Click at all. What is borrowed here is the *name of a sheet*, which is
/// a fact about the game.
///
/// ## ⭐ Two gates, and they are independent
///
/// A box is answered only when BOTH hold:
///
///   1. <see cref="Arm"/> ran within <see cref="Window"/> -- so *this plugin's key* caused it. A box
///      you opened by any other means is left alone, including the same box opened with Confirm.
///   2. The prompt text is in the sheet.
///
/// Either one alone would be wrong. Gate 1 alone would answer a trade request that happened to
/// arrive two seconds after a lever. Gate 2 alone is YesAlready, which is a fine plugin but is not
/// what this key is.
///
/// ## ⚠ The exit prompts are deliberately NOT carved out
///
/// The sheet includes *"Leave duty?"* (8), *"Record progress and leave the area?"* (103-105, the
/// Deep Dungeon one), *"Leave the Battlehall?"* (110) and *"Trigger the trap?"* (42). Removing them
/// was offered and deserok declined, with a reason that holds: *"it's an intentional choice to click
/// the interact button at the exit, the loot is never like, overlapping it or anything"*. Gate 1
/// means you already pressed the key, and gate 1 plus a six-yalm scan cannot reach an exit you were
/// not standing at. A carve-out would be a hardcoded row list drifting away from a sheet that gets
/// patched -- worse than the risk it removes.
/// </summary>
internal sealed class GimmickConfirm: IDisposable {
	private const string Addon = "SelectYesno";

	/// <summary>
	/// How long after a press a box still counts as ours.
	///
	/// ⚠ Generous on purpose, and safe to be: gate 2 does the real work. The prompt arrives within a
	/// frame or two in practice, so this is slack for a stutter rather than a real window.
	/// </summary>
	private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

	/// <summary>How long to keep re-checking for an addon that has not finished loading.</summary>
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(1);

	private DateTime armedUntil = DateTime.MinValue;
	private DateTime pendingSince = DateTime.MinValue;
	private HashSet<string>? prompts;

	/// <summary>For the tab. Not read by anything that decides.</summary>
	public string LastAnswer { get; private set; } = "nothing yet";

	/// <summary>For the tab: how many prompts the sheet gave us, so a failed load is visible.</summary>
	public int KnownPrompts => this.Prompts.Count;

	public GimmickConfirm() =>
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, Addon, this.OnSetup);

	/// <summary>Called by <see cref="InteractFeature"/> the moment it operates something.</summary>
	public void Arm() => this.armedUntil = DateTime.UtcNow + Window;

	/// <summary>
	/// ⚠⚠ Records that a box appeared; does NOT answer it here.
	///
	/// Answering inside PostSetup is what YesAlready does and it is fine for them, because they are
	/// the only listener that matters to them. deserok runs both plugins. Dalamud calls every
	/// registered listener for the event in turn, so if YesAlready clicks Yes first the addon starts
	/// closing and <c>args.Addon</c> is a pointer to memory the game is finished with -- and we would
	/// then read a text node out of it. Deferring to the next frame and looking the addon up again
	/// costs one frame and cannot do that.
	///
	/// ⭐ It also fixes a second thing for free: the prompt text node is reliably populated by the
	/// time we look, which it need not be at PostSetup.
	/// </summary>
	private void OnSetup(AddonEvent type, AddonArgs args) {
		if (!Plugin.Config.InteractAnswerGimmicks)
			return;

		if (DateTime.UtcNow > this.armedUntil) {
			Plugin.Diag("Interact: a yes/no box opened, but not from this key -- leaving it alone.");
			return;
		}

		this.pendingSince = DateTime.UtcNow;
	}

	public unsafe void Tick() {
		if (this.pendingSince == DateTime.MinValue)
			return;

		var unit = Plugin.GameGui.GetAddonByName(Addon);
		if (unit.IsNull) {
			// ⭐ Worth its own line rather than a shrug: this is what "YesAlready got there first"
			// looks like from in here, and it is the expected reading on deserok's own client.
			this.Give("the box was already gone a frame later -- something else answered it");
			return;
		}

		if (!unit.IsReady) {
			if (DateTime.UtcNow - this.pendingSince > Patience)
				this.Give("the box never finished loading");
			return;
		}

		var addon = (AddonSelectYesno*)(nint)unit;

		// ⚠⚠ BOTH accessors are read and BOTH are logged, even though only one is used. The framer
		// kit probe learned this the hard way: an accessor that silently returns the wrong thing
		// looks exactly like an accessor that works, right up until every row reads the same value.
		// If the node ever comes back empty this line says so instead of the feature just not firing.
		string node = ReadNode(addon);
		string value = ReadFirstValue(addon);
		string text = node.Length > 0 ? node : value;
		Plugin.Diag($"Interact: prompt node=\"{node}\" value=\"{value}\"");

		this.pendingSince = DateTime.MinValue;

		if (text.Length == 0) {
			this.Give($"could not read the prompt at all (node=\"{node}\" value=\"{value}\")");
			return;
		}

		if (!this.Prompts.Contains(Normalise(text))) {
			// ⚠ Named, not skipped silently -- the same rule as Choose() logging every rejected
			// candidate. A gimmick the sheet phrases differently shows up here as a quoted string
			// rather than as "the key does nothing at that lever".
			this.Give($"\"{text}\" is not in the GimmickYesNo sheet -- left for you");
			return;
		}

		// 0 is Yes, 1 is No. FireCallbackInt is the addon's own button handler, so this is the same
		// path the click takes rather than a synthetic mouse event.
		addon->AtkUnitBase.FireCallbackInt(0);
		this.LastAnswer = $"yes to \"{text}\"";
		Plugin.Log.Information($"Interact: answered yes to \"{text}\"");
		Plugin.Diag($"Interact: answered yes to \"{text}\"");
	}

	private void Give(string why) {
		this.pendingSince = DateTime.MinValue;
		this.LastAnswer = why;
		Plugin.Diag($"Interact: {why}");
	}

	private static unsafe string ReadNode(AddonSelectYesno* addon) {
		var node = addon->PromptText;
		return node is null ? string.Empty : node->NodeText.ExtractText().Trim();
	}

	private static unsafe string ReadFirstValue(AddonSelectYesno* addon) {
		var atk = &addon->AtkUnitBase;
		return atk->AtkValues is null || atk->AtkValuesCount == 0
			? string.Empty
			: (atk->AtkValues[0].GetValueAsString() ?? string.Empty).Trim();
	}

	/// <summary>
	/// Every gimmick prompt the game knows about, flattened for comparison.
	///
	/// ⚠ Read once and kept. The sheet cannot change while the client is running, and this runs on
	/// the frame a box appears.
	/// </summary>
	private HashSet<string> Prompts {
		get {
			if (this.prompts is not null)
				return this.prompts;

			this.prompts = new HashSet<string>(StringComparer.Ordinal);

			var gimmicks = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.GimmickYesNo>();
			if (gimmicks is null)
				Plugin.Log.Warning("Interact: GimmickYesNo sheet did not load.");
			else
				foreach (var row in gimmicks) {
					string text = row.Message.ExtractText();
					if (text.Length > 0)
						this.prompts.Add(Normalise(text));
				}

			// ⭐⭐ The SECOND sheet, added 2026-08-28 because Bunny found the hole: *"Enter Reisen Temple?"*
			// went unanswered. It is not a gimmick, it is a WARP -- and the game keeps those prompts in
			// their own sheet, one per door, ferry and area transition, in Warp.Question. 544 of them.
			//
			// ⭐ Including it is deserok's call, on domain knowledge no sheet contains: *"there is never a
			// case where you move to an area transition area and interact with it, and not intend to use
			// it. It's almost weird they confirm it. They're always out of the way, shoved into a corner,
			// likely to make things easier for console players to not accidentally interact."*
			var warps = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Warp>();
			if (warps is null)
				Plugin.Log.Warning("Interact: Warp sheet did not load.");
			else
				foreach (var row in warps) {
					string text = row.Question.ExtractText();
					if (text.Length > 0)
						this.prompts.Add(Normalise(text));
				}

			// ⚠ Rows 6 "Leave ?" and 38 "Use ?" have an unfilled parameter slot and can never match a
			// live prompt, and row 0 is the sheet's dummy text. All three are left in. Filtering them
			// would be three magic row numbers guarding against nothing -- they simply never match.
			Plugin.Log.Information($"Interact: {this.prompts.Count} prompts loaded from GimmickYesNo + Warp.");
			return this.prompts;
		}
	}

	/// <summary>
	/// ⚠ Whitespace is DROPPED rather than collapsed, because the two sides disagree about it in
	/// ways that are not worth enumerating: the sheet wraps with newlines the live box does not use,
	/// and the French prompts carry non-breaking spaces. Soft hyphens go for the same reason. What
	/// is left is compared exactly -- a substring match would let "Leave duty?" be found inside a
	/// longer prompt that means something else.
	/// </summary>
	private static string Normalise(string text) {
		var sb = new StringBuilder(text.Length);
		foreach (char c in text) {
			if (char.IsWhiteSpace(c) || c == '\u00ad')
				continue;
			sb.Append(char.ToLowerInvariant(c));
		}
		return sb.ToString();
	}

	public void Dispose() => Plugin.AddonLifecycle.UnregisterListener(this.OnSetup);
}
