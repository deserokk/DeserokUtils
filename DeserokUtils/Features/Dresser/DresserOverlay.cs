using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

using DeserokUtils.UI;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Dresser;

/// <summary>
/// A Scan button attached to the glamour dresser itself.
///
/// ⭐⭐ deserok's idea, and the right frame for it: *"this is a prime case for 'attach a button to
/// the UI'. A single Scan button, then a pop up with the readout and a Pack button."* You are already
/// stood at the dresser with it open — sending you to a separate plugin window to act on the thing in
/// front of you is the kind of detour that makes a tool feel like a tool rather than part of the game.
///
/// ⚠ It is an OVERLAY, not an injected control. The button is an ImGui widget positioned over the
/// game's window, tracking it as it moves. Adding a real node to the addon would look marginally more
/// native and would break on the first patch that reorders the dresser's node list — a bad trade for
/// a button.
///
/// ⚠ It hides with the game's interface, because a floating button over a hidden UI in somebody's
/// screenshot is worse than no button.
/// </summary>
internal sealed unsafe class DresserOverlay {
	private const string AddonName = "MiragePrismPrismBox";

	private readonly DresserFeature feature;
	private bool showResults;

	public DresserOverlay(DresserFeature feature) => this.feature = feature;

	public void Draw() {
		if (Plugin.GameGui.GameUiHidden) return;

		if (!TryGetFooterAnchor(out var anchor)) {
			this.showResults = false;
			return;
		}

		this.DrawAttachedButton(anchor);

		if (this.showResults) this.DrawResultsWindow();
	}

	/// <summary>
	/// The button itself, sitting on the window’s own footer row.
	///
	/// ⚠⚠ Getting this WRONG is what deserok noticed first: hung below the frame it reads as a
	/// dingleberry, where PF Analysis — which is also only an overlay — reads as part of the window
	/// because it shares the footer’s baseline. The illusion is entirely about alignment; nothing
	/// about it is more native than this.
	/// </summary>
	private void DrawAttachedButton(Vector2 anchor) {
		ImGui.SetNextWindowPos(anchor, ImGuiCond.Always, new Vector2(0f, 0.5f));

		const ImGuiWindowFlags flags =
			ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize
			| ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
			| ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoBackground;

		if (!ImGui.Begin("##dsu_dresser_button", flags)) { ImGui.End(); return; }

		var packer = this.feature.Packer;

		if (packer.Running) {
			if (Accent.Button("Stop packing", Accent.Amber)) packer.Stop("you stopped it");
		}
		else if (Accent.Button("Scan dresser", Accent.Blue)) {
			this.feature.Run();
			this.showResults = true;
		}

		ImGui.End();
	}

	/// <summary>
	/// The readout, in a window of its own rather than expanding the button.
	///
	/// ⭐ PF Analysis does exactly this, and it is the better shape: an inline panel growing out of a
	/// game window pushes everything around and cannot be moved out of the way, where a real window
	/// can be dragged, ignored, or closed.
	/// </summary>
	private void DrawResultsWindow() {
		var open = this.showResults;

		ImGui.SetNextWindowSize(new Vector2(330f, 0f), ImGuiCond.FirstUseEver);
		if (!ImGui.Begin("Dresser##dsu_dresser_results", ref open,
			    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) {
			ImGui.End();
			this.showResults = open;
			return;
		}

		this.DrawContents();

		ImGui.End();
		this.showResults = open;
	}

	private void DrawContents() {
		var packer = this.feature.Packer;

		if (packer.Running) {
			ImGui.TextColored(new Vector4(0.62f, 0.86f, 0.68f, 1f), packer.Status);
			ImGui.SameLine();
			if (ImGui.SmallButton("Stop")) packer.Stop("you stopped it");
			return;
		}

		var result = this.feature.Last;
		if (result is null) return;

		if (result.Problem is { } problem) {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.35f, 1f));
			ImGui.TextUnformatted(problem);
			ImGui.PopStyleColor();
			return;
		}

		ImGui.Text($"{result.Used} of {result.Capacity} slots used");

		// ⭐ Free storage beats packing, so it is said before the packing numbers rather than after.
		if (result.ArmoireEligible.Count > 0) {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.62f, 0.86f, 0.68f, 1f));
			ImGui.TextUnformatted($"{result.ArmoireEligible.Count} piece(s) your Armoire takes for free");
			ImGui.PopStyleColor();
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(
					"The Armoire stores these at no cost at all, so they are left out of\n"
					+ "the packing. Store them there and the slots go entirely.\n\n  "
					+ string.Join("\n  ", System.Linq.Enumerable.Take(result.ArmoireEligible, 12)));
		}

		if (result.ArmoireDuplicate.Count > 0) {
			ImGui.TextDisabled($"{result.ArmoireDuplicate.Count} piece(s) you already have in the Armoire");
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(
					"Already stored for free elsewhere, so the dresser copy is surplus.\n\n  "
					+ string.Join("\n  ", System.Linq.Enumerable.Take(result.ArmoireDuplicate, 12)));
		}


		// ⚠⚠ The armoire lines go ABOVE this, and that placement is the whole point. They used to
		// sit below, so "nothing left to pack" returned early and hid forty-two free slots — the most
		// valuable finding suppressed at exactly the moment it was the ONLY finding. Reported by
		// deserok, 2026-09-03.
		// ⚠⚠ Ask whether there is WORK, not whether the saving is positive. This gated on
		// SlotsRecoverable, which is a quantity rather than a test — and a single piece looted into
		// your bags scores MINUS one dresser slot (the dresser goes from holding none to holding an
		// outfit), so the job was real, worth doing, and silently hidden. Reported by deserok with a
		// Mistwake hood sitting in his bags, 2026-09-03.
		var work = result.Additions.Count + result.NewOutfits.Count + result.Duplicates.Count;

		if (work == 0) {
			ImGui.TextDisabled(result.ArmoireEligible.Count > 0
				? "No outfits to pack — but see the armoire pieces above."
				: "Nothing left to pack.");
			return;
		}

		// ⭐ The headline first. Somebody stood at the dresser wants the number that decides whether
		// to bother, not a breakdown.
		if (result.SlotsRecoverable > 0) {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.62f, 0.86f, 0.68f, 1f));
			ImGui.Text($"{result.SlotsRecoverable} dresser slot(s) recoverable");
			ImGui.PopStyleColor();
		}
		else if (result.SlotsRecoverable < 0) {
			// ⚠ Honest rather than hidden: filing loot from your bags into a brand-new outfit does
			// use a dresser slot. Worth doing, and worth saying.
			ImGui.TextDisabled($"costs {-result.SlotsRecoverable} dresser slot(s) to file this away");
		}

		// ⚠ Deliberately a second line, never added to the first. Filing loot away from your bags
		// is a different good thing from reclaiming dresser space, and an outfit built entirely from
		// the bags actually costs a dresser slot while freeing several bag ones.
		// ⭐ Said plainly rather than hidden in the headline: these buy nothing today and everything
		// later, and somebody deciding whether to spend the prisms deserves to know which.
		if (result.OutfitsStarted > 0) {
			ImGui.TextDisabled($"{result.OutfitsStarted} outfit(s) would be started from one piece");
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(
					"No slots recovered now — one in, one out.\n\n"
					+ "But once an outfit exists, every future piece of that set joins it\n"
					+ "for a whole free slot instead of sitting loose. Costs a prism each.");
		}

		if (result.BagSlotsFreed > 0) {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.62f, 0.78f, 0.92f, 1f));
			ImGui.Text($"{result.BagSlotsFreed} bag slot(s) filed away");
			ImGui.PopStyleColor();
		}

		ImGui.TextDisabled(
			$"{result.Additions.Count} addition(s), {result.NewOutfits.Count} new outfit(s), "
			+ $"{result.Duplicates.Count} duplicate(s)");

		if (result.InUseByPlate.Count > 0) {
			ImGui.TextDisabled($"{result.InUseByPlate.Count} piece(s) used by a glamour plate");
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(
					"Still packable — the game asks to confirm, and that is answered for you:\n\n  "
					+ string.Join("\n  ", System.Linq.Enumerable.Take(result.InUseByPlate, 12)));
		}

		// ⭐ Say whether it can run BEFORE the button is pressed, rather than refusing afterwards.
		var free = DresserPacker.FreeBagSlots();
		var room = free >= result.FreeSlotsNeeded;

		if (room) {
			ImGui.TextDisabled($"needs {result.FreeSlotsNeeded} free bag slot(s), you have {free}");
		}
		else {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.35f, 1f));
			ImGui.TextUnformatted($"needs {result.FreeSlotsNeeded} free bag slot(s), you have {free}");
			ImGui.PopStyleColor();
		}

		foreach (var (item, dye) in System.Linq.Enumerable.Distinct(result.ExpensiveDyes)) {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.35f, 1f));
			ImGui.TextUnformatted($"would destroy the {dye} on {item}");
			ImGui.PopStyleColor();
		}

		if (result.SkippedDyed > 0)
			ImGui.TextDisabled($"{result.SkippedDyed} dyed piece(s) left alone");

		var skipDyed = Plugin.Config.DresserSkipDyed;
		if (ImGui.Checkbox("Leave dyed pieces alone", ref skipDyed)) {
			Plugin.Config.DresserSkipDyed = skipDyed;
			Plugin.Config.Save();
			this.feature.Run();
		}
		if (ImGui.IsItemHovered())
			ImGui.SetTooltip(
				"Packing an item into an outfit destroys its dye.\n\n"
				+ "Most people at this point do not mind. Tick this if you would rather\n"
				+ "deal with the dyed ones yourself.");

		ImGui.Spacing();

		if (!room) ImGui.BeginDisabled();
		if (Accent.Button("Pack them", Accent.Amber)) this.feature.Packer.Start(result);
		if (!room) ImGui.EndDisabled();
		if (ImGui.IsItemHovered())
			ImGui.SetTooltip(
				"Takes a few minutes. Keep this window open — it stops if you close it.\n\n"
				+ "Undo any of it by right-clicking an outfit and choosing Restore Item.");

		if (packer.Verified is { } done) {
			ImGui.Spacing();
			ImGui.TextDisabled(done);
		}
	}

	/// <summary>
	/// Where to hang the button: just right of the window’s own lowest wide control, on its
	/// baseline.
	///
	/// ⭐⭐ Found by walking the addon’s nodes for the lowest visible wide one rather than by
	/// hard-coding an index or an offset. The dresser’s footer holds "Edit Glamour Plates"; a patch
	/// can move it or renumber the node list, and this still finds it. ⚠ A magic offset from the
	/// window corner would look right today and be subtly wrong the first time the window changes
	/// height.
	///
	/// ⚠ Falls back to the bottom-right of the frame if nothing suitable is found — a button in
	/// roughly the right place beats no button.
	/// </summary>
	private static bool TryGetFooterAnchor(out Vector2 anchor) {
		anchor = default;

		var addon = Plugin.GameGui.GetAddonByName(AddonName, 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return false;

		var unit = (AtkUnitBase*)addon.Address;
		if (unit->RootNode is null) return false;

		var scale = unit->Scale;
		var best = float.MinValue;
		var found = false;

		var uld = &unit->UldManager;
		if (uld->NodeList is not null) {
			for (var i = 0; i < uld->NodeListCount; i++) {
				var node = uld->NodeList[i];
				if (node is null || (node->NodeFlags & NodeFlags.Visible) == 0) continue;

				var w = node->Width * scale;
				var h = node->Height * scale;

				// Footer-button shaped: wide, short, and not the whole window.
				if (w < 80f || w > 400f || h < 16f || h > 48f) continue;

				if (node->ScreenY <= best) continue;

				best = node->ScreenY;
				anchor = new Vector2(node->ScreenX + w + 8f, node->ScreenY + (h * 0.5f));
				found = true;
			}
		}

		if (found) return true;

		anchor = new Vector2(
			unit->X + (unit->RootNode->Width * scale) - 8f,
			unit->Y + (unit->RootNode->Height * scale) - 24f);
		return true;
	}
}
