using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace DeserokUtils.Features.Interact;

/// <summary>
/// A key that operates the thing in front of you and NOTHING else.
///
///   /dsuinteract
///
/// ## The problem
///
/// Num0 (Confirm) is console-shaped and serves two masters: it operates world objects AND drives
/// menus. It also takes two presses for a lever -- one to target, one to use -- so you press it
/// repeatedly. **With any menu open those presses land in the menu**, drop a cursor on it, and
/// activate whatever is under it.
///
/// deserok keeps consumables at the top of his inventory for quick access, which turns a Watcher's
/// Tower key-and-wheel run with the bags open into a raid-grade strength potion consumed during a
/// levelling roulette. Everything else in this plugin fails by doing nothing; this fails by
/// spending something.
///
/// ⭐ A dedicated key cannot have that failure, because it is not the Confirm key -- there is no
/// menu path for it to be routed down.
///
/// ## ⭐ The guard is deserok's, and it is better than what was proposed
///
/// A quiet-gap collapse was suggested, copying DrawSheathe. His answer was to *"lock out if casting
/// anything, since interacting with everything is always a cast bar"* -- which is the game's own
/// state rather than an interval somebody chose, and therefore correct at every animation length
/// instead of at the one that got measured. Same reason SheatheCooldown beat an invented number.
/// </summary>
internal sealed class InteractFeature: IDisposable {
	public string TabTitle => "Interact";

	private readonly InteractSniffer sniffer = new();

	/// <summary>
	/// ⚠ Load-bearing, and only found because two machines disagreed. See <see cref="GimmickConfirm"/>:
	/// without it this key stops at a confirmation box on any client not running YesAlready, which is
	/// the trip to a menu the whole feature exists to avoid.
	/// </summary>
	private readonly GimmickConfirm gimmicks = new();

	public InteractFeature() {
		Plugin.Commands.AddHandler("/dsuinteract", new CommandInfo(this.OnCommand) {
			HelpMessage = "/dsuinteract -- operate the thing in front of you, without touching any menu. Add why to inspect a spot, or sniff to record what Confirm does.",
		});
	}

	/// <summary>
	/// ⚠ A floor for interactions with NO cast bar. deserok: *"maybe a 1 second lockout, some things
	/// don't have a castbar (aetheryte does not)"* -- so IsCasting alone has a hole, and this covers
	/// it. Two conditions, but they guard genuinely different things rather than duplicating.
	///
	/// ⚠⚠ 800ms, not the 1000ms he specified, and the missing 200ms is not a rounding opinion. The
	/// keybind repeats a HELD key every second by default, and a floor of exactly one second sits
	/// right on top of that -- the repeat lands at 1000ms plus a frame, the floor rejects anything
	/// under 1000ms, and whether a press survives comes down to frame timing. Holding the key would
	/// work intermittently for no visible reason. 800ms clears any repeat at or above a second while
	/// still doing the only job it has: stopping the second of two rapid presses on an aetheryte.
	/// </summary>
	private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(800);

	private DateTime lastInteract = DateTime.MinValue;
	private string lastResult = "nothing yet";

	private void OnCommand(string command, string arguments) {
		string arg = arguments.Trim().ToLowerInvariant();

		if (arg.StartsWith("sniff", StringComparison.Ordinal)) {
			this.OnSniff(arg["sniff".Length..].Trim());
			return;
		}

		if (arg is "why" or "probe") {
			this.OnWhy();
			return;
		}

		if (arg.Length > 0) {
			Plugin.Chat.PrintError($"[Interact] unknown argument \"{arg}\". Use /dsuinteract, /dsuinteract why, or /dsuinteract sniff.");
			return;
		}

		this.DoInteract();
	}

	/// <summary>
	/// Operate the thing in front of you, and touch nothing else.
	///
	/// ⭐ Calls <c>InteractWithObject(obj, checkLineOfSight: true)</c> — exactly what the real key
	/// does, verified rather than assumed.
	///
	/// ⚠⚠⚠ IT PASSED <c>false</c> FROM 2026-08-28 TO 2026-09-03, AND THAT BROKE GATHERING.
	/// The change was made while chasing the Snowcloak winch, on a theory that turned out to be wrong
	/// twice over: the winch was never a raycast problem — <see cref="Choose"/> was picking a gate
	/// standing nearer than the winch — and the argument was never verified against the recording
	/// afterwards. It sat here for six days as a known-unproven deviation, doing nothing anybody
	/// noticed, until Bunny tried to pick a tree.
	///
	/// ⭐⭐ What settled it, and it took one press. The sniffer already hooks both interaction
	/// functions, so the vanilla key answers the question itself:
	/// <code>
	/// InteractWithObject("Mature Tree" kind=GatheringPoint dataId=30015, checkLineOfSight: True)
	/// </code>
	/// Same function — the guess that gathering needed <c>OpenObjectInteraction</c> was wrong too —
	/// and the only difference in the whole call was this argument.
	///
	/// ⚠ THE LESSON IS THE COMMENT ITSELF. The previous version of this block said, in as many
	/// words, that putting it back was a one-press experiment. Nobody ran the press for six days,
	/// because nothing was visibly broken. A deviation you have written down as unproven is still a
	/// deviation; the note is not a substitute for the press.
	///
	/// ⭐ Still untouched: <c>OpenObjectInteraction</c>, which the mouse path pairs with this. That
	/// one is the menu-opening route and is what this feature exists to avoid. The line-of-sight
	/// argument was never the thing keeping menus away.
	/// </summary>
	private unsafe void DoInteract() {
		var player = Plugin.Objects.LocalPlayer;
		if (player is null) {
			Plugin.Chat.PrintError("[Interact] no local player.");
			return;
		}

		// ⭐⭐ BEFORE both guards, and that ordering is the whole feature. Bunny MASHES this key
		// through dialogue -- the one-second floor below would swallow four presses out of five and
		// make advancing a conversation feel broken. A dialogue box is also not an interaction, so
		// neither guard has anything to say about it.
		if (Plugin.Config.InteractAdvanceTalk && TalkAdvance.TryAdvance()) {
			// ⚠ The floor IS set, though, and for a different reason than it usually serves: the press
			// that clears the last line closes the box, and the next one in a mash would otherwise fall
			// through and re-interact with the NPC you just finished talking to, reopening it. One
			// second of overspill is exactly what needs swallowing.
			this.lastInteract = DateTime.UtcNow;
			Plugin.Diag("Interact: advanced dialogue");
			return;
		}

		// ⚠ Silent, like the DrawSheathe spam guard: this fires while the key is being pressed
		// repeatedly, so a chat line per refusal would be its own kind of spam.
		if (player.IsCasting) {
			Trace("press ignored: already casting (an interaction is in progress)");
			return;
		}
		if (DateTime.UtcNow - this.lastInteract < Floor) {
			Trace($"press ignored: within {Floor.TotalSeconds:0}s of the last interact");
			return;
		}

		var (chosen, how) = Choose(player);
		if (chosen is null) {
			// ⭐⭐ PRECEDENCE, and it is a deliberate change from what the macro did. The macro fired
			// /dsuinteract AND seven /ridepillion lines every press, with /merror off swallowing whatever
			// missed. That was harmless only because the failures were invisible. Now that the pillion
			// attempt actually checks and succeeds, doing both would mount you onto a friend the moment
			// you pressed interact at a coffer they happened to be standing near.
			//
			// So: the thing in front of you wins, and a mount is what you get when there is nothing in
			// front of you. A mounted player is not one of the kinds Choose() will ever return, so these
			// two can never be competing for the same press.
			if (Plugin.Config.InteractRidePillion && PillionRider.TryRide())
				return;

			Trace("nothing interactable nearby");
			return;
		}

		// ⭐⭐ ANSWERED 2026-08-18, and the answer removed a feature rather than adding one:
		// calling InteractWithObject directly does NOT touch your target. Every interaction across a
		// full dungeon logged `target none -> none` -- keys, doors, oil, devices, coffers, a company
		// chest. So the "stuck targeting a lever until you press escape" annoyance is gone for free,
		// and the target save/restore that was going to be written is not needed at all.
		//
		// ⚠⚠ TRUE OF EVERY KIND EXCEPT ONE, found 2026-09-03. See the gathering block below: a
		// node has to BE your target, so for that kind alone this key does set it. A full dungeon of
		// evidence is still a full dungeon of ONE KIND OF THING, and "nothing needs a target" was a
		// generalisation from a sample that happened not to contain the exception.
		//
		// ⚠ The reading is KEPT rather than deleted now it has answered. It costs two string reads on
		// a keypress, and it is the canary if a patch ever makes interaction start setting the target
		// -- which would bring the original annoyance back silently.
		string before = Plugin.Targets.Target?.Name.ToString() ?? "none";

		// ⭐⭐⭐ GATHERING NODES MUST BE YOUR TARGET. Everything else in the world operates from
		// no target at all -- a whole dungeon of doors, levers and coffers proved that -- but a tree
		// does nothing unless it is targeted first. Bunny found it; deserok confirmed it in one press
		// on 2026-09-03: *"works if targetted first, confirmed."*
		//
		// ⚠ Why it hid for three weeks: the vanilla key is console-shaped and targets on the first
		// press, so nobody using it can tell the two steps apart. Ours does the whole thing in one
		// press, which is the point of it, and that is exactly what made the missing half invisible.
		//
		// ⚠⚠ THIS KIND ONLY, deliberately. Setting the target for everything would hand back the
		// "stuck targeting a lever until you press escape" annoyance that not touching it removed for
		// free. The exception is scoped to what has been demonstrated and no wider.
		//
		// ⚠ No restore afterwards, and that is not an oversight: the gathering window IS built
		// around the target, so putting the old one back would close the thing we just opened. It
		// also matches what the real key leaves behind, which is the node.
		if (chosen.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.GatheringPoint
		    && Plugin.Targets.Target?.Address != chosen.Address) {
			Plugin.Targets.Target = chosen;
			Trace($"targeted \"{chosen.Name}\" first -- gathering nodes need it");
		}

		// ⚠ Armed BEFORE the call, not after. The window is what tells GimmickConfirm a box belongs to
		// us, and nothing here guarantees the box cannot appear during the call rather than after it.
		// Arming early can only ever be harmless -- an unused window expires in three seconds.
		this.gimmicks.Arm();

		// ⚠ true, matching the key. See the note above: false silently broke gathering nodes.
		ulong result = TargetSystem.Instance()->InteractWithObject(
			(FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)chosen.Address, true);
		this.lastInteract = DateTime.UtcNow;

		string after = Plugin.Targets.Target?.Name.ToString() ?? "none";
		this.lastResult = $"{chosen.Name} ({how})";

		// ⚠⚠ `result` IS NOT A STATUS CODE. Measured 2026-08-28 across sixteen presses: it climbed
		// 102,827,260 -> 102,892,830 over sixty-six seconds, which is milliseconds, not success. It is
		// logged because it is free, and it is written down here because every plan that ever wanted to
		// "retry if the interact failed" was going to read it as one.
		Trace($"interacted with \"{chosen.Name}\" kind={chosen.ObjectKind} data={chosen.BaseId} "
			+ $"at {Vector3.Distance(chosen.Position, player.Position):0.#}y via {how} -> {result} "
			+ $"| target {before} -> {after}");
	}

	/// <summary>
	/// Which object to operate, and how it was chosen.
	///
	/// ⭐ The first two rules ask the GAME what it has already picked -- soft target, then hard target
	/// -- rather than re-deriving it. Only the last resort scans, and that scan is the one place a
	/// kind list appears.
	///
	/// ⚠⚠ That list is the allowlist shape that has bitten this project twice, so it is arranged to
	/// FAIL LOUDLY: anything nearby that is rejected gets logged, so a missing kind shows up as a
	/// named candidate rather than as "the key does nothing near that thing". Kinds seen in the
	/// recording were EventObj, Treasure and Aetheryte; the rest are added on the same evidence or
	/// not at all.
	/// </summary>
	private static (Dalamud.Game.ClientState.Objects.Types.IGameObject? Object, string How) Choose(
		Dalamud.Game.ClientState.Objects.Types.IGameObject player) {

		var soft = Plugin.Targets.SoftTarget;
		if (soft is not null && Interactable(soft))
			return (soft, "soft target");

		var hard = Plugin.Targets.Target;
		if (hard is not null && Interactable(hard))
			return (hard, "current target");

		Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
		float bestDistance = float.MaxValue;

		foreach (var candidate in Plugin.Objects) {
			float distance = System.Numerics.Vector3.Distance(candidate.Position, player.Position);
			if (distance > Reach)
				continue;
			if (!Interactable(candidate, out string why)) {
				// ⚠ Named, not skipped silently, and now WITH THE REASON -- "ignoring X" was true for
				// the gate all along and still did not say the useful part.
				Plugin.Diag($"Interact: ignoring \"{candidate.Name}\" kind={candidate.ObjectKind} "
					+ $"at {distance:0.#}y -- {why}");
				continue;
			}
			if (distance < bestDistance) {
				best = candidate;
				bestDistance = distance;
			}
		}

		return best is null ? (null, "nothing") : (best, $"nearest at {bestDistance:0.#}y");
	}

	/// <summary>Interaction reach in yalms. Generous -- the guard against reaching too far is the
	/// game refusing, not this number.</summary>
	private const float Reach = 6f;

	private static bool Interactable(Dalamud.Game.ClientState.Objects.Types.IGameObject obj) => Interactable(obj, out _);

	/// <summary>
	/// Can this key operate that, and if not, in one phrase, why not.
	///
	/// ## ⭐⭐ THE KIND CHECK WAS NEVER ENOUGH, and a gate proved it
	///
	/// The Snowcloak winch hunt ended here rather than anywhere near line of sight or targeting.
	/// deserok, watching the target arrow while testing: *"it's STILL targetting the gate, when the
	/// gate isn't interactible, THAT'S what's wrong, it shows the arrow for a split second, it's not
	/// targetting the closest... The gate isn't interactible by the player, but I bet it's coded as
	/// one."* He was right. The gate is an EventObj sitting in the object table like any other, so the
	/// kind check waved it through, and from some standing positions its origin is nearer than the
	/// winch's -- so nearest-wins picked a door nobody can open and the game refused it. Move a few
	/// feet and the winch wins again, which is exactly the maddening position-dependence that looked
	/// like a raycast and was not.
	///
	/// ⭐ His discriminator was *"this has floating text"*, and that has an exact form which is the
	/// GAME's answer rather than one of ours: <c>IsTargetable</c>. A nameplate renders for objects you
	/// can target, so "has floating text" and "is targetable" are the same fact seen from two sides.
	/// The name check backs it up for anything targetable but anonymous.
	///
	/// ## ⭐ Measured, not argued
	///
	/// Sixteen recorded presses around the winch, and the split is total:
	/// <code>
	/// "Damaged Winch"   targetable=True   view=True  screen=True   dy = +0.8 .. +1.4
	/// (empty name)      targetable=False  view=True  screen=True   dy = -0.6 .. -1.2
	/// </code>
	/// The gate carries NO NAME at all, so both new gates catch it independently. Note what does not
	/// discriminate: <c>IsObjectInViewRange</c> and <c>IsObjectOnScreen</c> are true for both, and they
	/// were the two predicates this hunt reached for first. Distance does not either -- the two sat
	/// 2.1y and 2.9y away, trading places as he moved. Only targetability separates them.
	///
	/// ⚠⚠ This is still the allowlist shape that has bitten this project twice, so it still reports
	/// every refusal with a reason rather than failing silently. A thing that ought to work and does
	/// not now says which of the three gates stopped it, by name, in one line.
	/// </summary>
	private static bool Interactable(Dalamud.Game.ClientState.Objects.Types.IGameObject obj, out string why) {
		if (obj.ObjectKind is not (Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj
			or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure
			or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte
			or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
			or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.GatheringPoint)) {
			why = $"kind {obj.ObjectKind}";
			return false;
		}

		// ⭐ The game's own verdict, not ours. This is the line that excludes the gate.
		if (!obj.IsTargetable) {
			why = "not targetable";
			return false;
		}

		// ⚠ Belt and braces for the same idea: scenery that is targetable but has nothing to show you
		// is not what you meant to press.
		if (obj.Name.TextValue.Length == 0) {
			why = "no name";
			return false;
		}

		why = string.Empty;
		return true;
	}


	/// <summary>
	/// Say what this spot looks like, and touch NOTHING.
	///
	/// ⭐⭐ Written for one specific unsolved thing: a Damaged Winch in Snowcloak that operates from
	/// some standing positions and refuses with *"Cannot see target"* from others, while the vanilla
	/// key works from all of them. deserok confirmed *"There is only one interactable: Damaged Winch"*,
	/// which rules out this key picking the wrong object -- so the game is refusing the right one, and
	/// the question is which of its own gates it is refusing on.
	///
	/// ⭐ <c>IsObjectInViewRange</c> and <c>IsObjectOnScreen</c> are the GAME's opinions, not ours, and
	/// they are free to ask. If one of them flips between a working spot and a failing one, that is the
	/// gate, and no more guessing is needed.
	///
	/// ⚠ Strictly read-only. It runs the same <see cref="Choose"/> the key runs and reports what it
	/// WOULD use, without interacting -- so it is safe to spam in a dungeon, which is the whole point of
	/// having it rather than pressing the key and reading the log afterwards.
	/// </summary>
	private unsafe void OnWhy() {
		var player = Plugin.Objects.LocalPlayer;
		if (player is null) {
			Plugin.Chat.PrintError("[Interact] no local player.");
			return;
		}

		var ts = TargetSystem.Instance();
		Plugin.Chat.Print($"[Interact] soft={Plugin.Targets.SoftTarget?.Name.ToString() ?? "none"} "
			+ $"hard={Plugin.Targets.Target?.Name.ToString() ?? "none"}");

		int seen = 0;
		foreach (var candidate in Plugin.Objects) {
			float distance = Vector3.Distance(candidate.Position, player.Position);
			if (distance > Reach)
				continue;

			seen++;
			var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)candidate.Address;
			// ⚠ data= is the BaseId, and it is here because a NAME is a bad way to find a
			// furnishing: the Armoire transfer matches on the English string and simply fails on any
			// other client. A data id is the same on every language, and this is how you read one off
			// a thing you are standing in front of rather than guessing at it.
			Plugin.Chat.Print($"  \"{candidate.Name}\" {candidate.ObjectKind} {distance:0.#}y "
				+ $"{(Interactable(candidate, out string why) ? "usable" : "IGNORED: " + why)} "
				+ $"data={candidate.BaseId} "
				+ $"view={ts->IsObjectInViewRange(go)} screen={ts->IsObjectOnScreen(go)} "
				+ $"targetable={candidate.IsTargetable}");
		}

		if (seen == 0)
			Plugin.Chat.Print("  nothing within reach");

		var (chosen, how) = Choose(player);
		Plugin.Chat.Print($"[Interact] would use: {(chosen is null ? "nothing" : $"\"{chosen.Name}\" via {how}")}");
	}

	/// <summary>Driven from Plugin's framework update, and only to give GimmickConfirm its frame.</summary>
	/// <summary>
	/// What the keybind runs, and what the command runs. One entry point, so a key and a macro
	/// cannot drift into meaning different things.
	/// </summary>
	public void Press() => this.DoInteract();

	public void Tick() {
		this.gimmicks.Tick();

		// ⚠ Moved here from DrawTab when the recorder's buttons came out of the tab. It used to
		// expire only while you were looking at it, which was fine when a button was the only way to
		// start it and wrong the moment the command became the only way.
		this.sniffer.ExpireIfDue();
	}

	private static void Trace(string message) {
		Plugin.Log.Information($"Interact: {message}");
		Plugin.Diag($"Interact: {message}");
	}

	private void OnSniff(string rest) {
		if (rest is "off" or "stop") {
			if (this.sniffer.Armed)
				this.sniffer.Disarm();
			else
				Plugin.Chat.Print("[Interact] the recorder was not running.");
			return;
		}

		if (!this.sniffer.Available) {
			Plugin.Chat.PrintError("[Interact] neither TargetSystem function resolved -- nothing to hook. See /xllog.");
			return;
		}

		TimeSpan duration = InteractSniffer.DefaultDuration;
		if (rest.Length > 0) {
			if (!int.TryParse(rest, out int minutes) || minutes <= 0) {
				Plugin.Chat.PrintError($"[Interact] \"{rest}\" is not a number of minutes. Try /dsuinteract sniff 20.");
				return;
			}
			duration = TimeSpan.FromMinutes(minutes);
		}

		this.sniffer.Arm(duration);
		Plugin.Chat.Print($"[Interact] recording for {duration.TotalMinutes:0} min. Go operate things -- levers, "
			+ "keys on the floor, wheels, aetherytes. Both presses of Confirm are logged.");
	}

	// ── the tab ──────────────────────────────────────────────────────────────────────────────

	public void DrawTab() {
		ImGui.TextWrapped(
			"A key that operates the thing in front of you and nothing else -- one press, no menus, "
			+ "no cursor landing in your inventory, and it never changes your target.");
		ImGui.Spacing();
		if (Input.KeybindPicker.Draw("interact", Plugin.Config.InteractKey))
			Plugin.Config.Save();
		ImGui.TextWrapped(
			"Bind a key. A macro on a hotbar will not work during a conversation -- the hotbar is "
			+ "locked -- so a bound key is the only way to advance dialogue with this.");
		ImGui.Spacing();
		ImGui.TextDisabled("/dsuinteract");
		ImGui.SameLine();
		if (ImGui.Button("Copy##interact_cmd"))
			ImGui.SetClipboardText("/dsuinteract");
		ImGui.SameLine();
		ImGui.TextDisabled("still works as a macro, outside conversations");
		ImGui.Spacing();
		ImGui.TextDisabled($"last: {this.lastResult}");

		Section("Why it is worth building");
		ImGui.TextWrapped(
			"Confirm serves both the world and the UI, and takes two presses for a lever, so you press "
			+ "it repeatedly. With a menu open those presses land in the menu and activate whatever is "
			+ "under the cursor. Consumables at the top of the bags means a key-and-wheel dungeon run "
			+ "can eat a raid potion during a levelling roulette.");
		ImGui.Spacing();
		ImGui.TextWrapped(
			"⚠ That makes this the only thing here that can SPEND something. Everything else in this "
			+ "plugin fails by doing nothing.");

		Section("Record it");
		Section("Guards");
		ImGui.TextWrapped(
			"Refuses while you are casting: interacting produces a cast bar, so \"am I already "
			+ "interacting\" is a question the game answers itself, correct at every animation length "
			+ "rather than at whichever one got measured.");
		ImGui.Spacing();
		ImGui.TextWrapped(
			"Aetherytes produce no cast bar, so a one-second floor between presses covers those.");

		Section("Confirmation boxes");
		bool answer = Plugin.Config.InteractAnswerGimmicks;
		if (ImGui.Checkbox("Answer the box this key causes##interact_gimmick", ref answer)) {
			Plugin.Config.InteractAnswerGimmicks = answer;
			Plugin.Config.Save();
		}
		ImGui.TextWrapped(
			$"Dungeon gimmicks and area transitions ask before they act. Only boxes this key caused, "
			+ $"and only the {this.gimmicks.KnownPrompts} the game itself lists -- discarding an item "
			+ "still asks you, and so does anything that charges a fare.");
		ImGui.Spacing();
		ImGui.TextDisabled($"last: {this.gimmicks.LastAnswer}");

		Section("Pillion");
		bool ride = Plugin.Config.InteractRidePillion;
		if (ImGui.Checkbox("Ride pillion when there is nothing to operate##interact_pillion", ref ride)) {
			Plugin.Config.InteractRidePillion = ride;
			Plugin.Config.Save();
		}
		ImGui.TextWrapped(
			"Climbs onto the nearest mounted party member instead of right-clicking them and hunting "
			+ "for Ride Pillion. Party only, and the thing in front of you always wins.");

		Section("Dialogue");
		bool talk = Plugin.Config.InteractAdvanceTalk;
		if (ImGui.Checkbox("Advance NPC dialogue##interact_talk", ref talk)) {
			Plugin.Config.InteractAdvanceTalk = talk;
			Plugin.Config.Save();
		}
		ImGui.TextWrapped(
			"One line per press, so mashing works. Choice lists are left alone -- those are menus, "
			+ "and a conversation that stops to ask you something still stops.");
	}

	/// <summary>ImGui.SeparatorText does not exist in this binding version; this is the stand-in.</summary>
	private static void Section(string title) {
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextDisabled(title);
		ImGui.Spacing();
	}

	public void Dispose() {
		Plugin.Commands.RemoveHandler("/dsuinteract");
		this.sniffer.Dispose();
		this.gimmicks.Dispose();
	}
}
