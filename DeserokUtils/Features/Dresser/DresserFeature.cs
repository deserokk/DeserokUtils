using System;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

using DeserokUtils.UI;

namespace DeserokUtils.Features.Dresser;

/// <summary>
/// Tells you what your glamour dresser could pack away.
///
/// ⭐⭐ A run-once tool, and that is a category rather than an excuse. deserok, 2026-09-03: *"when
/// you need it, you really need it but once it's ran, you probably won't need it again for
/// months."* So it is judged on value-when-needed, not on frequency : the argument I originally
/// used against a home for tools like this, and got wrong.
///
/// ## What it is actually for
///
/// The dresser holds 800 items in a game with thousands. Outfits collapse several pieces into one
/// slot, but the dresser cannot assemble them : you have to find the pieces by eye across paginated
/// tabs. **The scanning is the cost, not the clicking.** deserok did six sets by hand and stopped,
/// and separately discovered he owned three copies of one helm, bought months apart, because a
/// person cannot diff twenty pages of icons.
///
/// ⭐ So the headline number is the product. Somebody at 403/800 does not want a list, they want to
/// know whether this recovers twelve slots or eighty : that decides whether they bother.
/// </summary>
internal sealed class DresserFeature {
	public string TabTitle => "Dresser";

	public string Summary => "Find outfits you could pack away, and duplicates you did not know about.";

	private readonly DresserScan scan = new();
	private DresserScan.Result? last;
	private bool showDetail;
	private string? logPath;
	private readonly DresserPacker packer = new();

	/// <summary>For the overlay attached to the dresser window.</summary>
	internal DresserPacker Packer => this.packer;

	internal DresserScan.Result? Last => this.last;

	public void Tick() => this.packer.Tick();

	/// <summary>
	/// Keep the cached snapshot honest, without anybody having to remember to scan.
	///
	/// ⭐⭐⭐ THE DRESSER CANNOT CHANGE WITH ITS WINDOW SHUT. deserok, 2026-09-04: *"this is one
	/// snapshot that only can be stale if the user opens the glamour chest, puts something in and
	/// doesn't scan, and we know when the dresser is open because we have to draw the button."*
	/// That observation is what makes a cache trustworthy rather than merely convenient — there is
	/// exactly one moment when it can go wrong.
	///
	/// ⭐⭐ THE TRIGGER IS BORROWED, and it is better than the one it replaces. This first polled
	/// the window every frame and refreshed when it closed. Seventhxiv/Collections listens for
	/// <c>PostRefresh</c> on the dresser addon instead, which the game raises whenever the contents
	/// change — a page turn, a store, a restore. That is both cheaper (no per-frame work at all, in a
	/// codebase that has been bitten by draw-loop cost twice) and more correct: the cache keeps up
	/// WHILE you rearrange, rather than catching up when you walk away.
	///
	/// ⚠ Silent. No chat line, no log dump — those belong to a scan somebody asked for. This one
	/// exists so nobody has to ask, because "remember to press Scan or the tooltips lie" is not a
	/// thing to build on.
	///
	/// ⚠ <see cref="DresserCache.MarkStale"/> stays for the case this cannot cover: the plugin
	/// enabled halfway through a dresser session, or contents that never loaded.
	/// </summary>
	public void Listen() {
		Plugin.AddonLifecycle.RegisterListener(
			AddonEvent.PostRefresh, "MiragePrismPrismBox", this.OnDresserRefreshed);
	}

	public void Dispose() {
		Plugin.AddonLifecycle.UnregisterListener(
			AddonEvent.PostRefresh, "MiragePrismPrismBox", this.OnDresserRefreshed);
	}

	private void OnDresserRefreshed(AddonEvent type, AddonArgs args) {
		var fresh = new DresserScan().Scan();

		if (fresh.Loaded && fresh.Problem is null) DresserCache.Save(fresh);
		else DresserCache.MarkStale();
	}

	public void Run() {
		this.last = this.scan.Scan();

		// ⚠ Before the report, so a scan that then fails to print still leaves the cache correct.
		DresserCache.Save(this.last);

		// ⭐⭐ Always dump. Two things in this feature could not be settled from documentation and
		// are answerable only from a real dresser — and a summary relayed through a person drops
		// exactly the detail the dump exists to carry. See DresserLog.
		this.logPath = DresserLog.Write(this.last);

		Report();
	}

	private void Report() {
		if (this.last is not { } r) return;

		if (r.Problem is { } problem) {
			Plugin.Chat.Print($"Dresser: {problem}");
			return;
		}

		Plugin.Chat.Print(
			$"Dresser: {r.Used}/{r.Capacity} used, {r.SlotsRecoverable} slot(s) recoverable. "
			+ "See the Dresser tab for the breakdown.");

		if (this.logPath is { } path) Plugin.Chat.Print($"Dresser: details written to {path}");
	}

	public void DrawTab() {
		ImGui.TextWrapped(this.Summary);
		ImGui.Spacing();

		// ⭐⭐ THE TOOLTIP IS THE PART MOST PEOPLE WILL EVER USE, so it is settled here rather than
		// buried: it works with no scan, no button and no reading, which is the participant case.
		//
		// ⚠ On by default, and a switch all the same. "Install it and it works" is the goal, but a
		// feature that writes on every item tooltip in the game and cannot be turned off is somebody
		// else's plugin fighting yours with no way to stop it.
		var tooltip = Plugin.Config.DresserTooltip;
		if (ImGui.Checkbox("Note what you own on item tooltips", ref tooltip)) {
			Plugin.Config.DresserTooltip = tooltip;
			Plugin.Config.Save();
		}

		if (ImGui.IsItemHovered()) {
			ImGui.SetTooltip(
				"Adds a line to the game's own item tooltips, anywhere they appear:\n"
				+ "a vendor, the market board, your bags, a Need/Greed roll.\n\n"
				+ "    ✓ You have this appearance — in your armoury chest\n"
				+ "    ! You need this for an outfit\n"
				+ "    x You do not have this appearance\n\n"
				+ "The dresser half is remembered from your last scan, because the game\n"
				+ "only sends its contents while you stand at one. Your armoury and what\n"
				+ "you are wearing are read live.");
		}

		ImGui.Spacing();

		if (Accent.Button("Scan my dresser", Accent.Blue)) this.Run();

		ImGui.SameLine();
		ImGui.TextDisabled("(or /dsu-dresser)");

		if (this.logPath is { } path) {
			ImGui.SameLine();
			if (ImGui.SmallButton("Copy log path")) ImGui.SetClipboardText(path);
			if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
		}

		if (this.last is not { } r) {
			ImGui.Spacing();
			ImGui.TextDisabled("Nothing scanned yet.");
			return;
		}

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		if (r.Problem is { } problem) {
			ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.75f, 0.35f, 1f));
			ImGui.TextWrapped(problem);
			ImGui.PopStyleColor();
			return;
		}

		ImGui.Text($"{r.Used} of {r.Capacity} slots used");
		ImGui.Spacing();

		Line("Pieces that fit outfits you already have", r.Additions.Count, r.SlotsFromAdditions);
		Line("New outfits you could pack", r.NewOutfits.Count, r.SlotsFromNewOutfits);
		Line("Exact duplicates", r.Duplicates.Count, r.SlotsFromDuplicates);

		ImGui.Separator();

		// ⭐ The one number the whole tool exists to produce.
		ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.62f, 0.86f, 0.68f, 1f));
		ImGui.Text($"Recoverable: {r.SlotsRecoverable} slot(s)");
		ImGui.PopStyleColor();

		if (r.SlotsRecoverable == 0) {
			ImGui.TextDisabled("Your dresser is already as packed as it can be.");
			return;
		}

		ImGui.Spacing();
		ImGui.TextDisabled($"Would cost about {r.PrismsNeeded} glamour prism(s)");
		ImGui.TextDisabled($"Needs {r.FreeSlotsNeeded} free inventory slot(s) at once");

		// ⚠ Only ever the two dyes worth stopping for. Every other one is cheap, and warning about
		// all of them would train people to ignore the warning.
		foreach (var (item, dye) in r.ExpensiveDyes.Distinct()) {
			ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.75f, 0.35f, 1f));
			ImGui.TextWrapped($"Packing would destroy the {dye} on {item}.");
			ImGui.PopStyleColor();
		}

		ImGui.Spacing();

		// ── Packing ───────────────────────────────────────────────────────

		if (!DresserPacker.Enabled) {
			ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.75f, 0.35f, 1f));
			ImGui.TextWrapped("Packing is turned off.");
			ImGui.PopStyleColor();
			ImGui.TextWrapped(
				"It could build an outfit for the wrong set, which left duplicates and a few "
				+ "empty outfits behind. The scan above is read-only and unaffected — it will "
				+ "still tell you what is worth packing by hand.");
			ImGui.TextDisabled("Empty outfits are not lost slots: put any piece of that set in "
			                   + "and the entry works again.");
		}
		else if (this.packer.Running) {
			ImGui.TextColored(new System.Numerics.Vector4(0.62f, 0.86f, 0.68f, 1f), this.packer.Status);
			if (ImGui.Button("Stop")) this.packer.Stop("you stopped it");
		}
		else {
			if (Accent.Button("Pack them for me", Accent.Amber)) this.packer.Start(r);
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(
					"Restores each piece and stores it back as an outfit.\n\n"
					+ "Takes a few minutes and needs the glamour dresser window open the\n"
					+ "whole time — it stops if you close it. Everything it does is undone\n"
					+ "by right-clicking an outfit and choosing Restore Item.");

			if (this.packer.Verified is { } done) {
				ImGui.SameLine();
				ImGui.TextDisabled(done);
			}
		}

		ImGui.Spacing();
		ImGui.Checkbox("Show me what it found", ref this.showDetail);

		if (!this.showDetail) return;

		ImGui.Spacing();

		if (r.Additions.Count > 0) {
			ImGui.TextDisabled("Fits an outfit you already have");
			foreach (var a in r.Additions) {
				ImGui.BulletText($"{a.OutfitName} — add {a.Pieces.Count}");
				foreach (var p in a.Pieces) {
					ImGui.Indent(18f);
					ImGui.TextDisabled($"{DresserScan.SlotNames[p.Slot]}: {p.Name}");
					ImGui.Unindent(18f);
				}
			}
			ImGui.Spacing();
		}

		if (r.NewOutfits.Count > 0) {
			ImGui.TextDisabled("Could become a new outfit");
			foreach (var o in r.NewOutfits) {
				ImGui.BulletText($"{o.SetName} — {o.Pieces.Count} piece(s), saves {o.Pieces.Count - 1}");
				foreach (var p in o.Pieces) {
					ImGui.Indent(18f);
					ImGui.TextDisabled($"{DresserScan.SlotNames[p.Slot]}: {p.Name}");
					ImGui.Unindent(18f);
				}
			}
			ImGui.Spacing();
		}

		if (r.Duplicates.Count > 0) {
			ImGui.TextDisabled("You own more than one of these");
			foreach (var d in r.Duplicates)
				ImGui.BulletText($"{d.Name} × {d.Indices.Count}");
			ImGui.Spacing();
		}

		// ⭐ What your existing outfits are short of, asked for directly — and the check on
		// whether IsSetSlotUnlocked means what this code assumes it means.
		if (r.Packed.Count > 0) {
			ImGui.TextDisabled("Outfits you already have, and what they are missing");
			foreach (var o in r.Packed) {
				var have = o.Slots.Count(x => x.Filled);
				ImGui.BulletText($"{o.Name} — {have} of {o.Slots.Count}");

				foreach (var (_, item, filled) in o.Slots) {
					ImGui.Indent(18f);
					if (filled) ImGui.TextDisabled($"have  {item}");
					else ImGui.TextColored(new System.Numerics.Vector4(0.75f, 0.7f, 0.55f, 1f),
						$"need  {item}");
					ImGui.Unindent(18f);
				}
			}
		}
	}

	private static void Line(string label, int count, int slots) {
		if (count == 0) {
			ImGui.TextDisabled($"{label}: none");
			return;
		}

		ImGui.Text($"{label}: {count}");
		ImGui.SameLine();
		ImGui.TextDisabled($"→ {slots} slot(s)");
	}
}
