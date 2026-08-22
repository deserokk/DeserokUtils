using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Command;

using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DeserokUtils.Features.DrawSheathe;

/// <summary>
/// One key that draws or sheathes, whichever is currently correct -- and picks the fancy emote or
/// the game's own toggle depending on whether you are moving.
///
///   /drawsheathe
///
/// ⚠⚠ THE GOLD SAUCER DRAW AND SHEATHE ARE EMOTES, NOT THE WEAPON TOGGLE. Confirmed from the game's
/// own Emote sheet 2026-08-17: row 237 "Sheathe Weapon" -> /sheathe, row 238 "Draw Weapon" ->
/// /draw, and those two rows are the ONLY draw/sheathe entries in the sheet. Buying them does not
/// replace the default animations, it adds two independent emotes -- so the default draw/sheathe
/// KEYBIND cannot play them, and an emote cannot be bound as a toggle. Two hotbar slots, and you
/// have to know which one is currently correct. That is the problem this removes.
///
/// ⭐ This already worked via TinyCommands: `/ifcmd -r /sheathe motion` + `/ifcmd -R /draw motion`.
/// The missing thing was never functionality, it was OWNERSHIP -- that maintainer has quit several
/// times and takes months to ship even a version bump, so every patch leaves a working setup broken
/// at someone else's pace. Here, patch day is a recompile deserok controls.
///
/// ⚠⚠ AND THE EMOTES DO NOTHING WHILE MOVING. That is what the macro could not solve and is why
/// this is not simply a reimplementation of it: the emote is silently refused, so the key looks
/// broken exactly when you are running somewhere. Hence the second branch -- moving means fall back
/// to the game's own instant toggle, which always works. One key, and the choice between "fancy"
/// and "fast" stops being something to make in your head.
///
/// ⚠ Scope: one keypress, one action. No loop, no queue, no reaction to game events. The state
/// reads and the branch are the whole feature.
/// </summary>
internal sealed class DrawSheatheFeature: IDisposable {
	public string TabTitle => "DrawSheathe";

	private readonly WeaponStateSniffer sniffer = new();

	// ⚠⚠ A `expectedState` / `expectedSince` / `SettleTimeout` trio lived here and is GONE. It waited
	// for the weapon to reach whatever the last press asked for, and it was measured useless: the
	// state flips in about 100 ms, key auto-repeat arrives every 102 ms, so it had already cleared
	// before the next repeat and the cycling continued straight through it.
	//
	// ⭐ Deleted the same hour rather than left beside PressTooSoonBecause. A fix that makes earlier
	// code unreachable is only finished when that code is gone -- and two guards that can disagree
	// about the same press is a worse position than either one alone.

	/// <summary>When the last press EVENT arrived, accepted or dropped. See PressTooSoonBecause.</summary>
	private DateTime lastPressAt = DateTime.MinValue;

	public DrawSheatheFeature() {
		Plugin.Commands.AddHandler("/drawsheathe", new CommandInfo(this.OnDrawSheathe) {
			HelpMessage = "/drawsheathe -- draw or sheathe, whichever is correct. Emote when standing still, the game's own toggle when moving. Add 'state' to report what it reads.",
		});
	}

	// ── the state reads ──────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Whether the weapon is out, or null if there is nobody to ask.
	///
	/// ⭐ `StatusFlags.WeaponOut` off the managed Dalamud object, NOT a pointer walk. It is also the
	/// exact flag TinyCommands' `-r` reads, which is what decided it: this is the signal already
	/// proven correct for this job on this character, not a second candidate that ought to agree.
	///
	/// ⚠ `UIState.WeaponState.IsUnsheathed` is that second candidate and it is deliberately NOT used
	/// for the decision, even though the toggle branch calls a method on that very struct. Mixing
	/// signals would make the answer depend on which branch ran, which is the harder bug of the two.
	/// The tab shows both side by side so a disagreement is visible instead of silent.
	///
	/// ⚠ Null is NOT false. "Not logged in" and "weapon sheathed" would both draw, and one of those
	/// is a bug that reports itself as the feature working. The caller refuses instead.
	/// </summary>
	internal static bool? WeaponIsOut() {
		var player = Plugin.Objects.LocalPlayer;
		if (player is null)
			return null;
		return player.StatusFlags.HasFlag(StatusFlags.WeaponOut);
	}

	/// <summary>
	/// The client's own copy of the same fact, for cross-checking in the tab. See
	/// <see cref="WeaponIsOut"/> for why this is not what the branch reads.
	/// </summary>
	internal static unsafe bool? ClientSaysUnsheathed() {
		UIState* ui = UIState.Instance();
		return ui is null ? null : ui->WeaponState.IsUnsheathed;
	}

	/// <summary>
	/// Whether the player is moving, or null if it cannot be read.
	///
	/// ⚠ `AgentMap.IsPlayerMoving`, and the agent is an odd-looking place for it -- but it is the
	/// field the client keeps for exactly this and it is populated whether or not the map is open.
	/// There is no movement flag on ICondition and no `IsMoving` anywhere in the Dalamud surface;
	/// this was found by reflecting over FFXIVClientStructs.dll rather than assumed.
	///
	/// ⚠ What counts as "moving" here is the client's opinion, not ours, and it has not been
	/// characterised against jumping, mounts or knockbacks. The diagnostic line prints it on every
	/// press so a surprising case shows itself rather than being argued about.
	/// </summary>
	internal static unsafe bool? PlayerIsMoving() {
		AgentMap* map = AgentMap.Instance();
		return map is null ? null : map->IsPlayerMoving;
	}

	/// <summary>
	/// The game's own sheathe cooldown, purely for display. NOT used as a gate.
	///
	/// ⚠⚠ Because nobody knows which direction it counts. `SheatheCooldown` is a float on
	/// WeaponState and FFXIVClientStructs documents neither its unit nor its sign convention -- it is
	/// as plausibly "seconds elapsed since the last change", which only ever grows, as it is
	/// "seconds remaining". Gating on `> 0` under the first reading would dead-lock the key on the
	/// first press and look exactly like the spam bug it was added to fix.
	///
	/// ⭐ SETTLED 2026-08-17, BY WATCHING IT RATHER THAN GUESSING: it counts DOWN. Traced at 0 while
	/// idle, set to 1.0 by a weapon-state change, and decaying at 1.0/second -- `0.895 -> 1`,
	/// `0.487 -> 1`, `0.583 -> 1` across a burst, and 0.891 exactly 102 ms after a change. Both paths
	/// set it: the queued emote does so as surely as the direct call. So `> 0` is safe to gate on and
	/// PressTooSoonBecause now does.
	///
	/// ⚠ The doc above said "displayed, not obeyed" for exactly one build, because the other reading
	/// -- seconds elapsed, only ever climbing -- would have dead-locked the key on the first press
	/// and looked identical to the spam bug being fixed. One round of evidence cost nothing and
	/// removed the ambiguity completely.
	/// </summary>
	internal static unsafe float? SheatheCooldown() {
		UIState* ui = UIState.Instance();
		return ui is null ? null : ui->WeaponState.SheatheCooldown;
	}

	/// <summary>
	/// Why this press must be dropped, or null to let it through -- the spam guard.
	///
	/// ⚠⚠ HOLDING THE KEY IS A REAL INPUT MODE HERE. deserok uses a key repeater as an assistive
	/// device -- tapping is difficult -- and it behaves like ordinary auto-repeat: a tap is one
	/// press, but holding past about half a second streams presses at roughly 10 Hz (102 ms apart,
	/// measured) until release. So a normal press is NOT a burst, and the collapse below never
	/// touches one; it exists for the hold, where a dozen toggles a second is what was observed.
	///
	/// ⚠⚠ A cooldown ALONE does not fix the hold, and shipping only that would have looked fixed. A
	/// one-second gate against a stream does not stop the cycling, it slows it to one toggle per
	/// second and keeps going for as long as the key is down. The stream has to COLLAPSE.
	///
	/// ⭐ So the first rule is a quiet-gap test, about the input device rather than the game: act on
	/// the first press, then ignore everything until the key has been quiet for
	/// <see cref="Configuration.DrawSheatheRepeatCollapseMs"/>. A held key never reaches that gap and
	/// so acts exactly once; releasing and pressing again does. Configurable because repeat rates are
	/// a property of somebody's hardware, and this is an accessibility setting rather than a tuning
	/// detail to bury in a constant.
	///
	/// ⭐⭐ THIS IS THE GAME'S OWN COOLDOWN, MEASURED. `WeaponState.SheatheCooldown` is set to 1.0 by
	/// a weapon-state change and decays to 0 at 1.0/second -- traced 2026-08-17 as 0 at rest, then
	/// `0.895 -> 1`, `0.487 -> 1`, `0.583 -> 1` across a burst, and 0.891 exactly 102 ms after a
	/// change. It counts DOWN, and BOTH paths set it: the emote does it as surely as the direct call.
	/// So one gate covers both, with no invented constant, and it is the same one second the default
	/// keybind enforces -- which is why the default key never glitched.
	///
	/// ⚠⚠ It replaced a guard that waited for the weapon state to reach what the last press asked
	/// for. That guard was not wrong, it was just too fast to help: the flag flips within ~100 ms, so
	/// it cleared before the next repeat arrived and the cycle continued underneath it. Deleted
	/// rather than left beside this one -- two rules that can disagree about the same press is worse
	/// than either alone.
	///
	/// ⭐ A refused action sets no cooldown, so it does not lock you out. The old guard armed itself
	/// on a request it could not confirm and burned two seconds when the request went nowhere.
	///
	/// ⚠ Unreadable means allow. Falling back to "block" would make an unavailable UIState look
	/// exactly like the dead key this exists to prevent.
	///
	/// ⚠ It WRITES as well as reads -- every press event stamps the clock, dropped ones included,
	/// because a repeat that was ignored is still evidence the key is down. Impure for a "Because"
	/// method, and called from exactly one place for that reason.
	/// </summary>
	private string? PressTooSoonBecause() {
		DateTime now = DateTime.UtcNow;
		TimeSpan sinceLastPress = now - this.lastPressAt;
		this.lastPressAt = now;

		int windowMs = Plugin.Config.DrawSheatheRepeatCollapseMs;
		if (windowMs > 0 && sinceLastPress < TimeSpan.FromMilliseconds(windowMs))
			return $"key repeat ({sinceLastPress.TotalMilliseconds:0}ms gap, collapsing anything under {windowMs}ms)";

		float? cooldown = SheatheCooldown();
		if (cooldown > 0f)
			return $"the game's sheathe cooldown ({cooldown.Value:0.###}s left)";

		return null;
	}

	/// <summary>
	/// Why the emote would be refused right now, or null if it would play.
	///
	/// ⚠⚠ THE QUESTION IS NOT "AM I MOVING". It only looked that way because moving was the first
	/// case found. Jumping on the spot refuses the emote too and reads `IsPlayerMoving == false` --
	/// so a check named after movement was answering a narrower question than the one being asked,
	/// and would have kept being wrong one new case at a time.
	///
	/// ⭐ Returning a REASON rather than a bool is the point. Every branch says out loud which
	/// condition sent it down the fast path, so the next case that turns up (falling? gliding?
	/// mounted?) is named in the diagnostic line instead of being inferred from the key misbehaving.
	/// `/drawsheathe conditions` lists everything the game currently has set, for exactly that.
	///
	/// ⚠ This is a list of OBSERVED refusals, not a model of the game's rule. It cannot be complete
	/// and is not trying to be: an unlisted case simply gets the emote, which fails harmlessly and
	/// is the behaviour that existed before any of this.
	///
	/// ⚠⚠⚠ DESPITE THE NAME, THIS IS NOT "EVERY CASE WHERE THE EMOTE FAILS". It is "cases where
	/// falling back to the direct call is CORRECT", and those two sets are not the same. **Cutscenes
	/// are the counterexample and must stay off this list.** Tested 2026-08-17: typing the command
	/// during one gets a clean refusal from the game -- *The command "/draw motion" is unavailable at
	/// this time.* -- which is the right outcome, delivered with a reason. Adding cutscenes here
	/// would route them to SetUnsheathed, which is a raw state write that consults none of that, and
	/// would force the weapon out mid-scene. The "consistency fix" is the bug.
	///
	/// ⭐ So the test for adding a case is not "does the emote fail here" but "should the game's own
	/// toggle happen here instead". Moving and jumping pass it: you asked to draw, and drawing while
	/// running is ordinary. A cutscene fails it: the game is saying no on purpose.
	/// </summary>
	/// <summary>
	/// Which emote command a press would send right now. ⭐ One place, so the ownership check and the
	/// send cannot disagree about which emote is in question -- checking one and sending the other is
	/// exactly the bug this was added to fix.
	/// </summary>
	internal static string CommandFor(bool weaponOut) =>
		(weaponOut ? Plugin.Config.SheatheCommand : Plugin.Config.DrawCommand).Trim();

	internal static string? EmoteRefusedBecause(string? command = null) {
		// ⚠⚠ FIRST, because it is the only durable one. Moving and jumping pass in a second; an emote
		// you do not own never becomes available, so checking it first means the diagnostic names the
		// fact worth acting on rather than whichever transient state happened to also be true.
		//
		// ⭐ Bunny, 2026-08-22: she owns Draw Weapon and not Sheathe Weapon, so sheathing did nothing
		// at all -- the command was refused and nothing took over. See EmoteUnlock.
		if (command is not null && EmoteUnlock.LockedBecause(command) is string locked)
			return locked;

		if (PlayerIsMoving() == true)
			return "moving";

		// ⚠ Both flags, OR'd. Dalamud exposes Jumping and Jumping61 and does not say how they
		// differ; either being set means the character is off the ground, which is the fact wanted
		// here. Picking one on a guess is how you get a check that works for hops and not for falls.
		if (Plugin.Condition[ConditionFlag.Jumping] || Plugin.Condition[ConditionFlag.Jumping61])
			return "jumping";

		return null;
	}

	// ── /drawsheathe ─────────────────────────────────────────────────────────────────────────

	private void OnDrawSheathe(string command, string arguments) {
		string arg = arguments.Trim().ToLowerInvariant();
		bool? weaponOut = WeaponIsOut();
		string? refused = EmoteRefusedBecause(
			weaponOut is null ? null : CommandFor(weaponOut.Value));

		if (arg is "state" or "status" or "?") {
			this.ReportState(weaponOut, refused);
			return;
		}

		if (arg is "conditions" or "cond" or "flags") {
			DumpConditions();
			return;
		}

		if (arg.StartsWith("sniff", StringComparison.Ordinal) || arg == "record") {
			this.OnSniff(arg.StartsWith("sniff", StringComparison.Ordinal) ? arg["sniff".Length..].Trim() : string.Empty);
			return;
		}

		// ⚠ An unrecognised argument is an error, not something to ignore. Silently toggling on a
		// typo leaves the typo in the macro, to surface later as "it did the wrong thing once".
		if (arg.Length > 0) {
			Plugin.Chat.PrintError($"[DrawSheathe] unknown argument \"{arg}\". Use /drawsheathe, or /drawsheathe state.");
			return;
		}

		if (weaponOut is null) {
			Plugin.Chat.PrintError("[DrawSheathe] could not read your weapon state (no local player). Nothing sent.");
			return;
		}

		// ⚠ SILENT, and on purpose. This is the spam path -- the whole point is that it fires while
		// the key repeats, so a chat line per rejected press would be its own kind of spam. The
		// diagnostic channel is where a suppressed press belongs.
		string? tooSoon = this.PressTooSoonBecause();
		if (tooSoon is not null) {
			Trace($"press ignored: {tooSoon}");
			return;
		}

		// ⚠ A read that FAILED is not a refusal. EmoteRefusedBecause returns null both for "it would
		// play" and for "could not tell", and both fall through to the emote -- the pre-existing
		// behaviour, which fails harmlessly, rather than a client call made on the strength of a null.
		if (Plugin.Config.UseDefaultToggleWhenEmoteWouldFail && refused is not null) {
			this.FastToggle(weaponOut.Value, refused);
			return;
		}

		this.PlayEmote(weaponOut.Value, refused);
	}

	/// <summary>
	/// Every condition flag the game currently has set.
	///
	/// ⭐ Here because the refusal list above cannot be complete, and the alternative to this is
	/// guessing which flag names the case you just hit. Same argument as the sniffer: when a new
	/// refusal turns up, read what the game says rather than reasoning about what it might be.
	///
	/// ⚠ Prints only what is SET. The full enum is ~90 entries, most of them off, and a wall of
	/// False is how a useful diagnostic becomes one nobody runs.
	/// </summary>
	private static void DumpConditions() {
		var set = new System.Collections.Generic.List<string>();
		foreach (ConditionFlag flag in Enum.GetValues<ConditionFlag>()) {
			if (Plugin.Condition[flag])
				set.Add($"{flag} ({(int)flag})");
		}

		string moving = Show(PlayerIsMoving());
		string cooldown = Show(SheatheCooldown());
		Plugin.Chat.Print($"[DrawSheathe] IsPlayerMoving={moving}, SheatheCooldown={cooldown}; conditions set: "
			+ (set.Count > 0 ? string.Join(", ", set) : "none"));
		Plugin.Log.Information($"DrawSheathe conditions: moving={moving} cooldown={cooldown}; {string.Join(", ", set)}");
	}

	/// <summary>The Gold Saucer animation. Silently refused while moving, which is why the branch exists.</summary>
	private void PlayEmote(bool weaponOut, string? refused) {
		string line = CommandFor(weaponOut);
		if (line.Length == 0) {
			// ⚠ A blank box must not fail quietly. From outside, a key that does nothing is
			// indistinguishable from the state read being broken, and that is the wrong half to go
			// looking at.
			Plugin.Chat.PrintError(
				$"[DrawSheathe] the {(weaponOut ? "sheathe" : "draw")} command is blank in the DrawSheathe tab. Nothing sent.");
			return;
		}

		// ⭐ QUEUED, not RunNow, and the asymmetry in GameCommands is deliberate. There is no
		// following macro line to outrun -- the whole feature is one line -- and the chatbox pipeline
		// is what makes this behave exactly as if the emote had been typed, which is the promise
		// being made. RunNow exists for the one case that must beat a macro's next line
		// (/macrocancel in CastWatch); here it would buy nothing and lose placeholder expansion.
		Trace($"weapon {(weaponOut ? "OUT" : "away")}, refused={refused ?? "no"} -> emote: {line}"
			+ $" | cooldown={Show(SheatheCooldown())}");
		GameCommands.Queue(line);
	}

	/// <summary>
	/// The game's own draw/sheathe -- what the default keybind does, from code, because the sheet has
	/// no text command for it. Only `/autosheathe`, `/draw` and `/sheathe` mention it, and the last
	/// two are the emotes.
	///
	/// ⚠⚠⚠ THE ARGUMENTS ARE COPIED FROM A RECORDING OF THE REAL KEYBIND, AND `isInstant` MEANS THE
	/// OPPOSITE OF WHAT IT IS CALLED.
	///
	///   newState   -- true = unsheathed. So it is the OPPOSITE of what we just read.
	///   sendPacket -- whether to send a network update. TRUE: other players must see it, and the
	///                 default keybind is not a client-side illusion.
	///   isInstant  -- TRUE. Read the name and this is obviously the flag that skips the animation,
	///                 so it shipped as false. That teleported the weapon into the hand with no
	///                 animation at all -- the exact effect the name promises for `true`.
	///
	/// ⭐⭐ The sniffer settled it in one press. The game's own keybind passes `isInstant: true`
	/// EVERY time, standing still and moving alike (recorded 2026-08-17, eight presses, no
	/// variation), and that is the call that animates. Whatever the flag really selects, it is not
	/// "skip the animation" -- and no amount of staring at the parameter list would have said so.
	///
	/// ⚠ So the arguments do NOT vary with movement. `moving` chooses BETWEEN the emote and this
	/// function; it changes nothing about how this function is called. A future reader looking for
	/// the "moving" variant of the call should stop here: there isn't one.
	///
	/// ⚠ It returns a bool. Nothing here can act on a refusal -- there is no second thing to try --
	/// but a false is logged, because "the key did nothing" and "the client said no" look identical
	/// from the outside and only one of them is worth investigating.
	/// </summary>
	private unsafe void FastToggle(bool weaponOut, string? refused) {
		UIState* ui = UIState.Instance();
		if (ui is null) {
			// Fall back rather than do nothing -- and SAY which path ran, or a working feature and a
			// broken one produce the same silence.
			Plugin.Log.Warning("DrawSheathe: UIState was null; falling back to the emote.");
			Trace("UIState unavailable -> fell back to the emote path.");
			this.PlayEmote(weaponOut, refused);
			return;
		}

		float before = ui->WeaponState.SheatheCooldown;
		bool ok = ui->WeaponState.SetUnsheathed(!weaponOut, true, true);
		Trace($"weapon {(weaponOut ? "OUT" : "away")}, refused={refused ?? "no"} -> default toggle"
			+ $" SetUnsheathed({!weaponOut}, sendPacket: true, isInstant: true) returned {ok}"
			+ $" | cooldown {before:0.###} -> {ui->WeaponState.SheatheCooldown:0.###}");

		// ⚠ Nothing to arm. The cooldown this call just set IS the guard, and a refused call sets no
		// cooldown -- so a refusal correctly leaves the next press free instead of locking it out.
		if (!ok)
			Plugin.Log.Information($"DrawSheathe: SetUnsheathed({!weaponOut}) returned false -- the client refused it.");
	}

	/// <summary>
	/// Arm the recorder, then go press the REAL draw/sheathe key -- the game's own keybind, not this
	/// command. Whatever the client calls on the way to the animation shows up in chat.
	/// </summary>
	private void OnSniff(string rest) {
		if (rest is "off" or "stop") {
			if (this.sniffer.Armed)
				this.sniffer.Disarm();
			else
				Plugin.Chat.Print("[DrawSheathe] the sniffer was not running.");
			return;
		}

		if (!this.sniffer.Available) {
			Plugin.Chat.PrintError("[DrawSheathe] neither WeaponState function could be resolved -- nothing to hook. See /xllog.");
			return;
		}

		TimeSpan duration = WeaponStateSniffer.DefaultDuration;
		if (rest.Length > 0) {
			if (!int.TryParse(rest, out int seconds) || seconds <= 0) {
				Plugin.Chat.PrintError($"[DrawSheathe] \"{rest}\" is not a number of seconds. Try /drawsheathe sniff 60.");
				return;
			}
			duration = TimeSpan.FromSeconds(seconds);
		}

		this.sniffer.Arm(duration);
		Plugin.Chat.Print(
			$"[DrawSheathe] sniffer ON for {duration.TotalSeconds:0}s. Now press YOUR OWN draw/sheathe keybind "
			+ "(not this command) while standing still, then again while moving. /drawsheathe sniff off to stop.");
	}

	private void ReportState(bool? weaponOut, string? refused) {
		if (weaponOut is null) {
			Plugin.Chat.Print("[DrawSheathe] no local player to read.");
			return;
		}

		bool fast = Plugin.Config.UseDefaultToggleWhenEmoteWouldFail && refused is not null;
		string verb = weaponOut.Value ? "sheathe" : "draw";
		string how = fast ? $"the game's own toggle (you are {refused})" : "the emote";
		Plugin.Chat.Print(
			$"[DrawSheathe] weapon is {(weaponOut.Value ? "OUT" : "AWAY")}"
			+ $"{(refused is null ? "" : $", {refused}")} -- a press would {verb} using {how}.");
	}

	private static string Show(bool? value) => value switch {
		true => "yes",
		false => "no",
		null => "unreadable",
	};

	private static string Show(float? value) => value is null ? "unreadable" : $"{value.Value:0.###}";

	/// <summary>
	/// Every press outcome, to BOTH the log and the diagnostic channel.
	///
	/// ⚠⚠ The log half is not redundant. Diag is off by default, so the first round of "it does
	/// nothing when moving" produced a completely empty dalamud.log -- no evidence at all, and the
	/// only way forward was to ask for the toggle and repeat the test. A keypress is a rare event;
	/// one line each is nothing next to being able to read what happened after the fact.
	/// </summary>
	private static void Trace(string message) {
		Plugin.Log.Information($"DrawSheathe: {message}");
		Plugin.Diag(message);
	}

	// ── the tab ──────────────────────────────────────────────────────────────────────────────

	public void DrawTab() {
		// Cheapest place to notice an arm has aged out. See WeaponStateSniffer.ExpireIfDue.
		this.sniffer.ExpireIfDue();

		ImGui.TextWrapped(
			"One key that draws or sheathes, whichever is currently correct. Standing still it plays "
			+ "the Gold Saucer emote; moving it uses the game's own toggle, because the emote is "
			+ "silently refused while you are moving.");
		ImGui.Spacing();

		bool? weaponOut = WeaponIsOut();
		string? refused = EmoteRefusedBecause(
			weaponOut is null ? null : CommandFor(weaponOut.Value));

		Section("Right now");
		switch (weaponOut) {
			case true:
				ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "weapon is OUT");
				break;
			case false:
				ImGui.Text("weapon is away");
				break;
			default:
				ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), "no local player to read");
				break;
		}

		if (weaponOut is not null) {
			ImGui.SameLine();
			bool fast = Plugin.Config.UseDefaultToggleWhenEmoteWouldFail && refused is not null;
			ImGui.TextDisabled(
				$"-- {refused ?? "still"}, so a press would {(weaponOut.Value ? "sheathe" : "draw")}"
				+ $" using {(fast ? "the game's toggle" : "the emote")}");

			// ⚠ Two independent sources for one fact, shown together on purpose. They should always
			// agree; if they ever do not, that is worth seeing rather than discovering as a branch
			// that behaves differently from the readout above it.
			bool? client = ClientSaysUnsheathed();
			if (client is not null && client != weaponOut)
				ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f),
					$"⚠ the client's own WeaponState.IsUnsheathed says {client} -- these disagree.");

			ImGui.TextDisabled($"game's SheatheCooldown: {Show(SheatheCooldown())}");
		}

		Section("Bind this");
		ImGui.TextUnformatted("/drawsheathe");
		if (ImGui.Button("Copy##ds_cmd"))
			ImGui.SetClipboardText("/drawsheathe");
		ImGui.SameLine();
		ImGui.TextDisabled("one line in a macro, dragged to a hotbar slot -- this replaces both keys");

		Section("When the emote would be refused");
		bool useToggle = Plugin.Config.UseDefaultToggleWhenEmoteWouldFail;
		if (ImGui.Checkbox("Fall back to the game's own draw/sheathe", ref useToggle)) {
			Plugin.Config.UseDefaultToggleWhenEmoteWouldFail = useToggle;
			Plugin.Config.Save();
		}
		ImGui.TextWrapped(
			"The emote does nothing while you are moving or jumping, so those fall back to the game's "
			+ "own toggle and one key covers everything. Off, the key only ever sends the emote, which "
			+ "is the old behaviour.");
		ImGui.Spacing();
		ImGui.TextWrapped(
			"Worth knowing this switch is here: the fallback is the only part that calls into the "
			+ "client directly, so if a patch ever breaks it, turning this off gets a working key back "
			+ "without waiting for a build.");
		ImGui.Spacing();
		ImGui.TextDisabled("/drawsheathe conditions");
		ImGui.TextWrapped(
			"Moving and jumping are the refusals found so far, not a complete model of the game's "
			+ "rule. If the key ever no-ops again, run that while it is happening -- it lists every "
			+ "condition flag the game has set, which names the case instead of guessing at it.");

		Section("Held keys and repeaters");
		ImGui.TextWrapped(
			"Presses closer together than this count as one press, so holding the key gives a single "
			+ "toggle instead of a stream of them. A normal tap is one press and is never affected. "
			+ "Set 0 to switch it off.");
		ImGui.Spacing();
		int collapse = Plugin.Config.DrawSheatheRepeatCollapseMs;
		ImGui.SetNextItemWidth(160f);
		if (ImGui.InputInt("ms##ds_collapse", ref collapse)) {
			Plugin.Config.DrawSheatheRepeatCollapseMs = Math.Clamp(collapse, 0, 2000);
			Plugin.Config.Save();
		}
		ImGui.TextWrapped(
			"250 is roughly double the 102ms repeat measured here, which is comfortable margin. Raise "
			+ "it if a single press still toggles more than once; lower it if two deliberate presses "
			+ "in quick succession only register as one.");
		ImGui.Spacing();
		ImGui.TextDisabled(
			$"the game's own sheathe cooldown is also honoured: {Show(SheatheCooldown())}");

		Section("What it sends when standing still");
		ImGui.TextWrapped(
			"Whole command lines, sent exactly as if typed. Change them if you would rather it drove a "
			+ "different pair of emotes.");
		ImGui.Spacing();

		string draw = Plugin.Config.DrawCommand;
		ImGui.TextUnformatted("when the weapon is away");
		ImGui.SetNextItemWidth(-1);
		if (ImGui.InputText("##ds_draw", ref draw, 128)) {
			Plugin.Config.DrawCommand = draw;
			Plugin.Config.Save();
		}

		string sheathe = Plugin.Config.SheatheCommand;
		ImGui.TextUnformatted("when the weapon is out");
		ImGui.SetNextItemWidth(-1);
		if (ImGui.InputText("##ds_sheathe", ref sheathe, 128)) {
			Plugin.Config.SheatheCommand = sheathe;
			Plugin.Config.Save();
		}

		ImGui.Spacing();
		if (ImGui.Button($"Reset to \"{Configuration.DefaultDrawCommand}\" and \"{Configuration.DefaultSheatheCommand}\"")) {
			Plugin.Config.DrawCommand = Configuration.DefaultDrawCommand;
			Plugin.Config.SheatheCommand = Configuration.DefaultSheatheCommand;
			Plugin.Config.Save();
		}

		Section("Notes");
		ImGui.BulletText("\"motion\" is the game's own subcommand for hiding the emote's chat text.");
		ImGui.TextWrapped(
			"The game writes its usage as /draw [subcommand] -- the square brackets mean \"optional\" "
			+ "and are not typed. /draw [motion] is not the same thing as /draw motion.");
		ImGui.Spacing();
		ImGui.BulletText("Draw Weapon and Sheathe Weapon are emotes (Emote rows 238 and 237).");
		ImGui.TextWrapped(
			"They are the only two draw/sheathe rows in the sheet, so buying them adds a pair of emotes "
			+ "rather than replacing the default animations. That is why the default keybind will not "
			+ "play them, and why they cannot be bound as a toggle.");
		ImGui.Spacing();
		ImGui.BulletText("Nothing is suppressed in combat.");
		ImGui.TextWrapped(
			"An emote the game will not allow already fails harmlessly and says so itself. A gate here "
			+ "would be a guess at a case nobody has hit.");

		Section("Sniffer");
		ImGui.TextWrapped(
			"Records what the client actually calls when you press your own draw/sheathe keybind, and "
			+ "prints it to chat. This is here because the obvious call turned out to skip the "
			+ "animation, and reading the real one beats reasoning about the parameter names.");
		ImGui.Spacing();
		if (this.sniffer.Armed) {
			ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"recording -- {this.sniffer.Remaining.TotalSeconds:0}s left");
			ImGui.SameLine();
			if (ImGui.Button("Stop##ds_sniff"))
				this.sniffer.Disarm();
		}
		else if (!this.sniffer.Available) {
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "neither WeaponState function resolved -- nothing to hook. See /xllog.");
		}
		else {
			if (ImGui.Button("Record for 60s##ds_sniff"))
				this.sniffer.Arm(WeaponStateSniffer.DefaultDuration);
			ImGui.SameLine();
			ImGui.TextDisabled("or /drawsheathe sniff");
		}
	}

	/// <summary>ImGui.SeparatorText does not exist in this binding version; this is the stand-in.</summary>
	private static void Section(string title) {
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextDisabled(title);
		ImGui.Spacing();
	}

	public void Dispose() {
		Plugin.Commands.RemoveHandler("/drawsheathe");
		this.sniffer.Dispose();
	}
}
