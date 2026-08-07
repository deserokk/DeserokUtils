using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

namespace DeserokUtils.Features.FcBuffs;

/// <summary>
/// Keep the FC buffs running, because the failure is never "we did not know how" -- it is eight
/// hours of grinding before anybody looks up.
///
/// ⚠⚠ READING ONLY at this stage. Nothing here presses anything yet, on purpose: the activation
/// half depends on facts that only the running game can settle (see the tab), and the last time
/// this project guessed between three candidate causes it rewrote the half that was already
/// correct. Instrument first, then act.
/// </summary>
internal sealed class FcBuffsFeature: IDisposable {
	private readonly FcActionRecorder recorder = new();
	private readonly FcBuffPolicy policy = new();
	private readonly FcActionActivator activator = new();

	/// <summary>⚠ Throttle. The framework tick is every frame; none of this needs to be.</summary>
	private DateTime lastCheck = DateTime.MinValue;

	/// <summary>
	/// Stock per buff family, for the tab's display only.
	///
	/// ⚠⚠ Cached because the tab is a LOOP. Reading stock walks the UI string array per family, and
	/// doing that for every row on every frame is the same mistake as the sheet walks that tanked
	/// the framerate -- committed again, in the fix for it, about ten minutes later. Draw code is
	/// where innocent-looking reads become sixty-per-second reads.
	/// </summary>
	private readonly Dictionary<string, string> stockLabels = new(StringComparer.OrdinalIgnoreCase);
	private DateTime lastStockRead = DateTime.MinValue;

	public string TabTitle => "FC buffs";

	public FcBuffsFeature() {
		Plugin.Commands.AddHandler("/fcbuffs", new CommandInfo(this.OnCommand) {
			HelpMessage = "/fcbuffs [now | probe | actions | strings | addons | here | open | record [filter]] -- refresh a buff now, or inspect what the plugin can see.",
		});
	}

	public void Tick() {
		this.recorder.Tick();

		// An activation in flight steps every frame -- it is waiting on addons, which appear on
		// frame boundaries. Everything else is throttled.
		this.activator.Tick();
		if (this.activator.Step is ActivationStep.Done or ActivationStep.Failed)
			this.FinishActivation();

		if (!Plugin.Config.FcBuffsEnabled || this.activator.Busy)
			return;

		if (DateTime.UtcNow - this.lastCheck < TimeSpan.FromSeconds(Math.Clamp(Plugin.Config.FcBuffCheckSeconds, 5, 600)))
			return;
		this.lastCheck = DateTime.UtcNow;

		this.policy.Observe();

		// ⭐ The drop event, announced only when the plugin CANNOT fix it itself. In a city it just
		// gets refreshed below and there is nothing to report; in the field the refresh is not
		// possible and that is exactly the moment worth interrupting for.
		foreach (string dropped in this.policy.JustDropped) {
			if (!FcBuffPolicy.InSafePlace())
				Plugin.Announce($"{Capitalise(dropped)} just ran out -- no refresh out here.");
			else
				Plugin.Diag($"FcBuffs: {dropped} dropped, refreshing.");
		}

		if (!FcBuffPolicy.InSafePlace())
			return;

		var wanted = this.policy.AllToActivate();
		if (wanted.Count == 0)
			return;

		// ⭐ Everything missing goes in one window session, rather than one window per buff.
		foreach (string w in wanted)
			this.policy.RecordAttempt(w);
		this.activator.Begin(wanted);
	}

	/// <summary>
	/// Reports the outcome once, then clears. Both of deserok's asked-for warnings live here.
	/// </summary>
	private void FinishActivation() {
		string action = this.activator.WantedAction;

		// ⚠ Stock has to be read while the window is still up -- by the time this runs on the Done
		// path the window is closing, so the counts come from the last read. Reported per buff
		// because "which one ran out" is the whole point of the warning.
		foreach (string done in this.activator.Completed) {
			int left = FcBuffReader.RowsHolding(done).Count;

			// ⚠⚠ "Ran out" is announced, "getting low" is not -- the first is a thing that has
			// happened and the second is a thing that might. An alert for every dwindling buff is an
			// alert that gets tuned out, and this plugin exists because something already was.
			if (left == 0)
				Plugin.Announce($"That was the last {done} -- none left in the FC stock.");
			else if (left <= Plugin.Config.FcBuffLowStockWarning)
				Plugin.Chat.Print($"[FcBuffs] {done} refreshed. {left} left in stock.");
			else
				Plugin.Diag($"FcBuffs: {done} refreshed, {left} left.");
		}

		if (this.activator.Step == ActivationStep.Failed) {
			// The case worth interrupting for: doing the right thing and still leaving him unbuffed.
			if (this.activator.FailureReason.Contains("no \"", StringComparison.Ordinal))
				Plugin.Announce($"Tried to refresh {action} -- nothing left in the FC stock.");
			else
				Plugin.Chat.PrintError($"[FcBuffs] could not refresh {action}: {this.activator.FailureReason}");
		}

		this.activator.Reset();
	}

	private static string Capitalise(string s) =>
		s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

	private void OnCommand(string command, string arguments) {
		string raw = arguments.Trim();

		// "record" optionally carries the addon-name filter: /fcbuffs record FreeCompany
		if (raw.StartsWith("record", StringComparison.OrdinalIgnoreCase)
			|| raw.StartsWith("rec", StringComparison.OrdinalIgnoreCase)) {
			int sp = raw.IndexOf(' ');
			this.recorder.Toggle(sp > 0 ? raw[(sp + 1)..].Trim() : string.Empty);
			return;
		}

		switch (raw.ToLowerInvariant()) {
			case "probe" or "dump":
				Probe();
				break;

			case "actions" or "list":
				ListActions();
				break;

			case "here":
				AddHere();
				break;

			case "addons":
				ListAddons();
				break;

			case "values":
				DumpValues();
				break;

			case "strings":
				DumpStrings();
				break;

			case "now":
				this.ForceNow();
				break;

			case "open":
				OpenFcWindow();
				break;

			default:
				Summary();
				break;
		}
	}

	/// <summary>
	/// Runs one activation immediately, ignoring the settle clock and the safe-place list.
	///
	/// ⚠ The gates exist for the AUTOMATIC path, where nobody is watching. Asking for it by hand is
	/// somebody watching, so the gates would only be in the way -- but the dry-run flag still
	/// applies, because that one is about whether the payload is right, not about timing.
	/// </summary>
	private void ForceNow() {
		if (this.activator.Busy) {
			Plugin.Chat.Print($"[FcBuffs] already working: {this.activator.Step}.");
			return;
		}

		// ⚠ Read the active set ONCE. Calling this inside the predicate re-reads the status list per
		// candidate, which is the same shape of mistake as the per-frame sheet walks.
		var active = FcBuffReader.ActiveFamilies();

		var wanted = Plugin.Config.FcBuffActions
			.Where(w => !active.Contains(FcBuffReader.NormaliseName(w)))
			.ToList();

		if (wanted.Count == 0) {
			Plugin.Chat.Print("[FcBuffs] every buff you asked for is already up. Nothing to do.");
			return;
		}

		Plugin.Chat.Print(
			$"[FcBuffs] activating {string.Join(", ", wanted)}{(Plugin.Config.FcBuffsDryRun ? " (DRY RUN)" : "")}...");
		this.activator.Begin(wanted);
	}

	/// <summary>
	/// Finds where the action list's text actually lives, so a row index can be turned into a name.
	/// </summary>
	private static void DumpStrings() {
		var hits = FcBuffReader.FindActionStrings();
		if (hits.Count == 0) {
			Plugin.Chat.PrintError(
				"[FcBuffs] no company action names found in any string array. Is the FC action window open?");
			return;
		}

		Plugin.Chat.Print($"[FcBuffs] found {hits.Count} action name(s); full dump in dalamud.log.");
		foreach (var (array, index, text) in hits)
			Plugin.Log.Information($"FcBuffs strings: array={array} entry={index} text=\"{text}\"");

		// ⭐ Dump the WHOLE list array, empties included. The matching scan above only ever shows
		// entries that look like actions, which is precisely why a stale leftover is invisible to it
		// -- a ghost row names a real buff. Seeing the raw shape is the only way to find out what,
		// if anything, marks the live end of the list.
		Plugin.Log.Information("FcBuffs raw array 58 (index: text):");
		for (int i = 0; i < 80; i++) {
			string? s = FcBuffReader.ReadStringArray(58, i);
			if (s is null)
				break;
			Plugin.Log.Information($"  [{i,2}] \"{s}\"");
		}

		// The odd entries between the names are unexplored, and one of them may be what separates a
		// live row from a leftover.
		Plugin.Chat.Print("[FcBuffs] raw array 58 written to the log, empty entries included.");
	}

	/// <summary>
	/// Dumps the FreeCompanyAction window's own AtkValues while it is open.
	///
	/// ⭐ READS the list instead of predicting it. The predictor that used to live here was wrong in
	/// a way no amount of care would have fixed: it rebuilt the list from the CompanyAction sheet,
	/// one row per action, and the real window is an inventory of owned items with duplicates in it.
	/// The window already knows its own contents -- ask it.
	/// </summary>
	private static unsafe void DumpValues() {
		// ⚠ GetAddonByName hands back an AtkUnitBasePtr wrapper on this API, not a raw pointer.
		var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)
			Plugin.GameGui.GetAddonByName("FreeCompanyAction").Address;
		if (addon is null) {
			Plugin.Chat.PrintError("[FcBuffs] FreeCompanyAction is not open. Open the FC window and pick Actions first.");
			return;
		}

		Plugin.Chat.Print($"[FcBuffs] FreeCompanyAction has {addon->AtkValuesCount} AtkValue(s); written to dalamud.log.");
		Plugin.Log.Information(
			$"FcBuffs values: count={addon->AtkValuesCount} [{FcActionRecorder.DescribeValues(addon->AtkValuesCount, addon->AtkValues)}]");
	}

	/// <summary>
	/// Names every loaded addon, so the recorder's filter is set from what the game actually calls
	/// its windows rather than from a guess.
	///
	/// ⚠ LOADED, not visible. The limit-break gauge is loaded and firing events in Ul'dah, where it
	/// is never drawn -- so this list is longer than the screen and that is not a bug in it.
	/// </summary>
	private static void ListAddons() {
		var all = FcBuffReader.LoadedAddons();
		var hits = all.Where(n => n.Contains("ompan", StringComparison.OrdinalIgnoreCase)).ToList();

		Plugin.Chat.Print($"[FcBuffs] {all.Count} addon(s) loaded; {hits.Count} with \"compan\" in the name:");
		foreach (string n in hits)
			Plugin.Chat.Print($"  {n}");

		// The full list only to the log -- a few hundred names would bury the answer in chat.
		Plugin.Log.Information($"FcBuffs addons ({all.Count}): {string.Join(", ", all.OrderBy(n => n))}");
		Plugin.Chat.Print("[FcBuffs] full list written to dalamud.log.");
	}

	/// <summary>
	/// Opens the Free Company window through the agent that owns it.
	///
	/// ⭐ Not a keybind and not a text command. The agent exposes Show() directly, so there is no
	/// key to be rebound, no command to be localised, and nothing to lose a race with.
	/// </summary>
	private static unsafe void OpenFcWindow() {
		var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentFreeCompany.Instance();
		if (agent is null) {
			Plugin.Chat.PrintError("[FcBuffs] the FreeCompany agent is not available.");
			return;
		}

		agent->Show();
		Plugin.Diag("opened the FC window via AgentFreeCompany.Show()");
	}

	/// <summary>What is on right now, one line per STATUS -- see FcBuffReader.ActiveFamilies.</summary>
	private static void Summary() {
		var active = FcBuffReader.ActiveStatuses();
		if (active.Count == 0) {
			Plugin.Chat.Print("[FcBuffs] no FC buffs detected on you right now.");
			return;
		}

		foreach (var status in active)
			Plugin.Chat.Print($"[FcBuffs] {status.Name} is up.");
	}

	private static void ListActions() {
		var actions = FcBuffReader.KnownActions();
		Plugin.Chat.Print($"[FcBuffs] {actions.Count} company action(s) in the game data:");
		foreach (var a in actions.OrderBy(a => a.Name))
			Plugin.Chat.Print($"  {a.Name}  (id {a.RowId}, {a.Cost} credits, rank {a.RankRequired}{(a.Purchasable ? "" : ", not purchasable")})");
	}

	private static void AddHere() {
		var (territory, place) = FcBuffReader.CurrentPlace();
		if (place.Length == 0) {
			// ⚠ Territory reads 0 across a loading screen -- the same trap FateWatch hit with anchors.
			Plugin.Chat.PrintError($"[FcBuffs] this place has no name yet (territory {territory}). Wait for the zone to finish loading.");
			return;
		}

		if (Plugin.Config.FcBuffSafePlaces.Contains(place, StringComparer.OrdinalIgnoreCase)) {
			Plugin.Chat.Print($"[FcBuffs] \"{place}\" is already on the safe list.");
			return;
		}

		Plugin.Config.FcBuffSafePlaces.Add(place);
		Plugin.Config.Save();
		Plugin.Chat.Print($"[FcBuffs] added \"{place}\" (territory {territory}) to the safe list.");
	}

	/// <summary>
	/// The one command this whole feature is currently for. Dumps every source at once so the three
	/// open questions get answered together rather than one guess at a time:
	///   1. do FC buffs appear in the player's status list, with a real countdown?
	///   2. do the agent's three timers agree with them once the age is subtracted?
	///   3. what unit are the agent timers in?
	/// </summary>
	private static void Probe() {
		// ⭐ Every line goes to BOTH chat and dalamud.log. Chat is where you read it now; the log is
		// what survives the session and can be grepped afterwards -- and a diagnostic that only
		// exists in a chat buffer has to be transcribed by hand to be any use to anyone.
		static void Say(string line) {
			Plugin.Chat.Print(line);
			Plugin.Log.Information(line);
		}

		var (territory, place) = FcBuffReader.CurrentPlace();
		Say($"[FcBuffs] --- probe --- {(place.Length > 0 ? place : "(unnamed)")}, territory {territory}, instance {Plugin.ClientState.Instance}");

		// (1) The status list -- the candidate that identifies WHICH buff.
		var statuses = FcBuffReader.PlayerStatuses();
		var actions = FcBuffReader.KnownActions();
		var names = actions.Select(a => a.Name.ToLowerInvariant()).ToHashSet();

		Say($"[FcBuffs] {statuses.Count} status(es) on you, {actions.Count} company action(s) in the sheet:");
		foreach (var s in statuses) {
			string flag = names.Contains(s.Name.ToLowerInvariant()) ? "  <-- matches a company action" : "";
			Say($"  id {s.StatusId}  \"{s.Name}\"  {s.RemainingSeconds:0.#}s{flag}");
		}

		// (2)+(3) The agent timers -- the candidate that gives a DURATION, in an unknown unit, with
		// no way of its own to say which action each slot belongs to.
		var raw = FcBuffReader.RawTimers();
		if (raw is null) {
			Say("[FcBuffs] the FreeCompany agent is not available (null).");
		}
		else {
			var (age, snapshot) = raw.Value;
			Say($"[FcBuffs] agent timers -- TimeSinceUpdate (age) = {age}");
			for (int i = 0; i < snapshot.Length; i++)
				Say($"  slot {i}: snapshot {snapshot[i]}, live {FcBuffReader.LiveRemaining(snapshot[i], age)} "
					+ $"(= {TimeSpan.FromSeconds(FcBuffReader.LiveRemaining(snapshot[i], age)):h\\:mm\\:ss} if seconds)");
		}

		Say("[FcBuffs] run this once with the FC window shut, then open the FC window and run it again. "
			+ "If the snapshot changes only on the second run, the age subtraction is doing real work.");
	}

	// ── the tab ──────────────────────────────────────────────────────────────────────────────

	public void DrawTab() {
		var cfg = Plugin.Config;

		ImGui.TextWrapped(
			"Keeps the FC buffs running. The problem was never knowing how to switch them on -- it is "
			+ "grinding for eight hours and only then looking up.");
		ImGui.Spacing();

		bool on = cfg.FcBuffsEnabled;
		if (ImGui.Checkbox("Keep them up automatically", ref on)) { cfg.FcBuffsEnabled = on; cfg.Save(); }

		bool dry = cfg.FcBuffsDryRun;
		if (ImGui.Checkbox("Dry run (activate nothing)", ref dry)) { cfg.FcBuffsDryRun = dry; cfg.Save(); }

		if (dry) {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.3f, 1f));
			ImGui.TextWrapped(
				"Dry run is ON. Nothing is activated and no stock is consumed -- but it does open the "
				+ "FC window and switch to Actions, because it cannot check the row without looking "
				+ "at the list. A rehearsal that stops before the interesting part rehearses nothing.");
			ImGui.PopStyleColor();
			ImGui.TextDisabled("What it would have activated goes to dalamud.log. Read one before turning this off.");
		}

		ImGui.TextDisabled($"State: {this.activator.Step}"
			+ (this.policy.Settled ? ", settled" : ", not settled")
			+ (FcBuffPolicy.InSafePlace() ? ", safe place" : ", not a safe place"));
		ImGui.TextDisabled("/fcbuffs now runs one immediately, ignoring the gates.");

		Section("What is on right now");
		var active = FcBuffReader.ActiveStatuses();
		if (active.Count == 0)
			ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), "No FC buffs detected.");
		else
			foreach (var status in active)
				ImGui.TextColored(new Vector4(0.55f, 0.9f, 0.55f, 1f), status.Name);

		// ⚠ No duration shown, deliberately. The status list reports a fixed 30s for these, so any
		// number here would be a confident lie -- and it previously listed the same buff three times
		// because three sheet tiers matched one status.
		ImGui.TextDisabled("Presence only -- the game does not expose a usable countdown for these.");

		var raw = FcBuffReader.RawTimers();
		if (raw is not null) {
			var (age, snapshot) = raw.Value;
			ImGui.Spacing();
			ImGui.TextDisabled($"agent timers (age {age}): "
				+ string.Join(", ", snapshot.Select((s, i) => $"[{i}] {FcBuffReader.LiveRemaining(s, age)}")));
			ImGui.TextDisabled("Snapshot minus age -- the raw array is a value frozen at the last agent update.");
		}

		Section("Buffs to keep up");
		ImGui.TextWrapped("Picked from the game's own company action list, so the names are never typed in.");
		ImGui.Spacing();

		// ⭐ One row per FAMILY, not per tier. Listing Heat of Battle I, II and III as three separate
		// choices asks which tier to use, and there is no version of that question deserok answers
		// with anything but "the best one I have".
		var families = FcBuffReader.KnownActions()
			.Where(a => a.Purchasable)
			.GroupBy(a => FcBuffReader.NormaliseName(a.Name))
			.Select(g => g.OrderBy(a => FcBuffReader.TierOf(a.Name)).First())
			.OrderBy(a => a.Name)
			.ToList();

		// Twice a second is plenty for a number that only changes when something is bought or spent.
		if (DateTime.UtcNow - this.lastStockRead > TimeSpan.FromMilliseconds(500)) {
			this.lastStockRead = DateTime.UtcNow;
			this.stockLabels.Clear();
			foreach (var a in families) {
				var rows = FcBuffReader.RowsHolding(a.Name);
				if (rows.Count == 0)
					continue;
				string best = rows.OrderByDescending(r => r.Tier).First().Text;
				// Exact again: the row scan is now bounded by the game's own count, so the ghost rows
				// past the live end are no longer included.
				this.stockLabels[a.Name] = $"{rows.Count}x, best: {best}";
			}
		}

		if (ImGui.BeginTable("fcb_actions", 3,
			ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
			new Vector2(0, 180f))) {
			ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 28f);
			ImGui.TableSetupColumn("buff");
			ImGui.TableSetupColumn("in stock", ImGuiTableColumnFlags.WidthFixed, 150f);
			ImGui.TableHeadersRow();

			foreach (var a in families) {
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				bool want = cfg.FcBuffActions.Any(n => FcBuffReader.NormaliseName(n) == FcBuffReader.NormaliseName(a.Name));
				if (ImGui.Checkbox($"##fcb{a.RowId}", ref want)) {
					if (want)
						cfg.FcBuffActions.Add(a.Name);
					else
						cfg.FcBuffActions.RemoveAll(n =>
							FcBuffReader.NormaliseName(n) == FcBuffReader.NormaliseName(a.Name));
					cfg.Save();
				}

				ImGui.TableNextColumn();
				ImGui.TextUnformatted(a.Name);

				// ⚠ Only readable while the FC action window is open -- the list lives in a UI array
				// that does not exist otherwise. Says so rather than showing a confident zero.
				ImGui.TableNextColumn();
				if (this.stockLabels.TryGetValue(a.Name, out string? label))
					ImGui.TextUnformatted(label);
				else
					ImGui.TextDisabled("open the FC window");
			}
			ImGui.EndTable();
		}

		Section("Where it is allowed to act");
		ImGui.TextWrapped(
			"Cities and residential districts only. Stored as place names and resolved against the "
			+ "game's territory sheet, so there are no hand-typed zone numbers to be wrong -- and "
			+ "anything that fails to resolve is logged rather than silently skipped.");
		ImGui.Spacing();

		string? removePlace = null;
		foreach (string p in cfg.FcBuffSafePlaces.ToList()) {
			var ids = FcBuffReader.ResolveTerritories(p);
			ImGui.TextUnformatted(p);
			ImGui.SameLine();
			if (ids.Count == 0)
				ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), "-- no such place in the sheet");
			else
				ImGui.TextDisabled($"-- territory {string.Join(", ", ids)}");
			ImGui.SameLine();
			if (ImGui.SmallButton($"x##rmp{p}"))
				removePlace = p;
		}
		if (removePlace is not null) {
			cfg.FcBuffSafePlaces.Remove(removePlace);
			cfg.Save();
		}

		ImGui.Spacing();
		var (territory, place) = FcBuffReader.CurrentPlace();
		ImGui.TextDisabled($"You are in: {(place.Length > 0 ? place : "(unnamed)")} (territory {territory}).");
		if (place.Length > 0 && !cfg.FcBuffSafePlaces.Contains(place, StringComparer.OrdinalIgnoreCase)) {
			if (ImGui.Button($"Add \"{place}\"")) {
				cfg.FcBuffSafePlaces.Add(place);
				cfg.Save();
			}
			ImGui.SameLine();
			ImGui.TextDisabled("(or /fcbuffs here)");
		}

		Section("What is measured, and what is assumed");
		ImGui.TextWrapped(
			"Measured: the buffs appear in your status list, which is where 'is it on' comes from. "
			+ "The two callbacks that activate one were recorded from real clicks. The row index was "
			+ "cross-referenced against six of them.");
		ImGui.Spacing();
		ImGui.TextWrapped(
			"Assumed: that the list lives in string array 58, from entry 8, every second entry. "
			+ "Nothing in the game data says so and a patch can move all three -- so the row is "
			+ "re-read and checked against the buff's name immediately before being fired at. If "
			+ "those numbers move, this refuses and says so rather than consuming the wrong action.");
		ImGui.Spacing();
		ImGui.TextDisabled("Durations are deliberately unused. Presence only -- see /fcbuffs probe for why.");
	}

	private static void Section(string title) {
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextDisabled(title);
		ImGui.Spacing();
	}

	public void Dispose() {
		Plugin.Commands.RemoveHandler("/fcbuffs");
		this.recorder.Dispose();
	}
}
