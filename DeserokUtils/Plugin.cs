using System;
using System.Collections.Generic;

using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using DeserokUtils.Features.CastWatch;
using DeserokUtils.UI;

namespace DeserokUtils;

/// <summary>
/// A bucket for small utilities. This file is the SHELL only -- services, the window, and the
/// plugin-level command. Every feature owns a folder under Features/, registers its own commands,
/// and contributes one tab. Nothing feature-specific belongs here, so adding the second utility
/// never means editing the first one's code.
/// </summary>
public sealed class Plugin: IDalamudPlugin {
	public string Name => "DeserokUtils";

	internal static ICommandManager Commands { get; private set; } = null!;
	internal static IChatGui Chat { get; private set; } = null!;
	internal static IObjectTable Objects { get; private set; } = null!;
	internal static IDataManager Data { get; private set; } = null!;
	internal static ITargetManager Targets { get; private set; } = null!;
	internal static IPartyList Party { get; private set; } = null!;
	internal static IGameInteropProvider Interop { get; private set; } = null!;
	internal static IPluginLog Log { get; private set; } = null!;

	/// <summary>
	/// Diagnostic output to the Debug chat channel. Shared across features, because "what did it
	/// see, and why did it decide that" is the same question every time.
	/// </summary>
	internal static bool Verbose { get; set; } = true;

	private readonly IDalamudPluginInterface pluginInterface;
	private readonly WindowSystem windows = new("DeserokUtils");
	private readonly MainWindow mainWindow;
	private readonly List<IDisposable> features = new();

	public Plugin(
		IDalamudPluginInterface pluginInterface,
		ICommandManager commands,
		IChatGui chat,
		IObjectTable objects,
		IDataManager data,
		ITargetManager targets,
		IPartyList party,
		IGameInteropProvider interop,
		IPluginLog log) {

		this.pluginInterface = pluginInterface;
		Commands = commands;
		Chat = chat;
		Objects = objects;
		Data = data;
		Targets = targets;
		Party = party;
		Interop = interop;
		Log = log;

		// ── features ─────────────────────────────────────────────────────────────────────────
		// Each registers its own commands in its constructor and hands back a tab. Adding a second
		// is two lines here plus one new folder. No interface yet on purpose: a contract written
		// against a single implementation is a guess about what the second one will need.
		var castWatch = new CastWatchFeature();
		this.features.Add(castWatch);

		this.mainWindow = new MainWindow([(castWatch.TabTitle, castWatch.DrawTab)]);
		this.windows.AddWindow(this.mainWindow);

		this.pluginInterface.UiBuilder.Draw += this.windows.Draw;
		this.pluginInterface.UiBuilder.OpenMainUi += this.OpenMain;
		this.pluginInterface.UiBuilder.OpenConfigUi += this.OpenMain;

		Commands.AddHandler("/deserokutils", new CommandInfo(this.OnPluginCommand) {
			HelpMessage = "/deserokutils -- open the utilities window. Add 'debug' to toggle diagnostics.",
		});
		Commands.AddHandler("/dsu", new CommandInfo(this.OnPluginCommand) {
			HelpMessage = "/dsu -- short form of /deserokutils.",
		});
	}

	private void OpenMain() => this.mainWindow.IsOpen = true;

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

	public void Dispose() {
		Commands.RemoveHandler("/deserokutils");
		Commands.RemoveHandler("/dsu");

		this.pluginInterface.UiBuilder.Draw -= this.windows.Draw;
		this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenMain;
		this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenMain;
		this.windows.RemoveAllWindows();

		foreach (var feature in this.features)
			feature.Dispose();
	}
}
