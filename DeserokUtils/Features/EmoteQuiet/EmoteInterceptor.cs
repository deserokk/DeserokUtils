using System;
using System.Collections.Generic;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace DeserokUtils.Features.EmoteQuiet;

/// <summary>
/// Sets <c>PlayEmoteOption.DisableLogMessage</c> on a repeat of an emote you have already announced,
/// so the first clap talks and the next fifty do not.
///
/// ## ⭐⭐ Everything below was MEASURED, 2026-08-17, before a line of it was written
///
/// Recorded at this exact hook, one press per row:
///
/// <code>
/// /clap                 Flags=0x00  DisableLogMessage=False
/// /clap motion          Flags=0x01  DisableLogMessage=True
/// emote window click    Flags=0x88  DisableLogMessage=False   (AgentEmote got option=NULL)
/// hotbar slot           Flags=0x00  DisableLogMessage=False   (AgentEmote got option=NULL)
/// </code>
///
/// ⭐ **`motion` IS this flag and nothing else.** The two text-command calls reach this function
/// with identical arguments except bit 0. Since this is the funnel through which the emote actually
/// executes, and the command's only influence on it is these arguments, that bit is the whole
/// mechanism -- so setting it ourselves is exactly what typing `motion` does.
///
/// ⚠⚠ **NEVER ASSIGN Flags WHOLESALE.** The emote window arrives with `0x88` -- bits 3 and 7 set,
/// meaning something this code knows nothing about. Writing `Flags = 0x01` would silently clear them.
/// Only the `DisableLogMessage` property is ever touched, because it sets one bit and leaves the
/// rest alone. This was caught solely because the recording printed the raw byte next to the
/// friendly property; the friendly property alone said `False` in both cases and looked identical.
///
/// ⚠⚠ **Hooked at EmoteManager, not AgentEmote, and that was measured too.** `AgentEmote.ExecuteEmote`
/// receives `option = NULL` for the emote window and hotbar and builds the struct on the way down --
/// so a hook there would have nothing to modify on exactly the paths that matter most. A second hook
/// on AgentEmote existed while this was being investigated and was DELETED once it had answered:
/// nothing acts on that layer, and a detour that only observes is a detour to remove.
///
/// ⚠ Still unconfirmed by anything inside this client: whether other players stop seeing the line.
/// Nothing here can read someone else's chat log. The argument above is tight, but the acceptance
/// test is a person standing next to you.
/// </summary>
internal sealed unsafe class EmoteInterceptor: IDisposable {
	/// <summary>
	/// Signature verbatim from FFXIVClientStructs, NOT from memory:
	///   EmoteManager.ExecuteEmote(UInt16 emoteId, EmoteController.PlayEmoteOption* playEmoteOption) : Boolean
	/// </summary>
	private delegate bool ExecuteEmoteDelegate(
		EmoteManager* self, ushort emoteId, EmoteController.PlayEmoteOption* option);

	private readonly Hook<ExecuteEmoteDelegate>? hook;

	/// <summary>
	/// When each emote last actually ANNOUNCED itself. Keyed by emote id, so clapping does not
	/// silence your next /dote -- "the same emote" is the unit, per the original ask.
	/// </summary>
	private readonly Dictionary<ushort, DateTime> lastAnnounced = new();

	private DateTime sniffUntil = DateTime.MinValue;
	private int sniffSeen;

	public static readonly TimeSpan DefaultSniffDuration = TimeSpan.FromSeconds(90);

	public bool Available => this.hook is not null;
	public bool Sniffing => DateTime.UtcNow < this.sniffUntil;
	public TimeSpan SniffRemaining => this.Sniffing ? this.sniffUntil - DateTime.UtcNow : TimeSpan.Zero;

	public EmoteInterceptor() {
		nint addr = (nint)EmoteManager.MemberFunctionPointers.ExecuteEmote;
		if (addr == nint.Zero) {
			// A hook that silently fails to install is the worst outcome: the feature would look
			// switched on and quietly do nothing forever. Say so, loudly, once.
			Plugin.Log.Error("EmoteQuiet: could not resolve EmoteManager.ExecuteEmote. Suppression will not work.");
			return;
		}

		this.hook = Plugin.Interop.HookFromAddress<ExecuteEmoteDelegate>(addr, this.Detour);
		Plugin.Log.Information($"EmoteQuiet: resolved EmoteManager.ExecuteEmote at 0x{addr:X}");
		this.Sync();
	}

	/// <summary>
	/// ⭐ ONE hook serving both jobs. The suppressor and the recorder want the same detour on the
	/// same cold function, so installing two would mean two detours where one does. Enabled while
	/// either wants it, disabled when neither does -- see the per-frame audit in DeserokUtils.md for
	/// why a permanently-installed hook is not the default here.
	/// </summary>
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

	/// <summary>Forget every timer, so the next use of anything announces again.</summary>
	public void Reset() {
		this.lastAnnounced.Clear();
		Plugin.Chat.Print("[EmoteQuiet] timers cleared -- the next use of each emote will announce.");
	}

	/// <summary>Emotes currently inside their quiet window, newest first, for the tab.</summary>
	public IEnumerable<(ushort Id, TimeSpan Remaining)> ActiveWindows() {
		TimeSpan window = TimeSpan.FromSeconds(Math.Max(1, Plugin.Config.EmoteQuietWindowSeconds));
		DateTime now = DateTime.UtcNow;
		foreach (var (id, when) in this.lastAnnounced) {
			TimeSpan left = window - (now - when);
			if (left > TimeSpan.Zero)
				yield return (id, left);
		}
	}

	// ── the detour ───────────────────────────────────────────────────────────────────────────

	private bool Detour(EmoteManager* self, ushort emoteId, EmoteController.PlayEmoteOption* option) {
		string before = string.Empty;
		string action = "untouched";

		try {
			if (this.Sniffing)
				before = Describe(option);

			action = this.Decide(emoteId, option);
		}
		catch (Exception ex) {
			// An exception out of a detour takes the game with it. Catch -- and therefore log, or a
			// broken interceptor is indistinguishable from a quiet one.
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

	/// <summary>
	/// The whole rule. Returns what it did, for the log.
	///
	/// ⚠⚠ AN EMOTE THAT WAS ALREADY SILENT DOES NOT START THE TIMER. If you typed `/clap motion`
	/// yourself, no message went out, so treating it as "you have announced this" would eat the next
	/// real one. The timer tracks announcements, not uses -- which is why the dictionary is called
	/// lastAnnounced and not lastUsed.
	///
	/// ⚠ A null option is passed through untouched rather than guessed at. It has never been observed
	/// at this layer -- AgentEmote is where NULL shows up, and it fills the struct in before calling
	/// this -- but "never observed" is not "cannot happen", and the safe direction is the message
	/// going out when it should not rather than a crash.
	///
	/// ⚠⚠ THE GAME'S OWN "Display log message" CHECKBOX IS NOT VISIBLE HERE. Measured 2026-08-17:
	/// with it OFF, `/clap` still arrives as `Flags=0x00, DisableLogMessage=False`. So that setting
	/// is applied somewhere downstream and is a DIFFERENT mechanism from `motion` -- which is worth
	/// knowing, because it means this code cannot tell whether a message actually went out.
	///
	/// ⭐ It does suppress for EVERYONE, not just locally -- deserok has relied on that for years,
	/// spamming backflips with the box off and nobody ever seeing the line. Which settles the shape
	/// of the whole feature: the game has two independent broadcast-suppression routes, the
	/// all-or-nothing checkbox and the per-emote flag, and this uses the second to get what the first
	/// cannot express -- "say it once, then be quiet".
	///
	/// ⚠ Hence one known edge, accepted rather than fixed: with the checkbox off, a timer is started
	/// for an announcement that may never have happened, so ticking the box again inside the window
	/// can eat one message. It self-heals in a minute and `/emotequiet reset` clears it.
	/// `UiConfigOption.EmoteTextType` exists and could be consulted, but nothing documents which
	/// value means "on" -- and inventing a direction for an undocumented flag is precisely the
	/// `isInstant` mistake, which cost a build the same evening. One clap in a corner case is not
	/// worth paying that twice.
	/// </summary>
	private string Decide(ushort emoteId, EmoteController.PlayEmoteOption* option) {
		if (!Plugin.Config.EmoteQuietEnabled)
			return "disabled";
		if (option is null)
			return "option was NULL -- passed through";
		if (option->DisableLogMessage)
			return "already silent (you asked for motion); timer not started";

		DateTime now = DateTime.UtcNow;
		var window = TimeSpan.FromSeconds(Math.Max(1, Plugin.Config.EmoteQuietWindowSeconds));

		if (this.lastAnnounced.TryGetValue(emoteId, out DateTime last) && now - last < window) {
			// ⚠⚠ The property, NEVER `Flags = ...`. The emote window arrives carrying 0x88 and those
			// bits mean something to code that is not this code.
			option->DisableLogMessage = true;
			return $"SUPPRESSED (announced {(now - last).TotalSeconds:0.#}s ago)";
		}

		this.lastAnnounced[emoteId] = now;
		return "announced; quiet window started";
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
