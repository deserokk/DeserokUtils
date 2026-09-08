using System;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

using DeserokUtils.UI;

namespace DeserokUtils.Features.Dresser;

internal sealed class DresserFeature {

	private const string Nl = "\n";

	public string TabTitle => "Dresser";

	public string Summary => "Find outfits you could pack away, and duplicates you did not know about.";

	private readonly DresserScan scan = new();
	private DresserScan.Result? last;
	private string? logPath;
	private readonly DresserPacker packer = new();

	private readonly ArmoireTransfer armoire = new();

	public DresserFeature() {

		this.packer.Rescanned = this.Refresh;
		this.armoire.Rescanned = this.Refresh;
	}

	private void Refresh(DresserScan.Result fresh) {
		this.last = fresh;
		DresserCache.Save(fresh);
	}

	internal DresserPacker Packer => this.packer;

	internal ArmoireTransfer Armoire => this.armoire;

	internal DresserScan.Result? Last => this.last;

	public void Tick() {
		this.packer.Tick();
		this.armoire.Tick();
	}

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

		if (fresh.Loaded && fresh.Problem is null) {

			this.last = fresh;
			DresserCache.Save(fresh);
		}
		else DresserCache.MarkStale();
	}

	public void Run(bool quiet = false) {
		this.last = this.scan.Scan();

		DresserCache.Save(this.last);

		if (Plugin.Verbose) this.logPath = DresserLog.Write(this.last);

		if (!quiet) Report();
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

		if (Plugin.Verbose && this.logPath is { } path)
			Plugin.Chat.Print($"Dresser: details written to {path}");
	}

	public static bool AnyEnabled
		=> Plugin.Config.DresserOverlay || Plugin.Config.DresserTooltip;

	public static void SetEnabled(bool on) {
		Plugin.Config.DresserOverlay = on;
		if (!on)
			Plugin.Config.DresserTooltip = false;

		Plugin.Config.Save();
	}

	public void DrawTab() {

		var overlay = Plugin.Config.DresserOverlay;
		if (ImGui.Checkbox("Put the scan and pack buttons on the glamour dresser", ref overlay)) {
			Plugin.Config.DresserOverlay = overlay;
			Plugin.Config.Save();
		}

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
				+ "Stand near your Armoire and it will open it for you.");

	}
}
