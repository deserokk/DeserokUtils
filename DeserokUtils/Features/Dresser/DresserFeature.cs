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
	/// <summary>⚠ A literal newline. Escaping one through the tooling has broken this file twice.</summary>
	private const string Nl = "\n";

	public string TabTitle => "Dresser";

	public string Summary => "Find outfits you could pack away, and duplicates you did not know about.";

	private readonly DresserScan scan = new();
	private DresserScan.Result? last;
	private string? logPath;
	private readonly DresserPacker packer = new();

	private readonly ArmoireTransfer armoire = new();

	/// <summary>
	/// ⚠ Wired here rather than inside the packer: the packer re-reads the dresser when it finishes,
	/// and this tab is what would otherwise keep showing the scan from before the run.
	/// </summary>
	public DresserFeature() {
		// ⚠ BOTH of them. Either one changes the dresser enough to invalidate every number on the
		// tab, and a button working from a stale scan is how a duplicate outfit gets built.
		this.packer.Rescanned = this.Refresh;
		this.armoire.Rescanned = this.Refresh;
	}

	private void Refresh(DresserScan.Result fresh) {
		this.last = fresh;
		DresserCache.Save(fresh);
	}

	/// <summary>For the overlay attached to the dresser window.</summary>
	internal DresserPacker Packer => this.packer;

	internal ArmoireTransfer Armoire => this.armoire;

	internal DresserScan.Result? Last => this.last;

	public void Tick() {
		this.packer.Tick();
		this.armoire.Tick();
	}

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

	/// <param name="quiet">
	/// ⭐⭐ True when the player is LOOKING at the result already — the tab, or the button on the
	/// dresser window. deserok, 2026-09-04: *"probably want less log spam in this next release, fine
	/// for us but Q and Bunny hate it."* Five chat lines for one button press is five lines somebody
	/// has to read past, and every one of them was already on screen.
	/// </param>
	public void Run(bool quiet = false) {
		this.last = this.scan.Scan();

		// ⚠ Before the report, so a scan that then fails to print still leaves the cache correct.
		DresserCache.Save(this.last);

		// ⚠⚠ The full dump is a DIAGNOSTIC and is now behind Verbose. It answered two questions
		// that could not be settled from documentation, and it stays for that — but writing several
		// hundred lines per scan on somebody else's machine, forever, to answer a question nobody is
		// asking, is a cost they never agreed to. Outcomes still always log.
		if (Plugin.Verbose) this.logPath = DresserLog.Write(this.last);

		if (!quiet) Report();
	}

	private void Report() {
		if (this.last is not { } r) return;

		if (r.Problem is { } problem) {
			Plugin.Chat.Print($"Dresser: {problem}");
			return;
		}

		// ⚠ One line. It is the answer to /dsu-dresser, and anything more belongs in the tab —
		// which is where the sentence sends you.
		Plugin.Chat.Print(
			$"Dresser: {r.Used}/{r.Capacity} used, {r.SlotsRecoverable} slot(s) recoverable. "
			+ "See the Dresser tab for the breakdown.");

		// ⚠ Diagnostic. Nobody who did not turn Verbose on wants a file path in their chat log.
		if (Plugin.Verbose && this.logPath is { } path)
			Plugin.Chat.Print($"Dresser: details written to {path}");
	}

	/// <summary>
	/// Settings only. The Dresser is operated from the button on the glamour dresser itself.
	///
	/// ⭐⭐⭐ deserok, 2026-09-05: *"the tab should not be operable at all, they kept running
	/// through the tab and the desired use is from the button we place in the glam chest, so we should
	/// unwire that button and leave it to configuration."*
	///
	/// He is right on both counts. **You cannot pack from here** — every action needs the dresser
	/// window open, so a Pack button in a plugin window is an invitation to press something that will
	/// refuse. And a scan taken here describes a dresser you are not looking at.
	///
	/// ⭐⭐ It also settles a bug that produced five separate reports in one night. The tab and the
	/// dresser overlay drew the same findings from two bodies of code, and every change had to be made
	/// twice — the loose-piece count, the Armoire button's condition, the skip reasons. Each time I
	/// fixed one and heard about the other. Deleting one of the two is a better fix than remembering
	/// harder.
	///
	/// ⚠ What stays: the switches. A setting belongs where settings live, and the overlay is a strip
	/// of buttons on somebody's furniture — not the place for checkboxes they set once.
	/// </summary>
	public void DrawTab() {
		ImGui.TextWrapped(this.Summary);
		ImGui.Spacing();

		ImGui.TextDisabled("Open your glamour dresser — the buttons are on it.");

		if (ImGui.IsItemHovered())
			ImGui.SetTooltip(
				"Scanning and packing both need the dresser window open, so they live there" + Nl
				+ "rather than here. This tab is for the switches.");

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		var tooltip = Plugin.Config.DresserTooltip;
		if (ImGui.Checkbox("Note what you own on item tooltips", ref tooltip)) {
			Plugin.Config.DresserTooltip = tooltip;
			Plugin.Config.Save();
		}

		if (ImGui.IsItemHovered()) {
			ImGui.SetTooltip(
				"Adds a line to the game's own item tooltips, anywhere they appear:" + Nl
				+ "a vendor, the market board, your bags, a Need/Greed roll." + Nl + Nl
				+ "    ✓ You have this appearance — Armoury Chest" + Nl
				+ "    ! You need this for an outfit" + Nl
				+ "    x You do not have this appearance" + Nl + Nl
				+ "The dresser half is remembered from your last scan, because the game" + Nl
				+ "only sends its contents while you stand at one. Your armoury and what" + Nl
				+ "you are wearing are read live.");
		}

		var skipDyed = Plugin.Config.DresserSkipDyed;
		if (ImGui.Checkbox("Leave dyed pieces alone", ref skipDyed)) {
			Plugin.Config.DresserSkipDyed = skipDyed;
			Plugin.Config.Save();
		}

		if (ImGui.IsItemHovered())
			ImGui.SetTooltip(
				"Packing an item into an outfit destroys its dye." + Nl + Nl
				+ "Most people do not mind. Tick this if you would rather deal with" + Nl
				+ "the dyed ones yourself.");

		var toArmoire = Plugin.Config.DresserArmoire;
		if (ImGui.Checkbox("Offer to move pieces into your Armoire", ref toArmoire)) {
			Plugin.Config.DresserArmoire = toArmoire;
			Plugin.Config.Save();
		}

		if (ImGui.IsItemHovered())
			ImGui.SetTooltip(
				"The Armoire stores gear for free, where an outfit still costs a slot." + Nl
				+ "Adds a second button that takes those pieces out of the dresser — and" + Nl
				+ "takes apart outfits it would accept whole — and puts them in." + Nl + Nl
				+ "Stand near your Armoire and it will open it for you." + Nl + Nl
				+ "Off by default: it moves your things without you watching each one.");

		ImGui.Spacing();
		ImGui.TextDisabled("Everything here is undone by right-clicking an outfit and");
		ImGui.TextDisabled("choosing Restore Item, or by withdrawing from the Armoire.");
	}
}
