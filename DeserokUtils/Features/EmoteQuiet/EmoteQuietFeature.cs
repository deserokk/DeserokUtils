using System;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

namespace DeserokUtils.Features.EmoteQuiet;

/// <summary>
/// Announce an emote the first time, then stay quiet about repeats of that same emote for a while.
///
/// ## The problem
///
/// The emote log message is spam in a crowd -- a bard performance, a gathering -- so the game's
/// "Display log message" checkbox gets switched off, and then nobody ever knows you clapped at all.
/// The only way to have both today is tick the box, clap once, untick it. What is actually wanted,
/// in deserok's words, is *"display the first of a given emote, then hide log messages for the same
/// emote within 1 minute"*.
///
/// ⭐ The game already has the exact per-emote switch for this and it is what `motion` sets. See
/// <see cref="EmoteInterceptor"/> for the recording that proved it, and for the two things that
/// recording caught which reasoning would not have.
/// </summary>
internal sealed class EmoteQuietFeature: IDisposable {
	public string TabTitle => "EmoteQuiet";

	private readonly EmoteInterceptor interceptor = new();

	public EmoteQuietFeature() {
		Plugin.Commands.AddHandler("/emotequiet", new CommandInfo(this.OnCommand) {
			HelpMessage = "/emotequiet -- announce an emote once, then stay quiet about repeats. 'on'/'off', 'reset', or 'sniff' to record what the game passes.",
		});
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
				return;

			case "":
				this.ReportState();
				return;
		}

		if (arg.StartsWith("sniff", StringComparison.Ordinal)) {
			this.OnSniff(arg["sniff".Length..].Trim());
			return;
		}

		Plugin.Chat.PrintError($"[EmoteQuiet] unknown argument \"{arg}\". Use on, off, reset, or sniff.");
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
			+ $"{active} emote(s) currently quiet."
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

	// ── the tab ──────────────────────────────────────────────────────────────────────────────

	public void DrawTab() {
		// Cheapest place to notice a recording has aged out; no per-frame callback for an egg timer.
		this.interceptor.Sync();

		ImGui.TextWrapped(
			"Announce an emote the first time, then stay quiet about that same emote for a while. So "
			+ "clapping through a whole bard set says you clapped once, instead of fifty times, "
			+ "without turning the log message off and losing it entirely.");
		ImGui.Spacing();

		if (!this.interceptor.Available) {
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
				"EmoteManager.ExecuteEmote could not be resolved. Suppression cannot work. See /xllog.");
			return;
		}

		Section("Settings");
		bool enabled = Plugin.Config.EmoteQuietEnabled;
		if (ImGui.Checkbox("Suppress repeated emote log messages", ref enabled)) {
			Plugin.Config.EmoteQuietEnabled = enabled;
			Plugin.Config.Save();
			this.interceptor.Sync();
		}

		ImGui.TextWrapped(
			"⚠ This one changes what OTHER people see, which nothing else in this plugin does. That is "
			+ "why it ships off rather than on.");
		ImGui.Spacing();
		ImGui.TextWrapped(
			"⚠ It needs the game's own \"Display log message\" TICKED in the emote window. That box is "
			+ "the thing this exists to let you leave on -- with it off there is no message to "
			+ "suppress, and this will look broken while working perfectly.");

		ImGui.Spacing();
		int window = Plugin.Config.EmoteQuietWindowSeconds;
		ImGui.SetNextItemWidth(160f);
		if (ImGui.InputInt("seconds of quiet, per emote##eq_window", ref window)) {
			Plugin.Config.EmoteQuietWindowSeconds = Math.Clamp(window, 1, 3600);
			Plugin.Config.Save();
		}
		ImGui.TextWrapped(
			"Per emote, not global -- clapping never silences your next /dote. The clock starts when a "
			+ "message actually goes out, so typing \"/clap motion\" yourself does not start it.");

		Section("Currently quiet");
		var active = this.interceptor.ActiveWindows().OrderByDescending(a => a.Remaining).ToList();
		if (active.Count == 0) {
			ImGui.TextDisabled("nothing -- the next use of any emote will announce");
		}
		else {
			foreach (var (id, remaining) in active)
				ImGui.BulletText($"{EmoteInterceptor.Name(id)} -- quiet for another {remaining.TotalSeconds:0}s");
		}
		ImGui.Spacing();
		if (ImGui.Button("Clear timers##eq_reset"))
			this.interceptor.Reset();
		ImGui.SameLine();
		ImGui.TextDisabled("/emotequiet reset");

		Section("Recorder");
		ImGui.TextWrapped(
			"Prints what the game passed for each emote and what was done to it. This is how the "
			+ "feature was built -- the flag was measured, not assumed.");
		ImGui.Spacing();
		if (this.interceptor.Sniffing) {
			ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"recording -- {this.interceptor.SniffRemaining.TotalSeconds:0}s left");
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

		Section("The one thing this cannot check itself");
		ImGui.TextWrapped(
			"Whether other people stop seeing the message. Nothing inside your own client can read "
			+ "somebody else's chat log. The evidence is strong -- \"/clap\" and \"/clap motion\" reach "
			+ "the game's emote function differing in exactly this one bit and nothing else, and "
			+ "\"motion\" is known to silence it for everyone -- but the acceptance test is a person "
			+ "standing next to you while you clap five times, saying whether they saw one or five.");
	}

	/// <summary>ImGui.SeparatorText does not exist in this binding version; this is the stand-in.</summary>
	private static void Section(string title) {
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextDisabled(title);
		ImGui.Spacing();
	}

	public void Dispose() {
		Plugin.Commands.RemoveHandler("/emotequiet");
		this.interceptor.Dispose();
	}
}
