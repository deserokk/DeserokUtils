using System;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

namespace DeserokUtils.Features.EmoteQuiet;

internal sealed class EmoteQuietFeature: IDisposable {
	public string TabTitle => "EmoteQuiet";

	public string Summary => "Prevents emote spam while the game's own log messages stay on, for a configurable amount of time.";

	private readonly EmoteInterceptor interceptor = new();
	private readonly IncomingEmoteFilter incoming = new();
	private string newFamily = string.Empty;

	public EmoteQuietFeature() {
		Plugin.RegisterSub("emotequiet", "Toggle announcing an emote once, then hiding its repeats.", this.OnCommand);

		this.incoming.Sync();
	}

	private void OnCommand(string command, string arguments) {
		string arg = arguments.Trim().ToLowerInvariant();

		switch (arg) {
			case "on" or "off" or "toggle":
				Plugin.Config.EmoteQuietEnabled = arg switch {
					"on" => true,
					"off" => false,
					_ => !Plugin.Config.EmoteQuietEnabled,
				};
				Plugin.Config.Save();
				this.interceptor.Sync();
				Plugin.Chat.Print($"[EmoteQuiet] {(Plugin.Config.EmoteQuietEnabled ? "ON" : "off")}."
					+ (Plugin.Config.EmoteQuietEnabled
						? " Make sure \"Display log message\" is ticked in the emote window, or there is nothing to suppress."
						: ""));
				return;

			case "reset" or "clear":
				this.interceptor.Reset();
				this.incoming.Reset();
				return;

			case "others":
				Plugin.Config.EmoteQuietIncomingEnabled = !Plugin.Config.EmoteQuietIncomingEnabled;
				Plugin.Config.Save();
				this.incoming.Sync();
				Plugin.Chat.Print($"[EmoteQuiet] hiding other people's repeats: "
					+ $"{(Plugin.Config.EmoteQuietIncomingEnabled ? "ON" : "off")}.");
				return;

			case "":
				this.ReportState();
				return;
		}

		if (arg.StartsWith("sniff", StringComparison.Ordinal)) {
			this.OnSniff(arg["sniff".Length..].Trim());
			return;
		}

		Plugin.Chat.PrintError($"[EmoteQuiet] unknown argument \"{arg}\". Use on, off, others, reset, or sniff.");
	}

	private void ReportState() {
		if (!this.interceptor.Available) {
			Plugin.Chat.PrintError("[EmoteQuiet] EmoteManager.ExecuteEmote could not be resolved -- suppression cannot work. See /xllog.");
			return;
		}

		int active = this.interceptor.ActiveWindows().Count();
		Plugin.Chat.Print(
			$"[EmoteQuiet] {(Plugin.Config.EmoteQuietEnabled ? "ON" : "off")}, "
			+ $"window {Plugin.Config.EmoteQuietWindowSeconds}s, "
			+ $"{active} emote(s) currently quiet. "
			+ $"Others' repeats: {(Plugin.Config.EmoteQuietIncomingEnabled ? $"hidden ({this.incoming.SuppressedCount} so far)" : "shown")}."
			+ (this.interceptor.Sniffing ? $" Recording, {this.interceptor.SniffRemaining.TotalSeconds:0}s left." : ""));
	}

	private void OnSniff(string rest) {
		if (rest is "off" or "stop") {
			if (this.interceptor.Sniffing)
				this.interceptor.StopSniffing();
			else
				Plugin.Chat.Print("[EmoteQuiet] the recorder was not running.");
			return;
		}

		if (!this.interceptor.Available) {
			Plugin.Chat.PrintError("[EmoteQuiet] EmoteManager.ExecuteEmote could not be resolved -- nothing to hook. See /xllog.");
			return;
		}

		TimeSpan duration = EmoteInterceptor.DefaultSniffDuration;
		if (rest.Length > 0) {
			if (!int.TryParse(rest, out int seconds) || seconds <= 0) {
				Plugin.Chat.PrintError($"[EmoteQuiet] \"{rest}\" is not a number of seconds. Try /emotequiet sniff 120.");
				return;
			}
			duration = TimeSpan.FromSeconds(seconds);
		}

		this.interceptor.StartSniffing(duration);
		Plugin.Chat.Print($"[EmoteQuiet] recording for {duration.TotalSeconds:0}s -- every emote prints what it was passed and what was done to it.");
	}

	public void DrawTab() {
		this.interceptor.Sync();

		if (!this.interceptor.Available) {
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
				"EmoteManager.ExecuteEmote could not be resolved. Suppression cannot work. See /xllog.");
			return;
		}

		bool enabled = Plugin.Config.EmoteQuietEnabled;
		if (ImGui.Checkbox("Suppress outgoing emote log messages", ref enabled)) {
			Plugin.Config.EmoteQuietEnabled = enabled;
			Plugin.Config.Save();
			this.interceptor.Sync();
		}

		bool others = Plugin.Config.EmoteQuietIncomingEnabled;
		if (ImGui.Checkbox("Hide repeats from other players", ref others)) {
			Plugin.Config.EmoteQuietIncomingEnabled = others;
			Plugin.Config.Save();
			this.incoming.Sync();
		}

		ImGui.Spacing();
		int window = Plugin.Config.EmoteQuietWindowSeconds;
		ImGui.SetNextItemWidth(160f);
		if (ImGui.InputInt("seconds of quiet, per emote##eq_window", ref window)) {
			Plugin.Config.EmoteQuietWindowSeconds = Math.Clamp(window, 1, 3600);
			Plugin.Config.Save();
		}
	}

	public void DrawDiagnostics() {
		this.interceptor.Sync();

		Section("Currently quiet");
		var active = this.interceptor.ActiveWindows().OrderByDescending(a => a.Remaining).ToList();
		if (active.Count == 0) {
			ImGui.TextDisabled("nothing -- the next use of any emote will announce");
		}
		else {
			foreach (var (label, remaining) in active)
				ImGui.BulletText($"{label} -- quiet for another {remaining.TotalSeconds:0}s");
		}

		ImGui.Spacing();
		if (ImGui.Button("Clear timers##eq_reset"))
			this.interceptor.Reset();
		ImGui.SameLine();
		ImGui.TextDisabled("/emotequiet reset");

		if (Plugin.Config.EmoteQuietIncomingEnabled) {
			Section("Incoming");
			int watching = this.incoming.ActiveWindows().Count();
			ImGui.TextDisabled(
				$"hidden so far: {this.incoming.SuppressedCount}  ·  {watching} line(s) currently quiet");
		}

		Section("Recorder");
		if (this.interceptor.Sniffing) {
			ImGui.TextColored(
				new Vector4(0.4f, 1f, 0.4f, 1f),
				$"recording -- {this.interceptor.SniffRemaining.TotalSeconds:0}s left");
			ImGui.SameLine();
			if (ImGui.Button("Stop##eq_sniff"))
				this.interceptor.StopSniffing();
		}
		else {
			if (ImGui.Button("Record for 90s##eq_sniff"))
				this.interceptor.StartSniffing(EmoteInterceptor.DefaultSniffDuration);
			ImGui.SameLine();
			ImGui.TextDisabled("/emotequiet sniff");
		}
	}

	private static void Section(string title) {
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextDisabled(title);
		ImGui.Spacing();
	}

	public void Dispose() {
		this.interceptor.Dispose();
		this.incoming.Dispose();
	}
}
