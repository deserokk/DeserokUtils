using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace DeserokUtils.Features.EmoteQuiet;

internal sealed unsafe class EmoteInterceptor: IDisposable {

	private delegate bool ExecuteEmoteDelegate(
		EmoteManager* self, ushort emoteId, EmoteController.PlayEmoteOption* option);

	private readonly Hook<ExecuteEmoteDelegate>? hook;

	private readonly Dictionary<string, DateTime> lastAnnounced = new();

	private readonly Dictionary<ushort, (string Key, string Label)> familyCache = new();

	private DateTime sniffUntil = DateTime.MinValue;
	private int sniffSeen;

	public static readonly TimeSpan DefaultSniffDuration = TimeSpan.FromSeconds(90);

	public bool Available => this.hook is not null;
	public bool Sniffing => DateTime.UtcNow < this.sniffUntil;
	public TimeSpan SniffRemaining => this.Sniffing ? this.sniffUntil - DateTime.UtcNow : TimeSpan.Zero;

	public EmoteInterceptor() {
		nint addr = (nint)EmoteManager.MemberFunctionPointers.ExecuteEmote;
		if (addr == nint.Zero) {

			Plugin.Log.Error("EmoteQuiet: could not resolve EmoteManager.ExecuteEmote. Suppression will not work.");
			return;
		}

		this.hook = Plugin.Interop.HookFromAddress<ExecuteEmoteDelegate>(addr, this.Detour);
		Plugin.Log.Information($"EmoteQuiet: resolved EmoteManager.ExecuteEmote at 0x{addr:X}");
		this.Sync();
	}

	public void Sync() {
		if (this.hook is null)
			return;

		bool wanted = Plugin.Config.EmoteQuietEnabled || this.Sniffing;
		if (wanted && !this.hook.IsEnabled)
			this.hook.Enable();
		else if (!wanted && this.hook.IsEnabled)
			this.hook.Disable();
	}

	public void StartSniffing(TimeSpan duration) {
		this.sniffUntil = DateTime.UtcNow + duration;
		this.sniffSeen = 0;
		this.Sync();
	}

	public void StopSniffing() {
		if (!this.Sniffing)
			return;
		this.sniffUntil = DateTime.MinValue;
		Plugin.Chat.Print($"[EmoteQuiet] recorder off. {this.sniffSeen} call(s) logged.");
		this.Sync();
	}

	public void Reset() {
		this.lastAnnounced.Clear();
		Plugin.Chat.Print("[EmoteQuiet] timers cleared -- the next use of each emote will announce.");
	}

	public IEnumerable<(string Label, TimeSpan Remaining)> ActiveWindows() {
		TimeSpan window = TimeSpan.FromSeconds(Math.Max(1, Plugin.Config.EmoteQuietWindowSeconds));
		DateTime now = DateTime.UtcNow;
		foreach (var (key, when) in this.lastAnnounced) {
			TimeSpan left = window - (now - when);
			if (left > TimeSpan.Zero)
				yield return (this.LabelFor(key), left);
		}
	}

	private string LabelFor(string key) {
		foreach (var (_, entry) in this.familyCache) {
			if (entry.Key == key)
				return entry.Label;
		}
		return key;
	}

	private (string Key, string Label) FamilyOf(ushort emoteId) {
		if (this.familyCache.TryGetValue(emoteId, out var cached))
			return cached;

		string name = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Emote>()
			?.GetRowOrDefault(emoteId)?.Name.ExtractText() ?? string.Empty;

		(string Key, string Label) result = ($"#{emoteId}", Name(emoteId));

		foreach (string prefix in Families
			.Where(p => !string.IsNullOrWhiteSpace(p))
			.OrderByDescending(p => p.Length)) {

			if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
				result = ($"family:{prefix.Trim().ToLowerInvariant()}", $"{prefix.Trim()} (all variants)");
				break;
			}
		}

		this.familyCache[emoteId] = result;
		return result;
	}

	private static readonly string[] Families = { "Cheer " };

	public void ForgetFamilies() => this.familyCache.Clear();

	private bool Detour(EmoteManager* self, ushort emoteId, EmoteController.PlayEmoteOption* option) {
		string before = string.Empty;
		string action = "untouched";

		try {
			if (this.Sniffing)
				before = Describe(option);

			action = this.Decide(emoteId, option);
		}
		catch (Exception ex) {

			Plugin.Log.Error(ex, "EmoteQuiet threw while deciding; the emote is passed through untouched.");
		}

		bool result = this.hook!.Original(self, emoteId, option);

		if (this.Sniffing) {
			this.sniffSeen++;
			string line = $"{Name(emoteId)} -> {result} | in: {before} | {action}";
			Plugin.Log.Information($"EmoteQuiet sniff: {line}");
			Plugin.Chat.Print($"[EmoteQuiet] {line}");
		}

		return result;
	}

	private string Decide(ushort emoteId, EmoteController.PlayEmoteOption* option) {
		if (!Plugin.Config.EmoteQuietEnabled)
			return "disabled";
		if (option is null)
			return "option was NULL -- passed through";
		if (option->DisableLogMessage)
			return "already silent (you asked for motion); timer not started";

		DateTime now = DateTime.UtcNow;
		var window = TimeSpan.FromSeconds(Math.Max(1, Plugin.Config.EmoteQuietWindowSeconds));
		(string key, string label) = this.FamilyOf(emoteId);

		if (this.lastAnnounced.TryGetValue(key, out DateTime last) && now - last < window) {

			option->DisableLogMessage = true;
			return $"SUPPRESSED as {label} (announced {(now - last).TotalSeconds:0.#}s ago)";
		}

		this.lastAnnounced[key] = now;
		return $"announced as {label}; quiet window started";
	}

	private static string Describe(EmoteController.PlayEmoteOption* option) =>
		option is null
			? "option=NULL"
			: $"Flags=0x{option->Flags:X2} DisableLogMessage={option->DisableLogMessage}";

	internal static string Name(ushort emoteId) {
		var sheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
		string name = sheet?.GetRowOrDefault(emoteId)?.Name.ExtractText() ?? string.Empty;
		return name.Length > 0 ? $"{name} ({emoteId})" : $"emote {emoteId}";
	}

	public void Dispose() => this.hook?.Dispose();
}
