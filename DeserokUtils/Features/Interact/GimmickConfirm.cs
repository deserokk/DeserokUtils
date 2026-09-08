using System;
using System.Collections.Generic;
using System.Text;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.UI;

namespace DeserokUtils.Features.Interact;

internal sealed class GimmickConfirm: IDisposable {
	private const string Addon = "SelectYesno";

	private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(1);

	private DateTime armedUntil = DateTime.MinValue;
	private DateTime pendingSince = DateTime.MinValue;
	private HashSet<string>? prompts;

	public string LastAnswer { get; private set; } = "nothing yet";

	public int KnownPrompts => this.Prompts.Count;

	public GimmickConfirm() =>
		Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, Addon, this.OnSetup);

	public void Arm() => this.armedUntil = DateTime.UtcNow + Window;

	private void OnSetup(AddonEvent type, AddonArgs args) {
		if (!Plugin.Config.InteractAnswerGimmicks)
			return;

		if (DateTime.UtcNow > this.armedUntil) return;

		this.pendingSince = DateTime.UtcNow;
	}

	public unsafe void Tick() {
		if (this.pendingSince == DateTime.MinValue)
			return;

		var unit = Plugin.GameGui.GetAddonByName(Addon);
		if (unit.IsNull) {

			this.Give("the box was already gone a frame later -- something else answered it");
			return;
		}

		if (!unit.IsReady) {
			if (DateTime.UtcNow - this.pendingSince > Patience)
				this.Give("the box never finished loading");
			return;
		}

		var addon = (AddonSelectYesno*)(nint)unit;

		string node = ReadNode(addon);
		string value = ReadFirstValue(addon);
		string text = node.Length > 0 ? node : value;
		Plugin.Diag($"Interact: prompt node=\"{node}\" value=\"{value}\"");

		this.pendingSince = DateTime.MinValue;

		if (text.Length == 0) {
			this.Give($"could not read the prompt at all (node=\"{node}\" value=\"{value}\")");
			return;
		}

		if (!this.Prompts.Contains(Normalise(text))) {

			this.Give($"\"{text}\" is not in the GimmickYesNo sheet -- left for you");
			return;
		}

		addon->AtkUnitBase.FireCallbackInt(0);
		this.LastAnswer = $"yes to \"{text}\"";
		Plugin.Log.Information($"Interact: answered yes to \"{text}\"");
		Plugin.Diag($"Interact: answered yes to \"{text}\"");
	}

	private void Give(string why) {
		this.pendingSince = DateTime.MinValue;
		this.LastAnswer = why;
		Plugin.Diag($"Interact: {why}");
	}

	private static unsafe string ReadNode(AddonSelectYesno* addon) {
		var node = addon->PromptText;
		return node is null ? string.Empty : node->NodeText.ExtractText().Trim();
	}

	private static unsafe string ReadFirstValue(AddonSelectYesno* addon) {
		var atk = &addon->AtkUnitBase;
		return atk->AtkValues is null || atk->AtkValuesCount == 0
			? string.Empty
			: (atk->AtkValues[0].GetValueAsString() ?? string.Empty).Trim();
	}

	private HashSet<string> Prompts {
		get {
			if (this.prompts is not null)
				return this.prompts;

			this.prompts = new HashSet<string>(StringComparer.Ordinal);

			var gimmicks = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.GimmickYesNo>();
			if (gimmicks is null)
				Plugin.Log.Warning("Interact: GimmickYesNo sheet did not load.");
			else
				foreach (var row in gimmicks) {
					string text = row.Message.ExtractText();
					if (text.Length > 0)
						this.prompts.Add(Normalise(text));
				}

			int skippedTolls = 0;
			var warps = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Warp>();
			if (warps is null)
				Plugin.Log.Warning("Interact: Warp sheet did not load.");
			else
				foreach (var row in warps) {
					string text = row.Question.ExtractText();
					if (text.Length == 0)
						continue;

					if (row.WarpCondition.ValueNullable?.Gil > 0) {
						skippedTolls++;
						continue;
					}

					this.prompts.Add(Normalise(text));
				}

			Plugin.Log.Information($"Interact: {this.prompts.Count} prompts loaded from GimmickYesNo + Warp, "
				+ $"{skippedTolls} paid transports left for you to confirm.");
			return this.prompts;
		}
	}

	private static string Normalise(string text) {
		var sb = new StringBuilder(text.Length);
		foreach (char c in text) {
			if (char.IsWhiteSpace(c) || c == '\u00ad')
				continue;
			sb.Append(char.ToLowerInvariant(c));
		}
		return sb.ToString();
	}

	public void Dispose() => Plugin.AddonLifecycle.UnregisterListener(this.OnSetup);
}
