using System;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.FcBuffs;

internal enum ActivationStep {
	Idle,
	Opening,
	SelectingTab,
	Reading,
	Executing,
	Confirming,
	Settling,
	Closing,
	Done,
	Failed,
}

internal sealed unsafe class FcActionActivator {

	private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(8);

	private const int SettleTicks = 30;

	public ActivationStep Step { get; private set; } = ActivationStep.Idle;
	public string WantedAction { get; private set; } = string.Empty;
	public string FailureReason { get; private set; } = string.Empty;

	private readonly Queue<string> queue = new();

	public List<string> Completed { get; } = new();

	private DateTime stepStarted;
	private int waited;
	private int firedRow = -1;

	private bool weOpenedIt;

	public bool Busy => this.Step is not (ActivationStep.Idle or ActivationStep.Done or ActivationStep.Failed);

	public void Begin(IEnumerable<string> wanted) {
		this.queue.Clear();
		this.Completed.Clear();
		foreach (string w in wanted)
			this.queue.Enqueue(w);

		if (this.queue.Count == 0)
			return;

		this.WantedAction = this.queue.Dequeue();
		this.FailureReason = string.Empty;
		this.firedRow = -1;
		this.Enter(ActivationStep.Opening);
		Plugin.Log.Information(
			$"FcBuffs: activation BEGIN for \"{this.WantedAction}\""
			+ (this.queue.Count > 0 ? $" (+{this.queue.Count} more this session)" : ""));
	}

	public void Reset() {
		this.Step = ActivationStep.Idle;
		this.waited = 0;
	}

	private void Enter(ActivationStep step) {
		this.Step = step;
		this.stepStarted = DateTime.UtcNow;
		this.waited = 0;
	}

	private void Fail(string reason) {
		this.FailureReason = reason;
		this.Step = ActivationStep.Failed;
		Plugin.Log.Warning($"FcBuffs: activation FAILED -- {reason}");

		foreach (string name in new[] { "ContextMenu", "SelectYesno" }) {
			var addon = Addon(name);
			if (addon is not null)
				addon->FireCloseCallback();
		}

		if (this.weOpenedIt)
			CloseWindow();
	}

	private static string DescribeMenu(AtkUnitBase* menu) =>
		menu is null ? "(null)" : FcActionRecorder.DescribeValues(menu->AtkValuesCount, menu->AtkValues);

	private static AtkUnitBase* Addon(string name) =>
		(AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;

	public void Tick() {
		if (!this.Busy)
			return;

		if (DateTime.UtcNow - this.stepStarted > StepTimeout) {
			this.Fail($"step {this.Step} timed out");
			return;
		}

		switch (this.Step) {
			case ActivationStep.Opening: this.TickOpening(); break;
			case ActivationStep.SelectingTab: this.TickSelectingTab(); break;
			case ActivationStep.Reading: this.TickReading(); break;
			case ActivationStep.Executing: this.TickExecuting(); break;
			case ActivationStep.Confirming: this.TickConfirming(); break;
			case ActivationStep.Settling: this.TickSettling(); break;
			case ActivationStep.Closing: this.TickClosing(); break;
		}
	}

	private void TickOpening() {

		if (Addon("FreeCompanyAction") is not null) {
			this.Enter(ActivationStep.Reading);
			return;
		}

		if (this.waited++ == 0) {
			var agent = AgentFreeCompany.Instance();
			if (agent is null) {
				this.Fail("the FreeCompany agent is unavailable");
				return;
			}

			this.weOpenedIt = Addon("FreeCompany") is null;
			agent->Show();
			Plugin.Diag("FcBuffs: asked AgentFreeCompany to show");
		}

		if (this.waited > SettleTicks && Addon("FreeCompany") is not null)
			this.Enter(ActivationStep.SelectingTab);
	}

	private void TickSelectingTab() {

		if (Addon("FreeCompanyAction") is not null) {
			this.Enter(ActivationStep.Reading);
			return;
		}

		var fc = Addon("FreeCompany");
		if (fc is null) {
			this.Fail("the FC window closed while selecting the Actions tab");
			return;
		}

		if (this.waited++ != SettleTicks)
			return;

		var dismiss = stackalloc AtkValue[1];
		dismiss[0].Type = AtkValueType.Int;
		dismiss[0].Int = -2;

		foreach (string name in FcBuffReader.LoadedAddons()) {
			if (!name.StartsWith("FreeCompany", StringComparison.Ordinal)
				|| name is "FreeCompany" or "FreeCompanyAction")
				continue;

			var panel = Addon(name);
			if (panel is null)
				continue;

			panel->FireCallback(1, dismiss, false);
			Plugin.Diag($"FcBuffs: dismissed panel {name}");
		}

		var values = stackalloc AtkValue[2];
		values[0].Type = AtkValueType.Int;
		values[0].Int = 0;
		values[1].Type = AtkValueType.UInt;
		values[1].UInt = 4;
		fc->FireCallback(2, values, true);

		Plugin.Log.Information("FcBuffs: selected the Actions tab");
	}

	private void TickReading() {
		if (this.waited++ < SettleTicks)
			return;

		var best = FcBuffReader.BestRowFor(this.WantedAction);
		if (best is null) {
			this.Fail($"no \"{this.WantedAction}\" in the inactive list");
			return;
		}

		var (row, tier, chosen) = best.Value;

		string? text = FcBuffReader.ReadListEntry(row);
		if (text is null || FcBuffReader.NormaliseName(text) != FcBuffReader.NormaliseName(this.WantedAction)) {
			this.Fail($"row {row} reads \"{text ?? "null"}\", expected \"{this.WantedAction}\"");
			return;
		}

		Plugin.Diag($"FcBuffs: picked tier {tier} \"{chosen}\" at row {row}");

		var addon = Addon("FreeCompanyAction");
		if (addon is null) {
			this.Fail("FreeCompanyAction vanished before the row could be fired");
			return;
		}

		var values = stackalloc AtkValue[2];
		values[0].Type = AtkValueType.Int;
		values[0].Int = 1;
		values[1].Type = AtkValueType.UInt;
		values[1].UInt = (uint)row;
		addon->FireCallback(2, values, true);

		this.firedRow = row;
		Plugin.Log.Information($"FcBuffs: fired row {row} (\"{text}\")");
		this.Enter(ActivationStep.Executing);
	}

	private void TickExecuting() {
		var menu = Addon("ContextMenu");
		if (menu is null)
			return;

		if (this.waited++ < SettleTicks)
			return;

		string? first = FcBuffReader.ContextMenuFirstItem(menu);
		if (first is null || !first.Equals("Execute Action", StringComparison.OrdinalIgnoreCase)) {

			Plugin.Log.Warning($"FcBuffs: context menu AtkValues = [{DescribeMenu(menu)}]");
			this.Fail($"context menu item 0 is \"{first ?? "unreadable"}\", not Execute Action -- refusing to pick an index");
			return;
		}

		var values = stackalloc AtkValue[5];
		values[0].Type = AtkValueType.Int;
		values[0].Int = 0;
		values[1].Type = AtkValueType.Int;
		values[1].Int = 0;
		values[2].Type = AtkValueType.UInt;
		values[2].UInt = 0;
		values[3].Type = AtkValueType.Undefined;
		values[3].Int = 0;
		values[4].Type = AtkValueType.Undefined;
		values[4].Int = 0;
		menu->FireCallback(5, values, true);

		Plugin.Log.Information("FcBuffs: picked Execute Action");
		this.Enter(ActivationStep.Confirming);
	}

	private void TickConfirming() {
		var yesno = Addon("SelectYesno");
		if (yesno is null)
			return;

		if (this.waited++ < SettleTicks)
			return;

		var values = stackalloc AtkValue[1];
		values[0].Type = AtkValueType.Int;
		values[0].Int = 0;
		yesno->FireCallback(1, values, true);

		Plugin.Log.Information("FcBuffs: confirmed SelectYesno");
		this.Enter(ActivationStep.Settling);
	}

	private void TickSettling() {
		if (this.waited++ < SettleTicks * 2)
			return;

		bool up = FcBuffReader.ActiveFamilies()
			.Contains(FcBuffReader.NormaliseName(this.WantedAction));

		if (up) {
			Plugin.Log.Information($"FcBuffs: \"{this.WantedAction}\" is now active");
			this.Completed.Add(this.WantedAction);

			if (this.queue.Count > 0) {
				this.WantedAction = this.queue.Dequeue();
				this.firedRow = -1;
				Plugin.Log.Information($"FcBuffs: continuing with \"{this.WantedAction}\"");
				this.Enter(ActivationStep.Reading);
				return;
			}

			this.Enter(ActivationStep.Closing);
			return;
		}

		this.Fail($"fired row {this.firedRow} but \"{this.WantedAction}\" never appeared on the player");
	}

	private void TickClosing() {
		if (!this.weOpenedIt) {
			this.Step = ActivationStep.Done;
			return;
		}

		if (this.waited++ == 0) {
			CloseWindow();
			return;
		}

		if (Addon("FreeCompany") is not null)
			return;

		Plugin.Diag("FcBuffs: closed the FC window we opened");
		this.Step = ActivationStep.Done;
	}

	private static void CloseWindow() {
		var agent = AgentFreeCompany.Instance();
		if (agent is not null)
			agent->Hide();
	}
}
