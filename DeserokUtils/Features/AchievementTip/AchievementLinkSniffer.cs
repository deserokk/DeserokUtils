using System;
using System.Text;

using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.AchievementTip;

/// <summary>
/// ⚠⚠ RECONNAISSANCE, NOT A FEATURE. This exists to answer one question and should be deleted or
/// absorbed once it has: <b>what does the game actually hand us when you click an achievement link
/// in chat?</b>
///
/// ## The problem it is scouting
///
/// deserok, 2026-08-23: somebody near you earns an achievement, you want to know what it is, and the
/// game gives you nothing. Hovering does nothing. Clicking opens YOUR achievements at "Last Five" --
/// not the achievement that was linked, and not even reliably yours: *"if I earned 6? welp, time to
/// truffle hunt."* WoW has shown the description in a tooltip for fifteen years.
///
/// ## Why a click and not a hover
///
/// ⭐ deserok's call, and it collapses the hard half of the problem. A hover would mean hit-testing
/// the chat log ourselves; a CLICK is an event the game already routes -- it visibly does something
/// today -- so there is a function to hook and it receives the link.
///
/// ⭐⭐ <see cref="LogViewer.HandleLinkClick"/> is that function, and <see cref="LinkData"/> carries
/// more than hoped: the link type, several id fields, the raw payload bytes, AND the link's screen
/// rectangle. The rectangle means a hover version stays possible later -- the game is already
/// computing where every link sits.
///
/// ## ⚠ What is deliberately NOT assumed
///
/// Which field holds the achievement id. Dalamud's link-type enum has no achievement entry at all
/// (PlayerName=1, ItemLink=3, MapPositionLink=4, QuestLink=5, Status=9, ...), and the game opening a
/// generic "Last Five" page rather than the specific achievement is weak evidence that the id may not
/// be there to use. So this logs EVERY field and a hexdump, rather than reading the one that looks
/// right -- the same reason the interact recorder logged whole payloads instead of the argument
/// somebody expected to matter.
/// </summary>
internal sealed unsafe class AchievementLinkSniffer: IDisposable {
	private delegate void HandleLinkClickDelegate(LogViewer* self, LinkData* link);

	private readonly Hook<HandleLinkClickDelegate>? hook;
	private DateTime expiresAt = DateTime.MinValue;
	private int seen;

	public bool Available => this.hook is not null;
	public bool Armed { get; private set; }

	public AchievementLinkSniffer() {
		Plugin.Commands.AddHandler("/dsuachievesniff", new CommandInfo(this.OnCommand) {
			HelpMessage = "/dsuachievesniff [links] -- reconnaissance: record chat link clicks, or dump the links currently on screen.",
			ShowInHelp = false,
		});

		// ⭐⭐ ALWAYS ON, and it is the probe that should have been written first. Reading the message
		// as it ARRIVES sidesteps every rendering question -- visible or hidden, scrolled or not,
		// native chat or a replacement plugin. Three rounds were spent chasing which panel was showing
		// what, when the payload was available before any of that mattered.
		//
		// ⚠ Costs a walk of one message's payloads per chat line, and logs nothing unless something
		// unrecognised turns up. Chat volume is a few lines a second at worst.
		Plugin.Chat.ChatMessage += this.OnChatMessage;

		nint address = (nint)LogViewer.MemberFunctionPointers.HandleLinkClick;

		// ⭐ Resolved at construction even though nothing is enabled: a hook that CANNOT install must
		// say so at load, not at the moment somebody depends on it.
		if (address != nint.Zero) {
			this.hook = Plugin.Interop.HookFromAddress<HandleLinkClickDelegate>(address, this.Detour);
			Plugin.Log.Information($"AchievementTip: LogViewer.HandleLinkClick at 0x{address:X} (hook enabled only while recording).");
		}
		else {
			Plugin.Log.Warning("AchievementTip: could not resolve LogViewer.HandleLinkClick.");
		}
	}

	private void OnCommand(string command, string arguments) {
		if (arguments.Trim().StartsWith("links", StringComparison.OrdinalIgnoreCase)) {
			this.DumpLinks();
			return;
		}

		if (!this.Available) {
			Plugin.Chat.PrintError("[AchievementTip] the link hook did not resolve; nothing to record.");
			return;
		}

		if (this.Armed) {
			this.Disarm("stopped");
			return;
		}

		this.expiresAt = DateTime.UtcNow + TimeSpan.FromSeconds(60);
		this.seen = 0;
		this.Armed = true;
		this.hook!.Enable();
		Plugin.Chat.Print("[AchievementTip] recording chat link clicks for 60s. Click the achievement in chat.");
	}

	/// <summary>
	/// ⭐⭐ THE PROBE THAT MATTERS, and it needs no click at all.
	///
	/// <see cref="AtkTextNode"/> carries a <c>StdList&lt;LinkData&gt;</c> -- every link the node is
	/// currently showing, each with its own bounding box. So the game is already computing where every
	/// chat link sits, which is exactly what a hover needs and what I assumed we would have to
	/// reconstruct ourselves.
	///
	/// ⚠ Dumps the addon position and scale alongside, because the rectangles' coordinate space is
	/// unknown -- node-local, addon-local or screen. Comparing the two answers it in one reading
	/// instead of by trying three conversions and seeing which feels right.
	/// </summary>
	private void DumpLinks() {
		var report = new StringBuilder();
		int found = 0, panels = 0;

		// ⚠ ChatLogPanel_0 is the main tab; 1-3 are the detached ones. ChatLog itself holds the input
		// box and tabs, not the text, so it is not searched here.
		for (int panel = 0; panel < 4; panel++) {
			string name = $"ChatLogPanel_{panel}";
			var addon = (AddonChatLogPanel*)Plugin.GameGui.GetAddonByName(name).Address;
			if (addon is null)
				continue;

			panels++;
			var text = addon->ChatText;
			bool visible = addon->AtkUnitBase.IsVisible;

			// ⭐ Report the NEGATIVE cases explicitly. "No links found" has at least four causes --
			// addon absent, addon hidden, node absent, list empty -- and they call for completely
			// different next steps. Collapsing them into one message is how you end up debugging the
			// wrong one, which is the instrument-the-candidates-apart lesson from CastWatch.
			if (text is null) {
				report.Append($"{name}: visible={visible}, but ChatText is null. ");
				Plugin.Log.Information($"AchievementTip: {name} present, visible={visible}, ChatText null.");
				continue;
			}

			var list = text->LinkData;
			int here = 0;

			if (list is not null) {
				foreach (var entry in *list) {
					var link = entry.Value;
					if (link is null)
						continue;

					found++;
					if (++here > 40)
						break;

					Plugin.Log.Information(
						$"AchievementTip:   [{name}] type={link->LinkType} id={link->LinkId} "
						+ $"index={link->LinkIndex} group={link->LinkGroupId} "
						+ $"int1={link->IntValue1} uint1={link->UIntValue1} "
						+ $"int2={link->IntValue2} uint2={link->UIntValue2} "
						+ $"rect=({link->MinX},{link->MinY})-({link->MaxX},{link->MaxY})");
				}
			}

			report.Append($"{name}: visible={visible}, links={here}{(list is null ? " (no list)" : "")}. ");
			Plugin.Log.Information(
				$"AchievementTip: {name} visible={visible} at ({addon->AtkUnitBase.X},{addon->AtkUnitBase.Y}) "
				+ $"scale={addon->AtkUnitBase.Scale} links={here}");
		}

		if (panels == 0) {
			Plugin.Chat.PrintError(
				"[AchievementTip] no ChatLogPanel addon exists at all. That is the answer for a "
				+ "replacement chat plugin: there is no native chat log to read.");
			Plugin.Log.Information("AchievementTip: no ChatLogPanel_0..3 addon found.");
			return;
		}

		Plugin.Chat.Print($"[AchievementTip] {report}");
		Plugin.Chat.Print(found > 0
			? $"[AchievementTip] {found} link(s) dumped to the log."
			: "[AchievementTip] panels exist but hold no links -- scroll an item or achievement link into view and retry.");
	}

	private void Disarm(string why) {
		if (!this.Armed)
			return;
		this.Armed = false;
		this.hook?.Disable();
		Plugin.Chat.Print($"[AchievementTip] {why} after {this.seen} link click(s). See dalamud.log.");
	}

	/// <summary>
	/// ⚠⚠ Never throw out of a detour, and ALWAYS call the original. This is reconnaissance -- it must
	/// leave the game behaving exactly as it did, including opening the unhelpful window, or we would
	/// be measuring a client we had already changed.
	/// </summary>
	private void Detour(LogViewer* self, LinkData* link) {
		try {
			if (this.Armed && DateTime.UtcNow > this.expiresAt)
				this.Disarm("timed out");

			if (this.Armed && link is not null) {
				this.seen++;
				Plugin.Log.Information(
					$"AchievementTip: link click #{this.seen} -- type={link->LinkType} id={link->LinkId} "
					+ $"index={link->LinkIndex} group={link->LinkGroupId} "
					+ $"int1={link->IntValue1} uint1={link->UIntValue1} "
					+ $"int2={link->IntValue2} uint2={link->UIntValue2} "
					+ $"rect=({link->MinX},{link->MinY})-({link->MaxX},{link->MaxY}) "
					+ $"payloadEnd={link->PayloadEnd}");

				// ⚠ The whole payload, as bytes. An achievement id could be anywhere in here, and a
				// hexdump is the only reading that cannot be wrong about where it is.
				if (link->Payload is not null && link->PayloadEnd is > 0 and < 512) {
					var hex = new StringBuilder();
					var text = new StringBuilder();
					for (int i = 0; i < link->PayloadEnd; i++) {
						byte b = link->Payload[i];
						hex.Append(b.ToString("X2")).Append(' ');
						text.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
					}

					Plugin.Log.Information($"AchievementTip:   payload {hex}");
					Plugin.Log.Information($"AchievementTip:   ascii   {text}");
				}
				else {
					Plugin.Log.Information("AchievementTip:   payload unreadable or empty.");
				}
			}
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "AchievementTip: link-click recording failed.");
		}

		this.hook!.Original(self, link);
	}

	/// <summary>
	/// ⚠ Logs only when a payload arrives that Dalamud could not type, or when the line mentions an
	/// achievement. Everything Dalamud already understands -- items, players, statuses -- is silent,
	/// because those are solved and would bury the one line worth reading.
	///
	/// ⚠ The word match is a DIAGNOSTIC CRUTCH, not a design. It is English-only and a patch could
	/// reword it; it exists so the very first achievement is definitely caught even if its payload
	/// turns out to be something Dalamud types cleanly. Delete it once the format is known.
	/// </summary>
	private void OnChatMessage(IHandleableChatMessage message) {
		try {
			bool interesting = message.Message.TextValue.Contains("achievement", StringComparison.OrdinalIgnoreCase);
			int raws = 0;

			foreach (var payload in message.Message.Payloads) {
				if (payload is RawPayload)
					raws++;
			}

			if (raws == 0 && !interesting)
				return;

			Plugin.Log.Information(
				$"AchievementTip: CHAT kind={message.LogKind} ({(int)message.LogKind}) "
				+ $"raw={raws} text=\"{message.Message.TextValue}\"");

			foreach (var payload in message.Message.Payloads) {
				if (payload is not RawPayload raw) {
					Plugin.Log.Information($"AchievementTip:   payload {payload.Type}: {payload}");
					continue;
				}

				var hex = new StringBuilder();
				foreach (byte b in raw.Data)
					hex.Append(b.ToString("X2")).Append(' ');
				Plugin.Log.Information($"AchievementTip:   RAW {hex}");
			}
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "AchievementTip: chat inspection failed.");
		}
	}

	public void Dispose() {
		Plugin.Chat.ChatMessage -= this.OnChatMessage;
		Plugin.Commands.RemoveHandler("/dsuachievesniff");
		this.hook?.Disable();
		this.hook?.Dispose();
	}
}
