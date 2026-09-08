using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;

using DeserokUtils.Features.Fanfare.Achievements;
using DeserokUtils.Features.Fanfare.Notify;

using Lumina.Excel.Sheets;

namespace DeserokUtils.Features.Fanfare;

internal sealed class FanfareFeature: IDisposable {
	public string TabTitle => "Achievement popups";

	public string Summary => "Shows a console-style card with a chime when you earn an achievement.";

	private readonly FileDialogManager fileDialogs = new();
	private readonly SoundPlayer sound = new();
	private readonly SoundLibrary library;
	private readonly NotifyWindow notify;
	private readonly FanfareTab tab;
	private readonly AchievementWatcher watcher;
	private readonly UnlockDiagnostics diagnostics;
	private readonly PopupSuppressor suppressor;
	private readonly Rarity rarity;
	private readonly Random random = new();

	private Notification? held;

	private readonly Queue<Notification> pending = new();

	private long nextCardAt;

	private bool cardWasUp;

	private static readonly TimeSpan CardGap = TimeSpan.FromSeconds(1);

	private const int MaxQueued = 10;

	private readonly List<SessionEntry> session = [];

	private const int MaxSessionLog = 50;

	private readonly record struct SessionEntry(
		DateTime When, string Title, float? PercentOwned, bool IsRare, string Reward);

	private bool cutsceneOptOut;

	private bool standaloneCached;

	private long standaloneCheckedAt;

	private bool StandaloneIsLoaded
		=> Environment.TickCount64 - this.standaloneCheckedAt < 1000
			? this.standaloneCached
			: this.Recheck();

	private bool Recheck() {
		this.standaloneCheckedAt = Environment.TickCount64;
		var wasLoaded = this.standaloneCached;

		this.standaloneCached = Plugin.PluginInterface.InstalledPlugins
			.Any(p => p.IsLoaded && string.Equals(p.InternalName, "Fanfare", StringComparison.Ordinal));

		if (this.standaloneCached != wasLoaded)
			Plugin.Log.Information(this.standaloneCached
				? "Fanfare: the standalone plugin is loaded, so the built-in copy is standing down."
				: "Fanfare: the standalone plugin is gone, so the built-in copy is taking over.");

		return this.standaloneCached;
	}

	public void OnEnabledChanged(bool enabled) {
		var blocked = this.Recheck();

		if (!enabled)
			this.pending.Clear();

		if (enabled && blocked)
			Plugin.Log.Information(
				"Fanfare: switched on, but the standalone plugin is loaded, so nothing will fire yet.");
	}

	public FanfareFeature() {

		_ = this.StandaloneIsLoaded;

		ImportStandaloneConfig();

		this.rarity = new Rarity();
		this.library = new SoundLibrary();
		this.suppressor = new PopupSuppressor(Plugin.AddonLifecycle);
		this.diagnostics = new UnlockDiagnostics(Plugin.Framework, this.suppressor);

		this.notify = new NotifyWindow();
		this.tab = new FanfareTab(
			this.Preview, this.SetHold, this.rarity, this.sound, this.library, this.fileDialogs);

		this.watcher = new AchievementWatcher(this.OnAchievementUnlocked);
		Plugin.ClientState.Login += this.watcher.Rearm;
		Plugin.Framework.Update += this.OnFramework;
	}

	public void AddWindowsTo(WindowSystem windows) => windows.AddWindow(this.notify);

	public void DrawFileDialogs() => this.fileDialogs.Draw();

	public void DrawTab() {
		if (!this.StandaloneIsLoaded) {
			this.tab.Draw();
			return;
		}

		ImGui.TextWrapped(
			"The separate Fanfare plugin is also running, so this copy is holding off to avoid two "
			+ "popups for one achievement. Disable or uninstall Fanfare and these settings unlock on "
			+ "their own — your old ones are already here.");
		ImGui.Separator();

		ImGui.BeginDisabled();
		this.tab.Draw();
		ImGui.EndDisabled();
	}

	public void DrawDiagnostics() {
		var verbose = Plugin.Config.Fanfare.Verbose;
		if (ImGui.Checkbox("Fanfare: log every unlock and preview", ref verbose)) {
			Plugin.Config.Fanfare.Verbose = verbose;
			Plugin.Config.Save();
		}

		ImGui.TextDisabled(this.rarity.Loaded
			? $"rarity: {this.rarity.Total:N0} achievements in the bundled snapshot"
			: "rarity: snapshot failed to load; everything counts as ordinary");

		if (this.StandaloneIsLoaded)
			ImGui.TextDisabled("standalone Fanfare is loaded; this copy is holding off");

	}

	private void OnFramework(Dalamud.Plugin.Services.IFramework framework) {
		var wanted = this.notify.IsPlaying;
		if (wanted != this.cutsceneOptOut) {
			this.cutsceneOptOut = wanted;
			Plugin.PluginInterface.UiBuilder.DisableCutsceneUiHide = wanted;
		}

		this.PumpQueue();
		this.DropHoldIfHidden();
	}

	private void PumpQueue() {
		if (this.notify.IsPlaying) {
			this.cardWasUp = true;
			return;
		}

		if (this.cardWasUp) {
			this.cardWasUp = false;
			this.nextCardAt = Environment.TickCount64 + (long)CardGap.TotalMilliseconds;
			return;
		}

		if (this.pending.Count == 0 || Environment.TickCount64 < this.nextCardAt)
			return;

		this.Show(this.pending.Dequeue());
	}

	private void Enqueue(Notification notification) {
		if (this.pending.Count >= MaxQueued) {
			Plugin.Log.Warning(
				$"Fanfare: {MaxQueued} cards already queued; dropping '{notification.Title}'.");
			return;
		}

		foreach (var waiting in this.pending) {
			if (string.Equals(waiting.Title, notification.Title, StringComparison.Ordinal))
				return;
		}

		this.pending.Enqueue(notification);
	}

	private void DropHoldIfHidden() {
		if (!this.notify.IsPlaying || this.tab.LastDrawn == 0)
			return;

		if (Environment.TickCount64 - this.tab.LastDrawn > 500)
			this.tab.DropHold();
	}

	private void OnAchievementUnlocked(uint achievementId) {

		if (!Plugin.Config.Fanfare.Enabled || this.Recheck())
			return;

		this.diagnostics.OnUnlocked(achievementId);

		var notification = Notification.FromAchievement(achievementId, this.rarity);
		if (notification is null) {
			Plugin.Log.Warning($"Fanfare: {achievementId} has no usable sheet row; no popup.");
			return;
		}

		Plugin.Log.Information(
			$"Fanfare: '{notification.Title}' rare={notification.IsRare} owned={notification.PercentOwned}");

		this.held = notification;
		this.Remember(notification);
		this.Enqueue(notification);
	}

	private void Remember(Notification notification) {

		if (this.session.Count >= MaxSessionLog)
			this.session.RemoveAt(0);

		this.session.Add(new SessionEntry(
			DateTime.Now,
			notification.Title,
			notification.PercentOwned,
			notification.IsRare,
			notification.HasRewardSlide
				? $"{notification.RewardLabel}: {notification.RewardName}"
				: string.Empty));
	}

	private void PrintSessionLog() {
		if (this.session.Count == 0) {
			Plugin.Chat.Print("[DSU] No achievements yet this session.");
			return;
		}

		Plugin.Chat.Print($"[DSU] Achievements this session ({this.session.Count}):");

		foreach (var entry in this.session) {
			var rarity = entry.PercentOwned is float percent
				? $"{percent:0.#}% of tracked players have this"
				: "rarity unknown";

			var rare = entry.IsRare ? " (rare)" : string.Empty;
			var reward = entry.Reward.Length > 0 ? $" - {entry.Reward}" : string.Empty;

			Plugin.Chat.Print($"  {entry.When:HH:mm}  {entry.Title}{rare} - {rarity}{reward}");
		}
	}

	private void Show(Notification notification) {
		var config = Plugin.Config.Fanfare;
		this.notify.Play(notification);

		var path = notification.IsRare
			? config.RareSoundFor(config.Style)
			: config.SoundFor(config.Style);

		if (!string.IsNullOrWhiteSpace(path))
			this.sound.Play(this.library.Resolve(path), config.SoundVolume);
	}

	public void OnCommand(string arguments) {
		var args = arguments.Trim();

		if (args.Length == 0) {
			this.PrintSessionLog();
			return;
		}

		var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
		switch (parts[0].ToLowerInvariant()) {
			case "test" or "preview":
				this.Preview(parts.Length > 1 ? parts[1].Trim().Trim('"') : null);
				break;

			case "addons":
				UnlockDiagnostics.ListLoaded(parts.Length > 1 ? parts[1].Trim() : null);
				break;

			case "log" or "session" or "recent":
				this.PrintSessionLog();
				break;

			case "todo" or "missing":
				AchievementQuery.ListIncomplete(parts.Length > 1 ? parts[1].Trim() : null, this.rarity);
				break;

			default:
				Plugin.Chat.PrintError($"[DSU] unknown option {parts[0]}. Try /dsufanfare test.");
				break;
		}
	}

	private void Preview(string? query) {
		var notification = this.PickPreview(query);
		if (notification is null)
			return;

		this.held = notification;

		this.Enqueue(notification);
	}

	private Notification? PickPreview(string? query) {
		var sheet = Plugin.Data.GetExcelSheet<Achievement>();

		Achievement? match = null;

		if (string.Equals(query, "rare", StringComparison.OrdinalIgnoreCase)) {

			var rareIds = this.rarity.IdsAtOrBelow(Plugin.Config.Fanfare.RareThreshold)
				.Where(id => sheet.TryGetRow(id, out var r) && !string.IsNullOrWhiteSpace(r.Name.ExtractText()))
				.ToList();

			if (rareIds.Count == 0) {
				Plugin.Chat.PrintError("[DSU] no achievements are below the rarity threshold.");
				return null;
			}

			sheet.TryGetRow(rareIds[this.random.Next(rareIds.Count)], out var rare);
			match = rare;
		} else if (string.IsNullOrWhiteSpace(query)) {

			var candidates = sheet
				.Where(a => a.Icon != 0 && !string.IsNullOrWhiteSpace(a.Name.ExtractText()))
				.ToList();
			if (candidates.Count > 0)
				match = candidates[this.random.Next(candidates.Count)];
		} else if (uint.TryParse(query, out var id) && sheet.TryGetRow(id, out var byId)) {
			match = byId;
		} else {
			match = sheet.FirstOrDefault(a =>
				a.Name.ExtractText().Contains(query, StringComparison.OrdinalIgnoreCase));
			if (match.Value.RowId == 0 && string.IsNullOrWhiteSpace(match.Value.Name.ExtractText()))
				match = null;
		}

		if (match is null) {
			Plugin.Chat.PrintError($"[DSU] no achievement matched {query}.");
			return null;
		}

		var notification = Notification.FromAchievement(match.Value.RowId, this.rarity);
		if (notification is null) {
			Plugin.Chat.PrintError($"[DSU] achievement {match.Value.RowId} has no usable name.");
			return null;
		}

		if (Plugin.Verbose)
			Plugin.Log.Information($"Fanfare preview: [{match.Value.RowId}] {notification.Title}");

		return notification;
	}

	private void SetHold(bool on) {
		if (!on) {
			this.notify.Stop();
			return;
		}

		this.held ??= this.PickPreview(null);
		if (this.held is null) {
			Plugin.Chat.PrintError("[DSU] could not build a preview card.");
			return;
		}

		this.notify.Hold(this.held);
	}

	private static void ImportStandaloneConfig() {
		var settings = Plugin.Config.Fanfare;
		if (settings.Imported)
			return;

		settings.Imported = true;

		try {
			var path = System.IO.Path.Combine(
				Plugin.PluginInterface.ConfigDirectory.Parent?.FullName ?? string.Empty,
				"Fanfare.json");

			if (!System.IO.File.Exists(path)) {
				Plugin.Config.Save();
				return;
			}

			var text = System.IO.File.ReadAllText(path);
			var old = Newtonsoft.Json.JsonConvert.DeserializeObject<FanfareSettings>(text);
			if (old is null) {
				Plugin.Config.Save();
				return;
			}

			old.Imported = true;

			old.Enabled = true;

			Plugin.Config.Fanfare = old;

			Plugin.Log.Information($"Fanfare: imported settings from {path}.");
		}
		catch (Exception ex) {

			Plugin.Log.Error(ex, "Fanfare: could not import the standalone settings.");
		}

		Plugin.Config.Save();
	}

	public void Dispose() {
		Plugin.Framework.Update -= this.OnFramework;
		Plugin.ClientState.Login -= this.watcher.Rearm;

		if (this.cutsceneOptOut)
			Plugin.PluginInterface.UiBuilder.DisableCutsceneUiHide = false;

		this.watcher.Dispose();
		this.diagnostics.Dispose();
		this.suppressor.Dispose();

		this.sound.Dispose();
	}
}
