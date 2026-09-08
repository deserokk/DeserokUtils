using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;

namespace DeserokUtils.Features.FcBuffs;

internal sealed class FcBuffsFeature: IDisposable {
	private readonly FcActionRecorder recorder = new();
	private readonly FcBuffPolicy policy = new();
	private readonly FcActionActivator activator = new();

	private DateTime lastCheck = DateTime.MinValue;

	private readonly Dictionary<string, string> stockLabels = new(StringComparer.OrdinalIgnoreCase);
	private DateTime lastStockRead = DateTime.MinValue;

	public string TabTitle => "FC buffs";

	public string Summary => "Keeps your Free Company's buffs running, and says something before one lapses.";

	public FcBuffsFeature() {
		Plugin.RegisterSub("fcbuffs", "Refresh a Free Company buff now.", this.OnCommand);

		Plugin.Commands.AddHandler("/fcbuffs", new CommandInfo(this.OnCommand) {
			HelpMessage = "Refresh a Free Company buff now.",
			ShowInHelp = false,
		});
	}

	public void Tick() {
		this.recorder.Tick();

		this.activator.Tick();
		if (this.activator.Step is ActivationStep.Done or ActivationStep.Failed)
			this.FinishActivation();

		if (!Plugin.Config.FcBuffsEnabled || this.activator.Busy)
			return;

		if (DateTime.UtcNow - this.lastCheck < TimeSpan.FromSeconds(Math.Clamp(Plugin.Config.FcBuffCheckSeconds, 5, 600)))
			return;
		this.lastCheck = DateTime.UtcNow;

		this.policy.Observe();

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

		foreach (string w in wanted)
			this.policy.RecordAttempt(w);
		this.activator.Begin(wanted);
	}

	private void FinishActivation() {
		string action = this.activator.WantedAction;

		foreach (string done in this.activator.Completed) {
			int left = FcBuffReader.RowsHolding(done).Count;

			if (left == 0)
				Plugin.Announce($"That was the last {done} -- none left in the FC stock.");
			else if (left <= Plugin.Config.FcBuffLowStockWarning)
				Plugin.Chat.Print($"[FcBuffs] {done} refreshed. {left} left in stock.");
			else
				Plugin.Diag($"FcBuffs: {done} refreshed, {left} left.");
		}

		if (this.activator.Step == ActivationStep.Failed) {

			if (this.activator.FailureReason.Contains("no \"", StringComparison.Ordinal))
				Plugin.Announce($"Tried to refresh {action} -- nothing left in the FC stock.");
			else
				Plugin.Chat.PrintError($"[FcBuffs] could not refresh {action}: {this.activator.FailureReason}");
		}

		this.activator.Reset();
	}

	private static string Capitalise(string s) =>
		s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

	public void DrawDiagnostics() {

		if (!Plugin.Config.FcBuffsEnabled) {
			ImGui.TextDisabled("FC buffs is switched off -- nothing below has been read.");
			return;
		}

		var active = FcBuffReader.ActiveFamilies();

		ImGui.TextDisabled($"slots: {active.Count}/{FcBuffPolicy.MaxActive}");
		ImGui.TextDisabled(active.Count == 0
			? "nothing active"
			: $"active: {string.Join(", ", active)}");
		ImGui.TextDisabled(Plugin.Config.FcBuffActions.Count == 0
			? "no buffs chosen"
			: $"in order: {string.Join(" > ", Plugin.Config.FcBuffActions)}");

		ImGui.Spacing();

		if (!this.policy.Settled) {
			ImGui.TextDisabled(
				"settling -- the status list is not trusted yet, so the checks below are not read.");
			return;
		}

		var timers = FcBuffReader.RawTimers();
		if (timers is not null) {
			var (age, snapshot) = timers.Value;
			ImGui.TextDisabled($"agent timers (age {age}): "
				+ string.Join(", ", snapshot.Select((t, i) => $"[{i}] {FcBuffReader.LiveRemaining(t, age)}")));
		}

		ImGui.Spacing();

		foreach (var (label, met) in this.policy.Conditions()) {
			ImGui.TextColored(
				met ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f),
				$"{(met ? "yes" : "no ")}   {label}");
		}
	}

	private void OnCommand(string command, string arguments) {
		string raw = arguments.Trim();

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
				PrintSummary();
				break;
		}
	}

	private void ForceNow() {
		if (this.activator.Busy) {
			Plugin.Chat.Print($"[FcBuffs] already working: {this.activator.Step}.");
			return;
		}

		var active = FcBuffReader.ActiveFamilies();

		var wanted = Plugin.Config.FcBuffActions
			.Where(w => !active.Contains(FcBuffReader.NormaliseName(w)))
			.ToList();

		if (wanted.Count == 0) {
			Plugin.Chat.Print("[FcBuffs] every buff you asked for is already up. Nothing to do.");
			return;
		}

		Plugin.Chat.Print(
			$"[FcBuffs] activating {string.Join(", ", wanted)}...");
		this.activator.Begin(wanted);
	}

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

		Plugin.Log.Information("FcBuffs raw array 58 (index: text):");
		for (int i = 0; i < 80; i++) {
			string? s = FcBuffReader.ReadStringArray(58, i);
			if (s is null)
				break;
			Plugin.Log.Information($"  [{i,2}] \"{s}\"");
		}

		Plugin.Chat.Print("[FcBuffs] raw array 58 written to the log, empty entries included.");
	}

	private static unsafe void DumpValues() {

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

	private static void ListAddons() {
		var all = FcBuffReader.LoadedAddons();
		var hits = all.Where(n => n.Contains("ompan", StringComparison.OrdinalIgnoreCase)).ToList();

		Plugin.Chat.Print($"[FcBuffs] {all.Count} addon(s) loaded; {hits.Count} with \"compan\" in the name:");
		foreach (string n in hits)
			Plugin.Chat.Print($"  {n}");

		Plugin.Log.Information($"FcBuffs addons ({all.Count}): {string.Join(", ", all.OrderBy(n => n))}");
		Plugin.Chat.Print("[FcBuffs] full list written to dalamud.log.");
	}

	private static unsafe void OpenFcWindow() {
		var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentFreeCompany.Instance();
		if (agent is null) {
			Plugin.Chat.PrintError("[FcBuffs] the FreeCompany agent is not available.");
			return;
		}

		agent->Show();
		Plugin.Diag("opened the FC window via AgentFreeCompany.Show()");
	}

	private static void PrintSummary() {
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

	private static void Probe() {

		static void Say(string line) {
			Plugin.Chat.Print(line);
			Plugin.Log.Information(line);
		}

		var (territory, place) = FcBuffReader.CurrentPlace();
		Say($"[FcBuffs] --- probe --- {(place.Length > 0 ? place : "(unnamed)")}, territory {territory}, instance {Plugin.ClientState.Instance}");

		var statuses = FcBuffReader.PlayerStatuses();
		var actions = FcBuffReader.KnownActions();
		var names = actions.Select(a => a.Name.ToLowerInvariant()).ToHashSet();

		Say($"[FcBuffs] {statuses.Count} status(es) on you, {actions.Count} company action(s) in the sheet:");
		foreach (var s in statuses) {
			string flag = names.Contains(s.Name.ToLowerInvariant()) ? "  <-- matches a company action" : "";
			Say($"  id {s.StatusId}  \"{s.Name}\"  {s.RemainingSeconds:0.#}s{flag}");
		}

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

	public void DrawTab() {
		var cfg = Plugin.Config;

		Section("Buffs to keep up");
		ImGui.TextWrapped("Picked from the game's own company action list, so the names are never typed in.");
		ImGui.Spacing();

		var families = FcBuffReader.KnownActions()
			.Where(a => a.Purchasable)
			.GroupBy(a => FcBuffReader.NormaliseName(a.Name))
			.Select(g => g.OrderBy(a => FcBuffReader.TierOf(a.Name)).First())
			.OrderBy(a => a.Name)
			.ToList();

		if (DateTime.UtcNow - this.lastStockRead > TimeSpan.FromMilliseconds(500)) {
			this.lastStockRead = DateTime.UtcNow;
			this.stockLabels.Clear();
			foreach (var a in families) {
				var rows = FcBuffReader.RowsHolding(a.Name);
				if (rows.Count == 0)
					continue;
				string best = rows.OrderByDescending(r => r.Tier).First().Text;

				this.stockLabels[FcBuffReader.NormaliseName(a.Name)] = $"{rows.Count}x, best: {best}";
			}
		}

		int remove = -1;

		for (int i = 0; i < cfg.FcBuffActions.Count; i++) {
			string chosen = cfg.FcBuffActions[i];

			ImGui.TextUnformatted(i switch { 0 => "Primary", 1 => "Secondary", _ => "Then" });
			ImGui.SameLine(90f);

			ImGui.SetNextItemWidth(240f);
			if (ImGui.BeginCombo($"##fcb_pick{i}", chosen)) {
				foreach (var a in families) {

					bool taken = cfg.FcBuffActions
						.Where((_, n) => n != i)
						.Any(n => FcBuffReader.NormaliseName(n) == FcBuffReader.NormaliseName(a.Name));

					if (taken)
						continue;

					if (ImGui.Selectable(a.Name, FcBuffReader.NormaliseName(a.Name) == FcBuffReader.NormaliseName(chosen))) {
						cfg.FcBuffActions[i] = a.Name;
						cfg.Save();
					}
				}

				ImGui.EndCombo();
			}

			ImGui.SameLine();
			if (this.stockLabels.TryGetValue(FcBuffReader.NormaliseName(chosen), out string? stock))
				ImGui.TextDisabled(stock);
			else if (FcBuffReader.InactiveCount() is null)
				ImGui.TextDisabled("open the FC window");
			else
				ImGui.TextDisabled("none in stock");

			ImGui.SameLine();
			if (ImGui.SmallButton($"x##fcb_del{i}"))
				remove = i;
		}

		if (remove >= 0) {
			cfg.FcBuffActions.RemoveAt(remove);
			cfg.Save();
		}

		var unchosen = families
			.Where(a => !cfg.FcBuffActions.Any(n => FcBuffReader.NormaliseName(n) == FcBuffReader.NormaliseName(a.Name)))
			.ToList();

		if (unchosen.Count > 0 && ImGui.Button("+##fcb_add")) {
			cfg.FcBuffActions.Add(unchosen[0].Name);
			cfg.Save();
		}

		if (cfg.FcBuffActions.Count > FcBuffPolicy.MaxActive) {
			ImGui.TextDisabled(
				$"Only {FcBuffPolicy.MaxActive} can be up at once. The rest are used when one of those"
				+ " has run out of stock.");
		}

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
