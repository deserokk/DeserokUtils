using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Interface;
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
using DeserokUtils.Features.MeldFlow;
using DeserokUtils.Features.FcBuffs;
using DeserokUtils.UI;

namespace DeserokUtils;

internal static class Domains {

	public const string All = "All";

	public const string Macros = "Macro extensions";

	public const string Visual = "Visual";

	public const string Chat = "Chat";

	public const string Automation = "Automation";

	public const string Alerts = "Alerts";

	public const string Qol = "Quality of life";

	public const string Information = "Information";
}

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

	private readonly Features.Fanfare.FanfareFeature fanfare;
	private readonly OptionsWindow optionsWindow;

	private readonly Fonts fonts = new();
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

		var castWatch = new CastWatchFeature();
		this.features.Add(castWatch);
		this.fateWatch = new FateWatchFeature();
		this.features.Add(this.fateWatch);
		this.fcBuffs = new FcBuffsFeature();
		this.features.Add(this.fcBuffs);
		var drawSheathe = new DrawSheatheFeature();
		this.features.Add(drawSheathe);
		var emoteQuiet = new EmoteQuietFeature();
		this.features.Add(emoteQuiet);
		var ifMouseover = new IfMouseoverFeature();
		this.features.Add(ifMouseover);
		var marks = new EphemeralMarksFeature();
		this.features.Add(marks);

		var itemUse = new Features.ItemUse.ItemUseFeature();
		this.features.Add(itemUse);
		var macroIcons = new Features.MacroIcons.MacroIconFeature();
		this.features.Add(macroIcons);

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
		var chatColours = new Features.ChatColours.ChatColourFeature();
		this.features.Add(chatColours);

		var earshot = new Features.Earshot.EarshotFeature();
		this.features.Add(earshot);

		var food = new Features.Food.FoodFeature();
		this.features.Add(food);

		this.fanfare = new Features.Fanfare.FanfareFeature();
		this.features.Add(this.fanfare);
		this.fanfare.AddWindowsTo(this.windows);

		var interact = new InteractFeature();
		this.features.Add(interact);
		this.interact = interact;
		this.keybinds.Register("interact", "Interact", () => Config.InteractKey, interact.Press);
		this.keybinds.Register("drawsheathe", "Draw / sheathe", () => Config.DrawSheatheKey, drawSheathe.Press,
			repeats: false);
		this.keybinds.Register("openwindow", "Open this window", () => Config.OpenWindowKey, () => OpenWindow(),
			repeats: false);

		static Action<bool> Save(Action<bool> set) => v => { set(v); Config.Save(); };

		var entries = new List<TabEntry> {
			new TabEntry(castWatch.TabTitle, castWatch.Summary, castWatch.DrawTab) {
				Domains = [Domains.Information], Icon = FontAwesomeIcon.Crosshairs,
			},
			new TabEntry(ifMouseover.SectionTitle, ifMouseover.Summary, ifMouseover.DrawSection) {
				Domains = [Domains.Information], Icon = FontAwesomeIcon.MousePointer,
			},
			new TabEntry(itemUse.SectionTitle, itemUse.Summary, itemUse.DrawSection) {
				Domains = [Domains.Information], Icon = FontAwesomeIcon.Prescription,
			},
			new TabEntry(macroIcons.TabTitle, macroIcons.Summary, macroIcons.DrawTab) {
				Diagnostics = macroIcons.DrawDiagnostics,
				Domains = [Domains.Macros, Domains.Visual], Icon = FontAwesomeIcon.Image,

				Get = () => Config.MacroItemIcons || Config.MacroNameIcons,
				Set = Save(v => { Config.MacroItemIcons = v; Config.MacroNameIcons = v; }),
			},
			new TabEntry(this.fateWatch.TabTitle, this.fateWatch.Summary, this.fateWatch.DrawTab) {
				Diagnostics = this.fateWatch.DrawDiagnostics,
				Domains = [Domains.Alerts], Icon = FontAwesomeIcon.Bell,
				Get = () => Config.FateWatchEnabled, Set = Save(v => Config.FateWatchEnabled = v),
			},
			new TabEntry(this.fcBuffs.TabTitle, this.fcBuffs.Summary, this.fcBuffs.DrawTab) {
				Diagnostics = this.fcBuffs.DrawDiagnostics,
				Domains = [Domains.Alerts, Domains.Automation], Icon = FontAwesomeIcon.Landmark,
				Get = () => Config.FcBuffsEnabled, Set = Save(v => Config.FcBuffsEnabled = v),
			},

			new TabEntry(drawSheathe.TabTitle, drawSheathe.Summary, null) {
				Diagnostics = drawSheathe.DrawDiagnostics,
				Domains = [Domains.Qol], Icon = FontAwesomeIcon.Bolt,

				BindName = "drawsheathe", Bind = () => Config.DrawSheatheKey, BindRepeats = false,
			},
			new TabEntry(emoteQuiet.TabTitle, emoteQuiet.Summary, emoteQuiet.DrawTab) {
				Diagnostics = emoteQuiet.DrawDiagnostics,
				Domains = [Domains.Chat, Domains.Qol], Icon = FontAwesomeIcon.CommentDots,
				Get = () => Config.EmoteQuietEnabled, Set = Save(v => Config.EmoteQuietEnabled = v),
			},
			new TabEntry(interact.TabTitle, interact.Summary, interact.DrawTab) {
				Diagnostics = interact.DrawDiagnostics,
				Domains = [Domains.Qol], Icon = FontAwesomeIcon.HandPointer,
				BindName = "interact", Bind = () => Config.InteractKey, BindRepeats = true,
			},
			new TabEntry(marks.TabTitle, marks.Summary, marks.DrawTab) {
				Domains = [Domains.Visual, Domains.Qol], Icon = FontAwesomeIcon.Star,
				Get = () => Config.MarksEnabled, Set = Save(v => Config.MarksEnabled = v),
			},
			new TabEntry(debuffs.TabTitle, debuffs.Summary, debuffs.DrawTab) {
				Diagnostics = debuffs.DrawDiagnostics,
				Domains = [Domains.Visual], Icon = FontAwesomeIcon.Skull,
				Get = () => Config.DebuffMarksEnabled, Set = Save(v => Config.DebuffMarksEnabled = v),
			},
			new TabEntry(meldWindow.TabTitle, meldWindow.Summary, meldWindow.DrawTab) {
				Domains = [Domains.Qol], Icon = FontAwesomeIcon.Gem,

				Get = () => MeldWindowKeeper.AnyEnabled, Set = MeldWindowKeeper.SetEnabled,
			},
			new TabEntry(repairs.TabTitle, repairs.Summary, repairs.DrawTab) {
				Domains = [Domains.Automation, Domains.Qol], Icon = FontAwesomeIcon.Hammer,
				Get = () => Config.RepairAutoAccept, Set = Save(v => Config.RepairAutoAccept = v),
			},
			new TabEntry(dresser.TabTitle, dresser.Summary, dresser.DrawTab) {
				Domains = [Domains.Automation, Domains.Qol], Icon = FontAwesomeIcon.Tshirt,

				Get = () => Features.Dresser.DresserFeature.AnyEnabled,
				Set = Features.Dresser.DresserFeature.SetEnabled,
			},
			new TabEntry(partyJobs.TabTitle, partyJobs.Summary, partyJobs.DrawTab) {
				Diagnostics = partyJobs.DrawDiagnostics,
				Domains = [Domains.Visual, Domains.Qol], Icon = FontAwesomeIcon.UserFriends,
				Get = () => Config.PartyJobsEnabled, Set = Save(v => Config.PartyJobsEnabled = v),
			},
			new TabEntry(chatColours.TabTitle, chatColours.Summary, chatColours.DrawTab) {
				Diagnostics = chatColours.DrawDiagnostics,
				Domains = [Domains.Chat, Domains.Visual], Icon = FontAwesomeIcon.Palette,
				Get = () => Config.ChatColours, Set = Save(v => Config.ChatColours = v),
			},
			new TabEntry(food.TabTitle, food.Summary, food.DrawTab) {
				Diagnostics = food.DrawDiagnostics,
				Domains = [Domains.Alerts, Domains.Qol], Icon = FontAwesomeIcon.Utensils,
				Get = () => Config.FoodEnabled, Set = Save(v => Config.FoodEnabled = v),
			},
			new TabEntry(earshot.TabTitle, earshot.Summary, earshot.DrawTab) {
				Domains = [Domains.Alerts, Domains.Chat], Icon = FontAwesomeIcon.Bullhorn,
				Get = () => Config.EarshotEnabled, Set = Save(v => Config.EarshotEnabled = v),
			},
			new TabEntry(this.fanfare.TabTitle, this.fanfare.Summary, this.fanfare.DrawTab) {
				Diagnostics = this.fanfare.DrawDiagnostics,
				Domains = [Domains.Alerts, Domains.Visual], Icon = FontAwesomeIcon.Trophy,
				Get = () => Config.Fanfare.Enabled,

				Set = Save(v => {
					Config.Fanfare.Enabled = v;
					this.fanfare.OnEnabledChanged(v);
				}),
			},

			new TabEntry(Input.KeybindsTab.TabTitle, Input.KeybindsTab.Summary,
					() => Input.KeybindsTab.Draw(this.keybinds)) {
				Icon = FontAwesomeIcon.Keyboard,
			},
		};

		Chrome.Attach(this.fonts);
		this.optionsWindow = new OptionsWindow(
			[

				new DomainEntry(Domains.All, FontAwesomeIcon.LayerGroup, ShowsEverything: true),
				new DomainEntry(Domains.Qol, FontAwesomeIcon.Feather),
				new DomainEntry(Domains.Visual, FontAwesomeIcon.Eye),
				new DomainEntry(Domains.Macros, FontAwesomeIcon.Terminal),
				new DomainEntry(Domains.Automation, FontAwesomeIcon.Robot),
				new DomainEntry(Domains.Alerts, FontAwesomeIcon.Bell),
				new DomainEntry(Domains.Chat, FontAwesomeIcon.Comments),
			],
			[
				new DomainEntry(Domains.Information, FontAwesomeIcon.BookOpen),
			],
			entries, this.fonts, owned => Input.KeybindsTab.Draw(this.keybinds, owned));
		this.windows.AddWindow(this.optionsWindow);

		PluginInterface.UiBuilder.Draw += this.windows.Draw;
		PluginInterface.UiBuilder.Draw += SampleImGuiState;
		PluginInterface.UiBuilder.Draw += () => this.dresserOverlay?.Draw();
		PluginInterface.UiBuilder.Draw += this.fanfare.DrawFileDialogs;
		PluginInterface.UiBuilder.OpenMainUi += this.OpenMain;
		PluginInterface.UiBuilder.OpenConfigUi += this.OpenMain;

		OpenWindow = this.OpenMain;
		Framework.Update += this.OnFrameworkUpdate;

		Plugin.RegisterSub("dresser", "Report what your glamour dresser could pack away.", (_, arg) => {
			var a = arg.Trim();

			if (a.Equals("rows", StringComparison.OrdinalIgnoreCase)) {
				Features.Dresser.DresserList.DumpRows();
				return;
			}

			if (a.Equals("probe", StringComparison.OrdinalIgnoreCase)) {
				Features.Dresser.DresserProbe.ArmTooltipDump();
				return;
			}

			if (a.StartsWith("colours", StringComparison.OrdinalIgnoreCase)
			    || a.StartsWith("colors", StringComparison.OrdinalIgnoreCase)) {
				var rest = a[(a.IndexOf(' ') + 1)..].Trim();
				if (!int.TryParse(rest, out var start) || a.IndexOf(' ') < 0) start = 1;
				Features.Dresser.DresserProbe.Colours(start, 30);
				return;
			}

			if (a.StartsWith("glyphs", StringComparison.OrdinalIgnoreCase)) {

				var rest = a["glyphs".Length..].Trim();
				var from = 0xE000;
				if (rest.Length > 0)
					int.TryParse(rest, System.Globalization.NumberStyles.HexNumber, null, out from);

				Features.Dresser.DresserProbe.Glyphs(from, 64);
				return;
			}

			dresser.Run();
		});

		Commands.AddHandler("/dsufanfare", new CommandInfo((_, arg) => this.fanfare.OnCommand(arg)) {
			HelpMessage = "This session's achievements. 'test' previews a card.",
		});

		Commands.AddHandler("/dsu", new CommandInfo(this.OnPluginCommand) {
			HelpMessage = "Open the utilities window. /dsu help lists the rest.",
		});
	}

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

	private void OpenMain() => this.optionsWindow.IsOpen = true;

	internal static Action OpenWindow { get; private set; } = () => { };

	private static readonly Dictionary<string, (string Help, Action<string, string> Run)> SubCommands
		= new(StringComparer.OrdinalIgnoreCase);

	internal static void RegisterSub(string name, string help, Action<string, string> run)
		=> SubCommands[name] = (help, run);

	private void OnPluginCommand(string command, string arguments) {
		var args = arguments.Trim();

		if (args.Length == 0) {
			this.optionsWindow.Toggle();
			return;
		}

		var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
		var name = parts[0];
		var rest = parts.Length > 1 ? parts[1] : string.Empty;

		switch (name.ToLowerInvariant()) {
			case "debug" or "diag" or "verbose":
				Verbose = !Verbose;
				Chat.Print($"[DeserokUtils] diagnostics {(Verbose ? "ON" : "off")} (Debug channel).");
				return;

			case "help" or "?" or "commands":
				PrintSubCommands();
				return;
		}

		if (SubCommands.TryGetValue(name, out var sub)) {
			sub.Run($"/dsu {name}", rest);
			return;
		}

		Chat.PrintError($"[DeserokUtils] no such option '{name}'. Try /dsu help.");
	}

	private static void PrintSubCommands() {
		Chat.Print("[DeserokUtils] /dsu opens the window. It also takes:");

		foreach (var (name, entry) in SubCommands.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
			Chat.Print($"  /dsu {name} - {entry.Help}");

		Chat.Print("[DeserokUtils] For macros:");
		Chat.Print("  /watch, /ifwatch - fire a line only if a watched action went off.");
		Chat.Print("  /ifmo - run a line against the first target it would actually land on.");
		Chat.Print("  /dsuitem - use an item on a target.");
		Chat.Print("  /fcbuffs - refresh a Free Company buff now.");
		Chat.Print("  /dsufanfare - this session's achievements.");
	}

	internal static void Diag(string message) {
		if (!Verbose)
			return;
		Chat.Print(new XivChatEntry {
			Type = XivChatType.Debug,
			Name = "DeserokUtils",
			Message = message,
		});
	}

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
		Commands.RemoveHandler("/dsu");
		Commands.RemoveHandler("/dsufanfare");

		PluginInterface.UiBuilder.Draw -= this.windows.Draw;
		PluginInterface.UiBuilder.Draw -= SampleImGuiState;
		PluginInterface.UiBuilder.Draw -= this.fanfare.DrawFileDialogs;
		PluginInterface.UiBuilder.OpenMainUi -= this.OpenMain;
		PluginInterface.UiBuilder.OpenConfigUi -= this.OpenMain;
		this.windows.RemoveAllWindows();

		this.fonts.Dispose();

		this.dresser?.Dispose();
		this.dresserTooltip?.Dispose();

		foreach (var feature in this.features)
			feature.Dispose();
	}
}
