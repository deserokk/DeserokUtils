using System;
using System.Collections.Generic;

using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.UI;

using DeserokUtils.Features.CastWatch;
using DeserokUtils.Features.DrawSheathe;
using DeserokUtils.Features.EmoteQuiet;
using DeserokUtils.Features.EphemeralMarks;
using DeserokUtils.Features.FateWatch;
using DeserokUtils.Features.IfMouseover;
using DeserokUtils.Features.Interact;
using DeserokUtils.Features.FcBuffs;
using DeserokUtils.UI;

namespace DeserokUtils;

/// <summary>
/// A bucket for small utilities. This file is the SHELL only -- services, the window, and the
/// plugin-level command. Every feature owns a folder under Features/, registers its own commands,
/// and contributes one tab. Nothing feature-specific belongs here, so adding the second utility
/// never means editing the first one's code.
/// </summary>
public sealed class Plugin: IDalamudPlugin {
	internal static ICommandManager Commands { get; private set; } = null!;
	internal static IChatGui Chat { get; private set; } = null!;
	internal static IObjectTable Objects { get; private set; } = null!;
	internal static IDataManager Data { get; private set; } = null!;
	internal static ITargetManager Targets { get; private set; } = null!;
	internal static IPartyList Party { get; private set; } = null!;
	internal static IGameInteropProvider Interop { get; private set; } = null!;
	internal static IPluginLog Log { get; private set; } = null!;
	internal static IFateTable Fates { get; private set; } = null!;
	internal static IFramework Framework { get; private set; } = null!;
	internal static IToastGui Toasts { get; private set; } = null!;
	internal static IDtrBar Dtr { get; private set; } = null!;
	internal static IClientState ClientState { get; private set; } = null!;
	internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
	internal static ICondition Condition { get; private set; } = null!;
	internal static IGameGui GameGui { get; private set; } = null!;
	internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
	internal static IKeyState Keys { get; private set; } = null!;
	internal static ITextureProvider Textures { get; private set; } = null!;
	internal static Configuration Config { get; private set; } = null!;

	/// <summary>
	/// Diagnostic output to the Debug chat channel. Shared across features, because "what did it
	/// see, and why did it decide that" is the same question every time.
	///
	/// ⚠⚠ A PASSTHROUGH to the config, not a separate field. It used to be its own static seeded
	/// from the config at load, so both the checkbox and /dsu debug flipped the static and never
	/// saved -- turn it off, restart, and it was on again, with nothing anywhere explaining why.
	/// </summary>
	internal static bool Verbose {
		get => Config?.Verbose ?? false;
		set {
			if (Config is null || Config.Verbose == value)
				return;
			Config.Verbose = value;
			Config.Save();
		}
	}

	private readonly WindowSystem windows = new("DeserokUtils");
	private readonly MainWindow mainWindow;
	private readonly List<IDisposable> features = new();

	private Features.Dresser.DresserFeature? dresser;
	private Features.Dresser.DresserOverlay? dresserOverlay;
	private Features.Dresser.DresserTooltip? dresserTooltip;

	private readonly FateWatchFeature fateWatch;
	private readonly FcBuffsFeature fcBuffs;
	private readonly EphemeralMarksFeature marks;
	private readonly Features.AchievementData.AchievementPreload achievements;
	private readonly Features.DebuffMarks.DebuffMarksFeature debuffs;
	private readonly InteractFeature interact;

	/// <summary>⭐ Direct key binding, because the hotbar is locked during a conversation and a
	/// macro therefore cannot run when you most want this. See <see cref="Input.Keybind"/>.</summary>
	private readonly Input.KeybindWatcher keybinds = new();

	public Plugin(
		IDalamudPluginInterface pluginInterface,
		ICommandManager commands,
		IChatGui chat,
		IObjectTable objects,
		IDataManager data,
		ITargetManager targets,
		IPartyList party,
		IGameInteropProvider interop,
		IPluginLog log,
		IFateTable fates,
		IFramework framework,
		IToastGui toasts,
		IDtrBar dtr,
		IClientState clientState,
		IAddonLifecycle addonLifecycle,
		ICondition condition,
		IGameGui gameGui,
		IKeyState keys,
		ITextureProvider textures) {

		Commands = commands;
		Chat = chat;
		Objects = objects;
		Data = data;
		Targets = targets;
		Party = party;
		Interop = interop;
		Log = log;
		Fates = fates;
		Framework = framework;
		Toasts = toasts;
		Dtr = dtr;
		ClientState = clientState;
		AddonLifecycle = addonLifecycle;
		Condition = condition;
		GameGui = gameGui;
		Keys = keys;
		Textures = textures;
		PluginInterface = pluginInterface;

		Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
		Config.Migrate();

		// ── features ─────────────────────────────────────────────────────────────────────────
		// Each registers its own commands in its constructor and hands back a tab. Adding another
		// is two lines here plus one new folder. Still no IFeature interface, and at ten features
		// that is a measured result rather than the original guess -- see MainWindow, where the
		// implementations turned out to agree on nothing except how they present.
		var castWatch = new CastWatchFeature();
		this.features.Add(castWatch);
		this.fateWatch = new FateWatchFeature();
		this.features.Add(this.fateWatch);
		this.fcBuffs = new FcBuffsFeature();
		this.features.Add(this.fcBuffs);
		// ⭐ The fourth one, and still two lines plus a folder. No Tick(), no hook, no listener --
		// it is a command handler and a tab, which is the least a feature here has ever needed.
		var drawSheathe = new DrawSheatheFeature();
		this.features.Add(drawSheathe);
		var emoteQuiet = new EmoteQuietFeature();
		this.features.Add(emoteQuiet);
		var ifMouseover = new IfMouseoverFeature();
		this.features.Add(ifMouseover);
		var marks = new EphemeralMarksFeature();
		this.features.Add(marks);
		// ⭐ Two more, still two lines each and a folder. ItemUse is the first feature here that adds a
		// capability the GAME does not have rather than fixing how one behaves -- there is no item text
		// command at all -- and MacroIcons is the other half of the same gap.
		var itemUse = new Features.ItemUse.ItemUseFeature();
		this.features.Add(itemUse);
		var macroIcons = new Features.MacroIcons.MacroIconFeature();
		this.features.Add(macroIcons);

		// ⚠ No tab and no command: one request per login and nothing else. See the class for why the
		// tooltip feature that used to sit here is gone.
		this.achievements = new Features.AchievementData.AchievementPreload();
		this.features.Add(this.achievements);
		var partyJobs = new Features.PartyJobs.PartyJobsFeature();
		this.features.Add(partyJobs);
		var debuffs = new Features.DebuffMarks.DebuffMarksFeature();
		this.features.Add(debuffs);
		var meldWindow = new Features.MeldFlow.MeldWindowKeeper();
		this.features.Add(meldWindow);
		var repairs = new Features.Repairs.RepairAcceptFeature();
		this.features.Add(repairs);
		var dresser = new Features.Dresser.DresserFeature();
		this.dresser = dresser;
		dresser.Listen();

		this.dresserTooltip = new Features.Dresser.DresserTooltip();
		this.dresserTooltip.Listen();
		this.dresserOverlay = new Features.Dresser.DresserOverlay(dresser);
		this.debuffs = debuffs;
		this.marks = marks;
		var interact = new InteractFeature();
		this.features.Add(interact);
		this.interact = interact;
		this.keybinds.Register("interact", "Interact", () => Config.InteractKey, interact.Press);
		this.keybinds.Register("drawsheathe", "Draw / sheathe", () => Config.DrawSheatheKey, drawSheathe.Press,
			repeats: false);
		this.keybinds.Register("openwindow", "Open this window", () => Config.OpenWindowKey, () => OpenWindow(),
			repeats: false);

		// ⭐ A feature declares its own GROUP, or null for a tab of its own. Grouping is earned by
		// having relatives: the macro tools are two faces of one complaint, everything else stands
		// alone. Adding another utility is still one line here and one folder.
		const string macros = "Macros";
		this.mainWindow = new MainWindow([
			new TabEntry(macros, castWatch.TabTitle, castWatch.Summary, castWatch.DrawTab),
			new TabEntry(macros, ifMouseover.SectionTitle, ifMouseover.Summary, ifMouseover.DrawSection),
			new TabEntry(macros, itemUse.SectionTitle, itemUse.Summary, itemUse.DrawSection),
			new TabEntry(macros, macroIcons.TabTitle, string.Empty, macroIcons.DrawTab),
			new TabEntry(null, this.fateWatch.TabTitle, string.Empty, this.fateWatch.DrawTab),
			new TabEntry(null, this.fcBuffs.TabTitle, string.Empty, this.fcBuffs.DrawTab),
			new TabEntry(null, drawSheathe.TabTitle, string.Empty, drawSheathe.DrawTab),
			new TabEntry(null, emoteQuiet.TabTitle, string.Empty, emoteQuiet.DrawTab),
			new TabEntry(null, interact.TabTitle, string.Empty, interact.DrawTab),
			new TabEntry(null, marks.TabTitle, string.Empty, marks.DrawTab),
			new TabEntry(null, debuffs.TabTitle, string.Empty, debuffs.DrawTab),
			new TabEntry(null, meldWindow.TabTitle, string.Empty, meldWindow.DrawTab),
			new TabEntry(null, repairs.TabTitle, string.Empty, repairs.DrawTab),
			new TabEntry(null, dresser.TabTitle, dresser.Summary, dresser.DrawTab),
			new TabEntry(null, partyJobs.TabTitle, string.Empty, partyJobs.DrawTab),
			new TabEntry(null, Input.KeybindsTab.TabTitle, string.Empty, () => Input.KeybindsTab.Draw(this.keybinds)),
		]);
		this.windows.AddWindow(this.mainWindow);

		PluginInterface.UiBuilder.Draw += this.windows.Draw;
		PluginInterface.UiBuilder.Draw += SampleImGuiState;
		PluginInterface.UiBuilder.Draw += () => this.dresserOverlay?.Draw();
		PluginInterface.UiBuilder.OpenMainUi += this.OpenMain;
		PluginInterface.UiBuilder.OpenConfigUi += this.OpenMain;

		OpenWindow = this.OpenMain;
		Framework.Update += this.OnFrameworkUpdate;

		Commands.AddHandler("/deserokutils", new CommandInfo(this.OnPluginCommand) {
			HelpMessage = "/deserokutils -- open the utilities window. Add 'debug' to toggle diagnostics.",
		});
		// ⭐ Branded, per the /dsu- convention: a generic name loses the race on a client
		// carrying a hundred plugins.
		Commands.AddHandler("/dsu-dresser", new CommandInfo((_, _) => dresser.Run()) {
			HelpMessage = "Report what your glamour dresser could pack away.",
		});



		Commands.AddHandler("/dsu", new CommandInfo(this.OnPluginCommand) {
			HelpMessage = "/dsu -- short form of /deserokutils.",
		});
	}

	/// <summary>⚠ The ONLY place ImGui state may be read for the keybind watcher. See
	/// <see cref="Input.KeybindWatcher.TextInputActive"/>.</summary>
	private static void SampleImGuiState() =>
		Input.KeybindWatcher.TextInputActive = Dalamud.Bindings.ImGui.ImGui.GetIO().WantTextInput;

	private void OnFrameworkUpdate(IFramework framework) {
		this.fateWatch.Tick();
		this.fcBuffs.Tick();
		this.marks.Tick();
		this.achievements.Tick();
		this.debuffs.Tick();
		this.interact.Tick();
		this.keybinds.Tick();
		this.dresser?.Tick();
	}

	private void OpenMain() => this.mainWindow.IsOpen = true;

	/// <summary>Static hook so a feature can open the window without holding a reference to it.</summary>
	internal static Action OpenWindow { get; private set; } = () => { };

	private void OnPluginCommand(string command, string arguments) {
		switch (arguments.Trim().ToLowerInvariant()) {
			case "debug" or "diag" or "verbose":
				Verbose = !Verbose;
				Chat.Print($"[DeserokUtils] diagnostics {(Verbose ? "ON" : "off")} (Debug channel).");
				break;

			default:
				this.mainWindow.Toggle();
				break;
		}
	}

	/// <summary>
	/// Diagnostics go to the Debug channel so they land in a tab configured for them rather than in
	/// the middle of a pull. Genuine user-facing errors do NOT come through here -- those stay in
	/// normal chat where they cannot be missed.
	/// </summary>
	internal static void Diag(string message) {
		if (!Verbose)
			return;
		Chat.Print(new XivChatEntry {
			Type = XivChatType.Debug,
			Name = "DeserokUtils",
			Message = message,
		});
	}

	/// <summary>
	/// A thing the user asked to be told about -- NOT a diagnostic.
	///
	/// ⚠ Deliberately separate from Diag: diagnostics go to a tab you check afterwards, an alert has
	/// to reach you while you are looking at the game. Routing both to the same place would mean
	/// either the alerts get buried or the diagnostics start interrupting.
	/// </summary>
	internal static void Announce(string message) {
		if (Config.AlertToast)
			Toasts.ShowQuest(message, new Dalamud.Game.Gui.Toast.QuestToastOptions {
				PlaySound = Config.AlertSound,
				DisplayCheckmark = false,
			});
		else if (Config.AlertSound)
			UIGlobals.PlayChatSoundEffect(1);

		if (Config.AlertChat)
			Chat.Print($"[DeserokUtils] {message}");

		Log.Information($"alert: {message}");
	}

	public void Dispose() {
		Framework.Update -= this.OnFrameworkUpdate;
		Commands.RemoveHandler("/deserokutils");
		Commands.RemoveHandler("/dsu");
		Commands.RemoveHandler("/dsu-dresser");

		PluginInterface.UiBuilder.Draw -= this.windows.Draw;
		PluginInterface.UiBuilder.Draw -= SampleImGuiState;
		PluginInterface.UiBuilder.OpenMainUi -= this.OpenMain;
		PluginInterface.UiBuilder.OpenConfigUi -= this.OpenMain;
		this.windows.RemoveAllWindows();

		// ⚠ Not in this.features -- the Dresser is wired by hand, so its listener has to be
		// unregistered by hand too. A stale AddonLifecycle listener survives a plugin reload.
		this.dresser?.Dispose();
		this.dresserTooltip?.Dispose();

		foreach (var feature in this.features)
			feature.Dispose();
	}
}
