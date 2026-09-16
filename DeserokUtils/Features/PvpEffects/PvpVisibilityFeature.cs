using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using System.Numerics;

using Dalamud.Bindings.ImGui;

using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace DeserokUtils.Features.PvpEffects;

internal sealed class PvpVisibilityFeature: IDisposable {
	public string TabTitle => "PvP limit break visibility";

	public string Summary => "Keeps chosen limit break effects visible on limited battle effects.";

	private const string Tag = "DeserokUtils.PvpVisibility";

	private const int Priority = 99;

	private string state = "not applied";
	private int filesApplied;

	public void Dispose() => this.Remove();

	internal void Apply() {
		if (!Plugin.Config.PvpVisibleEnabled || Plugin.Config.PvpVisibleMoves.Count == 0) {
			this.Remove();
			return;
		}

		var files = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var job in PvpVfx.Jobs)
		foreach (var move in job.Extras.Prepend(job.LimitBreak)) {
			if (!Plugin.Config.PvpVisibleMoves.Contains(move.Key)) continue;

			foreach (var key in move.AnimationKeys) {
				if (files.ContainsKey(TmbPatch.GamePath(key))) continue;
				if (TmbPatch.Prepare(key) is not { } prepared) continue;

				files[prepared.Game] = prepared.Disk;
			}
		}

		if (files.Count == 0) {
			this.state = "nothing to patch";
			this.Remove();
			return;
		}

		try {

			var collection = new GetCollection(Plugin.PluginInterface).Invoke(ApiCollectionType.Default);
			if (collection is not { } target) {
				this.state = "Penumbra has no default collection";
				return;
			}

			var result = new AddTemporaryMod(Plugin.PluginInterface)
				.Invoke(Tag, target.Id, files, string.Empty, Priority);

			this.filesApplied = files.Count;
			this.state = result == PenumbraApiEc.Success
				? $"applied to {target.Name}"
				: $"Penumbra said {result}";
		}
		catch (Exception ex) {

			this.state = $"{ex.GetType().Name}: {ex.Message}";
			Plugin.Log.Warning($"PvP visibility: handing files to Penumbra failed. {ex}");
		}
	}

	internal void Remove() {
		this.filesApplied = 0;

		try {
			var collection = new GetCollection(Plugin.PluginInterface).Invoke(ApiCollectionType.Default);
			if (collection is not { } target) return;

			new RemoveTemporaryMod(Plugin.PluginInterface).Invoke(Tag, target.Id, Priority);
			this.state = "not applied";
		}
		catch (Exception ex) {
			Plugin.Log.Debug($"PvP visibility: nothing to remove ({ex.Message}).");
		}
	}

	public void DrawTab() {

		if (!Plugin.Config.PvpVisibleEnabled)
			ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), "Switched off. Ticking one below switches it on.");

		ImGui.TextDisabled("Ticked limit breaks stay visible on limited battle effects.");
		ImGui.TextDisabled("Your own game files are copied and edited here; nothing is downloaded.");
		ImGui.Spacing();

		var jobs = PvpVfx.Jobs;
		if (jobs.Count == 0) {
			ImGui.TextDisabled("The limit break list could not be read from the game data.");
			return;
		}

		foreach (var job in jobs) {
			var on = Plugin.Config.PvpVisibleMoves.Contains(job.LimitBreak.Key);
			var charge = job.LimitBreak.Charge > 0 ? $" ({job.LimitBreak.Charge}s gauge)" : string.Empty;

			if (ImGui.Checkbox($"{job.Job,-4} {job.LimitBreak.Name}{charge}##job{job.JobId}", ref on)) {
				if (on) {
					Plugin.Config.PvpVisibleMoves.Add(job.LimitBreak.Key);
					Plugin.Config.PvpVisibleEnabled = true;
				}
				else {
					Plugin.Config.PvpVisibleMoves.Remove(job.LimitBreak.Key);
				}

				Plugin.Config.Save();
				this.Apply();
			}

			if (job.Extras.Count == 0) continue;

			ImGui.Indent();
			if (ImGui.TreeNodeEx($"other {job.Job} abilities##extras{job.JobId}", ImGuiTreeNodeFlags.SpanAvailWidth)) {
				ImGui.TextDisabled("Marked ones share their file with the same ability outside PvP.");

				foreach (var move in job.Extras) {
					var extra = Plugin.Config.PvpVisibleMoves.Contains(move.Key);
					var label = move.SharedWithPve ? $"{move.Name} *" : move.Name;
					if (!ImGui.Checkbox($"{label}##move{move.Key}", ref extra)) continue;

					if (extra) {
						Plugin.Config.PvpVisibleMoves.Add(move.Key);
						Plugin.Config.PvpVisibleEnabled = true;
					}
					else {
						Plugin.Config.PvpVisibleMoves.Remove(move.Key);
					}

					Plugin.Config.Save();
					this.Apply();
				}

				ImGui.TreePop();
			}

			ImGui.Unindent();
		}

		ImGui.Spacing();
		if (ImGui.Button("Apply again")) this.Apply();

		ImGui.SameLine();
		if (ImGui.Button("Delete the patched copies")) {
			this.Remove();

			try {
				if (Directory.Exists(TmbPatch.Folder)) Directory.Delete(TmbPatch.Folder, recursive: true);
				this.state = "copies deleted";
			}
			catch (Exception ex) {
				Plugin.Log.Error(ex, "PvP visibility: could not delete the copies.");
			}
		}
	}

	public void DrawDiagnostics() {
		void Row(string label, bool ok, string detail) {
			ImGui.TextUnformatted(ok ? "PASS" : "no  ");
			ImGui.SameLine();
			ImGui.TextDisabled($"{label}  {detail}");
		}

		Row("feature on", Plugin.Config.PvpVisibleEnabled, string.Empty);
		Row("parts picked", Plugin.Config.PvpVisibleMoves.Count > 0, $"{Plugin.Config.PvpVisibleMoves.Count} ticked");
		Row("handed to Penumbra", this.filesApplied > 0, $"{this.filesApplied} files, {this.state}");

		foreach (var job in PvpVfx.Jobs)
		foreach (var move in job.Extras.Prepend(job.LimitBreak).Where(m => Plugin.Config.PvpVisibleMoves.Contains(m.Key)))
			ImGui.TextDisabled($"   {job.Job} {move.Name}: {string.Join(", ", move.AnimationKeys)}");
	}
}
