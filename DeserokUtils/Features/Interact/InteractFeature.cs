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
	/// </summary>
	private static readonly TimeSpan Floor = TimeSpan.FromSeconds(1);

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
	/// ⭐ Calls <c>InteractWithObject(obj, checkLineOfSight: false)</c>.
	///
	/// ⚠⚠ It passed <c>true</c> from the first version until 2026-08-28, because that is what the
	/// recording showed Num0 doing, and that was WRONG -- not about what the game calls, but about
	/// what our call has to survive. deserok found a Damaged Winch he was standing on top of that
	/// refused with *"Cannot see target"*, then explained why the real key does not: *"pressing the
	/// normal interact key simply targets it normally, then allows interacting"*. Vanilla is two
	/// presses, and the first one is the game deciding the thing is reachable. This key deliberately
	/// never targets (see below), so it arrives at the same call without that step having run, and a
	/// wall-mounted object whose origin sits inside the wall fails a raycast the real key never had
	/// to pass. Dropping the check restores the vanilla OUTCOME rather than the vanilla call.
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
			Trace("nothing interactable nearby");
			return;
		}

		// ⭐⭐ ANSWERED 2026-08-18, and the answer removed a feature rather than adding one:
		// calling InteractWithObject directly does NOT touch your target. Every interaction across a
		// full dungeon logged `target none -> none` -- keys, doors, oil, devices, coffers, a company
		// chest. So the "stuck targeting a lever until you press escape" annoyance is gone for free,
		// and the target save/restore that was going to be written is not needed at all.
		//
		// ⚠ The reading is KEPT rather than deleted now it has answered. It costs two string reads on
		// a keypress, and it is the canary if a patch ever makes interaction start setting the target
		// -- which would bring the original annoyance back silently.
		string before = Plugin.Targets.Target?.Name.ToString() ?? "none";

		// ⚠ Armed BEFORE the call, not after. The window is what tells GimmickConfirm a box belongs to
		// us, and nothing here guarantees the box cannot appear during the call rather than after it.
		// Arming early can only ever be harmless -- an unused window expires in three seconds.
		this.gimmicks.Arm();

		ulong result = TargetSystem.Instance()->InteractWithObject(
			(FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)chosen.Address, false);
		this.lastInteract = DateTime.UtcNow;

		string after = Plugin.Targets.Target?.Name.ToString() ?? "none";
		this.lastResult = $"{chosen.Name} ({how})";

		// ⚠ BaseId and distance are here for ONE open question: deserok reports a class of object that
		// refuses with "you cannot see the object", which is the checkLineOfSight:true argument above
		// failing. It has never happened somewhere testable. Without the base id the specimen cannot be
		// looked up in the EObj sheet afterwards, and without the distance we cannot tell a real
		// occlusion from an object whose origin sits inside the floor. Both are free on a keypress.
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
			if (!Interactable(candidate)) {
				// ⚠ Named, not skipped silently. This is how a missing ObjectKind surfaces.
				Plugin.Diag($"Interact: ignoring \"{candidate.Name}\" kind={candidate.ObjectKind} at {distance:0.#}y");
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

	private static bool Interactable(Dalamud.Game.ClientState.Objects.Types.IGameObject obj) =>
		obj.ObjectKind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj
			or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure
			or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte
			or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
			or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.GatheringPoint;


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
			Plugin.Chat.Print($"  \"{candidate.Name}\" {candidate.ObjectKind} {distance:0.#}y "
				+ $"{(Interactable(candidate) ? "usable" : "IGNORED")} "
				+ $"view={ts->IsObjectInViewRange(go)} screen={ts->IsObjectOnScreen(go)} "
				+ $"targetable={candidate.IsTargetable}");
		}

		if (seen == 0)
			Plugin.Chat.Print("  nothing within reach");

		var (chosen, how) = Choose(player);
		Plugin.Chat.Print($"[Interact] would use: {(chosen is null ? "nothing" : $"\"{chosen.Name}\" via {how}")}");
	}

	/// <summary>Driven from Plugin's framework update, and only to give GimmickConfirm its frame.</summary>
	public void Tick() => this.gimmicks.Tick();

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
		this.sniffer.ExpireIfDue();

		ImGui.TextWrapped(
			"A key that operates the thing in front of you and nothing else -- one press, no menus, "
			+ "no cursor landing in your inventory, and it never changes your target.");
		ImGui.Spacing();
		ImGui.TextUnformatted("/dsuinteract");
		if (ImGui.Button("Copy##interact_cmd"))
			ImGui.SetClipboardText("/dsuinteract");
		ImGui.SameLine();
		ImGui.TextDisabled("one line in a macro, dragged to a hotbar slot");
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
		if (ImGui.Button("What is here?##interact_why"))
			this.OnWhy();
		ImGui.SameLine();
		ImGui.TextDisabled("or /dsuinteract why -- reads the spot, touches nothing");

		if (!this.sniffer.Available) {
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "neither TargetSystem function resolved. See /xllog.");
		}
		else if (this.sniffer.Armed) {
			ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"recording -- {this.sniffer.Remaining.TotalMinutes:0.#} min left");
			ImGui.SameLine();
			if (ImGui.Button("Stop##interact_sniff"))
				this.sniffer.Disarm();
		}
		else {
			if (ImGui.Button("Record for 10 min##interact_sniff"))
				this.sniffer.Arm(InteractSniffer.DefaultDuration);
			ImGui.SameLine();
			ImGui.TextDisabled("or /dsuinteract sniff");
		}
		ImGui.Spacing();
		ImGui.TextWrapped(
			"Then go operate things: a lever, a key on the floor, a wheel, an aetheryte, an NPC. Both "
			+ "presses of Confirm are logged, so the target-then-use split shows up too.");

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
			$"Dungeon gimmicks ask before they act. Only boxes this key caused, and only the "
			+ $"{this.gimmicks.KnownPrompts} the game itself lists as gimmicks -- discarding an item "
			+ "still asks you.");
		ImGui.Spacing();
		ImGui.TextDisabled($"last: {this.gimmicks.LastAnswer}");

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
