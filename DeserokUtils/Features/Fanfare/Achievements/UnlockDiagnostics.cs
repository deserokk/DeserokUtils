using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Fanfare.Achievements;

internal sealed unsafe class UnlockDiagnostics: IDisposable {

	private static readonly TimeSpan SuppressionGrace = TimeSpan.FromSeconds(2);

	private readonly IFramework framework;
	private readonly PopupSuppressor suppressor;
	private readonly StreamWriter? file;

	private DateTime? awaitingSuppression;
	private uint awaitingFor;

	internal string LogPath { get; }

	internal UnlockDiagnostics(IFramework framework, PopupSuppressor suppressor) {
		this.framework = framework;
		this.suppressor = suppressor;

		this.LogPath = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "recorder.log");
		try {
			Directory.CreateDirectory(Plugin.PluginInterface.ConfigDirectory.FullName);

			this.file = new StreamWriter(this.LogPath, append: true) { AutoFlush = true };
			this.file.WriteLine($"\n=== session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
		} catch (Exception ex) {
			Plugin.Log.Error(ex, "diagnostics: could not open the log; dalamud.log only.");
		}

		Plugin.Chat.ChatMessage += this.OnChatMessage;
	}

	private const int MaxLines = 5000;

	private int written;

	internal void Record(string line) {
		Plugin.Log.Information($"fanfare:{line}");

		if (this.written > MaxLines)
			return;

		try {
			if (++this.written > MaxLines) {
				this.file?.WriteLine(
					$"{DateTime.Now:HH:mm:ss.fff} === {MaxLines} line cap reached; nothing further this session ===");
				return;
			}

			this.file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff}{line}");
		} catch (Exception ex) {
			Plugin.Log.Error(ex, "diagnostics: write failed");
		}
	}

	internal void OnUnlocked(uint achievementId) {
		this.Record($" unlocked {achievementId}");

		if (!Plugin.Config.Fanfare.SuppressDefaultPopup)
			return;

		this.awaitingFor = achievementId;
		this.awaitingSuppression = DateTime.UtcNow;
		this.framework.Update += this.CheckSuppression;
	}

	private void CheckSuppression(IFramework _) {
		if (this.awaitingSuppression is not DateTime since) {
			this.framework.Update -= this.CheckSuppression;
			return;
		}

		if (this.suppressor.LastSuppressedAt > since) {
			this.Record($" suppression confirmed for {this.awaitingFor}");
			this.Finish();
			return;
		}

		if (DateTime.UtcNow - since < SuppressionGrace)
			return;

		this.Record($" SUPPRESSION DID NOT FIRE for {this.awaitingFor} -- addon may have been renamed");
		Plugin.Chat.PrintError(
			"[Fanfare] the game's own achievement popup was not hidden. A patch may have renamed it. " +
			"Run /fanfare addons achieve to find the new name.");

		this.Finish();
	}

	private void Finish() {
		this.awaitingSuppression = null;
		this.framework.Update -= this.CheckSuppression;
	}

	private void OnChatMessage(IHandleableChatMessage message) {

		if (message.LogKind != XivChatType.Progress)
			return;

		var text = message.Message.TextValue;

		if (text.StartsWith("[Fanfare]", StringComparison.Ordinal))
			return;

		if (text.IndexOf("achievement", StringComparison.OrdinalIgnoreCase) < 0)
			return;

		this.Record($" CHAT [{message.LogKind}] {text}");
	}

	internal static void ListLoaded(string? filter) {
		var stage = AtkStage.Instance();
		if (stage is null || stage->RaptureAtkUnitManager is null) {
			Plugin.Chat.PrintError("[Fanfare] the UI manager is not available right now.");
			return;
		}

		var names = new List<string>();
		foreach (var entry in stage->RaptureAtkUnitManager->AllLoadedUnitsList.Entries) {
			var unit = entry.Value;
			if (unit is null)
				continue;

			var name = unit->NameString;
			if (string.IsNullOrEmpty(name))
				continue;
			if (!string.IsNullOrWhiteSpace(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
				continue;

			names.Add(name);
		}

		names.Sort(StringComparer.OrdinalIgnoreCase);
		Plugin.Log.Information($"addons: {names.Count} loaded -- {string.Join(", ", names)}");
		Plugin.Chat.Print($"[Fanfare] {names.Count} addons loaded" +
			(string.IsNullOrWhiteSpace(filter) ? " (full list in dalamud.log)." : $" matching '{filter}':"));

		foreach (var name in names.Take(string.IsNullOrWhiteSpace(filter) ? 0 : 25))
			Plugin.Chat.Print($"   {name}");
	}

	public void Dispose() {
		this.framework.Update -= this.CheckSuppression;
		Plugin.Chat.ChatMessage -= this.OnChatMessage;

		this.file?.Flush();
		this.file?.Dispose();
	}
}
