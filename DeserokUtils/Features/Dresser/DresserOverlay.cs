using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

using DeserokUtils.UI;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Dresser;

internal sealed unsafe class DresserOverlay {
	private const string AddonName = "MiragePrismPrismBox";

	private readonly DresserFeature feature;
	private bool showResults;

	public DresserOverlay(DresserFeature feature) => this.feature = feature;

	public void Draw() {

		if (!Plugin.Config.DresserOverlay) return;

		if (Plugin.GameGui.GameUiHidden) return;

		if (!TryGetFooterAnchor(out var anchor)) {
			this.showResults = false;
			return;
		}

		this.DrawAttachedButton(anchor);

		if (this.showResults) this.DrawResultsWindow();
	}

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
			this.feature.Run(quiet: true);
			this.showResults = true;
		}

		ImGui.End();
	}

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

		if (result.EmptyOutfits.Count > 0) {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.35f, 1f));
			ImGui.TextUnformatted($"{result.EmptyOutfits.Count} outfit(s) in your dresser are empty");
			ImGui.PopStyleColor();
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(
					"An outfit holding no pieces. Earlier versions of this tool could create\n"
					+ "these, and the game gives no way to delete one, because deleting an outfit\n"
					+ "means restoring an item out of it.\n\n"
					+ "They are not lost slots forever: put any piece of that set in and the entry\n"
					+ "becomes a real outfit again. Packing does it for you the next time you loot\n"
					+ "one.\n\n  "
					+ string.Join("\n  ", System.Linq.Enumerable.Take(result.EmptyOutfits, 12)));
		}

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

		if (result.RedundantWithOutfit.Count > 0) {
			ImGui.TextDisabled($"{result.RedundantWithOutfit.Count} spare piece(s) their outfit already holds");
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(
					"The outfit for these already has that slot filled, so there is nowhere\n"
					+ "to pack them. Sell or desynth.\n\n  "
					+ string.Join("\n  ", System.Linq.Enumerable.Take(result.RedundantWithOutfit, 12)));
		}

		if (Plugin.Config.DresserArmoire
		    && (result.ArmoireTransfer.Count > 0 || result.Dissolvable.Count > 0)) {
			var moving = this.feature.Armoire;

			if (moving.Running) {
				ImGui.TextColored(new Vector4(0.62f, 0.86f, 0.68f, 1f), moving.Status);
				ImGui.SameLine();
				if (ImGui.SmallButton("Stop##armoire")) moving.Stop("you stopped it");
			}
			else {

				var label = result.Dissolvable.Count > 0 && result.ArmoireTransfer.Count > 0
					? $"Move {result.ArmoireTransfer.Count} piece(s) and {result.Dissolvable.Count} outfit(s) to the Armoire"
					: result.Dissolvable.Count > 0
						? $"Take apart {result.Dissolvable.Count} outfit(s) into the Armoire"
						: $"Move {result.ArmoireTransfer.Count} piece(s) to the Armoire";

				if (Accent.Button(label, Accent.Amber)) moving.Start(result);
			}

			foreach (var why in moving.Failed) {
				ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.35f, 1f));
				ImGui.TextWrapped($"• {why}");
				ImGui.PopStyleColor();
			}
		}

		if (result.FullyArmoireOutfits.Count > 0) {
			ImGui.TextDisabled(
				$"{result.FullyArmoireOutfits.Count} packed outfit(s) the Armoire would take whole");

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(
					"Every piece in these is Armoire-eligible, so the whole entry could go\n"
					+ "and the slot would cost nothing. Unpacking is not automated yet.\n\n  "
					+ string.Join("\n  ", System.Linq.Enumerable.Select(
						System.Linq.Enumerable.Take(result.FullyArmoireOutfits, 12),
						x => $"{x.Name} ({x.Pieces})")));
		}

		if (result.ArmoireDuplicate.Count > 0) {
			ImGui.TextDisabled($"{result.ArmoireDuplicate.Count} piece(s) you already have in the Armoire");
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(
					"Already stored for free elsewhere, so the dresser copy is surplus.\n\n  "
					+ string.Join("\n  ", System.Linq.Enumerable.Take(result.ArmoireDuplicate, 12)));
		}

		var work = result.Additions.Count + result.NewOutfits.Count
		         + result.Duplicates.Count + result.StoreLoose.Count;

		if (work == 0) {
			ImGui.TextDisabled(result.ArmoireEligible.Count > 0
				? "No outfits to pack — but see the armoire pieces above."
				: "Nothing left to pack.");
			return;
		}

		if (result.StoreLoose.Count > 0) {
			ImGui.TextUnformatted($"{result.StoreLoose.Count} loose piece(s) to put in the dresser");
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(
					"Gear in your bags whose appearance the dresser does not already hold.\n\n  "
					+ string.Join("\n  ", System.Linq.Enumerable.Select(
						System.Linq.Enumerable.Take(result.StoreLoose, 12), x => x.Name)));
		}

		if (result.SlotsRecoverable > 0) {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.62f, 0.86f, 0.68f, 1f));
			ImGui.Text($"{result.SlotsRecoverable} dresser slot(s) recoverable");
			ImGui.PopStyleColor();
		}
		else if (result.SlotsRecoverable < 0) {

			ImGui.TextDisabled($"costs {-result.SlotsRecoverable} dresser slot(s) to file this away");
		}

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
			this.feature.Run(quiet: true);
		}
		if (ImGui.IsItemHovered())
			ImGui.SetTooltip(
				"Packing an item into an outfit destroys its dye.\n\n"
				+ "Most people at this point do not mind. Tick this if you would rather\n"
				+ "deal with the dyed ones yourself.");

		ImGui.Spacing();

		if (DresserPacker.Enabled) {
			if (!room) ImGui.BeginDisabled();
			if (Accent.Button("Pack them", Accent.Amber)) this.feature.Packer.Start(result);
			if (!room) ImGui.EndDisabled();
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(
					"Takes a few minutes. Keep this window open — it stops if you close it.\n\n"
					+ "Undo any of it by right-clicking an outfit and choosing Restore Item.");
		}
		else {

			ImGui.TextDisabled("Packing is off in this build — see the tab for why.");
		}

		foreach (var why in packer.Skipped) {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.35f, 1f));
			ImGui.TextWrapped($"• {why}");
			ImGui.PopStyleColor();
		}

		if (packer.Verified is { } done) {
			ImGui.Spacing();
			ImGui.TextDisabled(done);
		}
	}

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
