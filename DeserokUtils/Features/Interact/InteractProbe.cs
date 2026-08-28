using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using Dalamud.Game.Chat;
using Dalamud.Game.Text;

using FFXIVClientStructs.FFXIV.Client.Game.Control;

using Newtonsoft.Json;

namespace DeserokUtils.Features.Interact;

/// <summary>
/// Record one row per interact press, to a file, so a failure can be read rather than described.
///
/// ## Why this exists
///
/// A Damaged Winch in Snowcloak operates from some standing positions and refuses with *"Cannot see
/// target"* from others, while the vanilla key works from every position. deserok established two
/// things that between them rule out the easy answers: *"There is only one interactable: Damaged
/// Winch"*, so this key is not picking the wrong object; and dropping <c>checkLineOfSight</c> in
/// v1.12.0 did not fix it, so the raycast was not the gate either.
///
/// His proposal, and it is the right one: *"we can make a sniffer, if it can say why it isn't
/// working... then I'll walk around the winch pressing it, so you can see why it works and why it
/// doesn't"*. Walking a circle and pressing produces a table where the only thing that varies is
/// where he stood -- which is a controlled experiment, and it is not something chat lines pasted one
/// at a time can be.
///
/// ## ⭐⭐ Why success is read from CHAT and not from the return code
///
/// <c>InteractWithObject</c> returns a <c>ulong</c> whose values nobody here has decoded. Treating
/// some value as "failed" would be the guess this whole exercise exists to avoid. So instead every
/// game message that lands within <see cref="Settle"/> of a press is attached to that press
/// verbatim, with no matching and no filtering -- *"Cannot see target."* labels its own row. If the
/// return code turns out to correlate, we will have learned what it means for free, from data.
///
/// ⚠ Deliberately temporary. This is investigation scaffolding for one specimen; when the winch is
/// understood it comes out, along with its file. Same rule the collection dump got.
/// </summary>
internal sealed class InteractProbe: IDisposable {
	/// <summary>How long after a press to keep listening before the row is written.</summary>
	private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1500);

	/// <summary>One press. Field names are short because this is read as a table, not as prose.</summary>
	private sealed class Row {
		public string? Time;
		public string? Note;
		public float[]? Player;
		public float Facing;
		public string? Chosen;
		public string? How;
		public uint BaseId;
		public string? Kind;
		public float Distance;

		/// <summary>Object minus player, so a wall-mounted thing shows up as height rather than range.</summary>
		public float[]? Delta;

		public bool ViewRange;
		public bool OnScreen;
		public bool Targetable;
		public string? Soft;
		public string? Hard;
		public ulong Result;
		public List<string> Said = new();

		/// <summary>⚠ Not written to the file -- it only decides when the row stops waiting.</summary>
		[JsonIgnore]
		public DateTime Born = DateTime.UtcNow;
	}

	private readonly List<Row> pending = new();
	private bool armed;

	public bool Armed => this.armed;

	public string Path => System.IO.Path.Combine(
		Plugin.PluginInterface.GetPluginConfigDirectory(), "interact-probe.jsonl");

	public void Arm() {
		if (this.armed)
			return;
		this.armed = true;
		Plugin.Chat.ChatMessage += this.OnChatMessage;
		Plugin.Chat.Print($"[Interact] recording presses to {this.Path}");
		Plugin.Chat.Print("[Interact] walk around it and press. /dsuinteract record off when done.");
	}

	public void Disarm() {
		if (!this.armed)
			return;
		this.armed = false;
		Plugin.Chat.ChatMessage -= this.OnChatMessage;
		this.Flush(all: true);
		Plugin.Chat.Print($"[Interact] stopped. Rows are in {this.Path}");
	}

	/// <summary>
	/// ⚠ EVERYTHING the game says is kept, with its channel, and nothing is matched against a string.
	/// "Cannot see target." is the message we expect, but expecting it is exactly how a probe ends up
	/// only able to see what it was told to look for -- and the client may not even be in English.
	/// </summary>
	private void OnChatMessage(IHandleableChatMessage message) {
		if (this.pending.Count == 0)
			return;

		try {
			string text = message.Message.TextValue;
			if (text.Length == 0)
				return;

			// The most recent press owns it. Presses are seconds apart; messages arrive in milliseconds.
			this.pending[^1].Said.Add($"[{message.LogKind}] {text}");
		}
		catch (Exception ex) {
			Plugin.Log.Warning($"Interact: probe could not read a chat line: {ex.Message}");
		}
	}

	/// <summary>Called straight after the interact call, with everything that decided it.</summary>
	public unsafe void Capture(
		Dalamud.Game.ClientState.Objects.Types.IGameObject player,
		Dalamud.Game.ClientState.Objects.Types.IGameObject chosen,
		string how,
		ulong result) {

		if (!this.armed)
			return;

		try {
			var ts = TargetSystem.Instance();
			var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)chosen.Address;
			Vector3 delta = chosen.Position - player.Position;

			this.pending.Add(new Row {
				Time = DateTime.Now.ToString("HH:mm:ss.fff"),
				Player = [player.Position.X, player.Position.Y, player.Position.Z],
				Facing = player.Rotation,
				Chosen = chosen.Name.ToString(),
				How = how,
				BaseId = chosen.BaseId,
				Kind = chosen.ObjectKind.ToString(),
				Distance = delta.Length(),
				Delta = [delta.X, delta.Y, delta.Z],
				ViewRange = ts->IsObjectInViewRange(go),
				OnScreen = ts->IsObjectOnScreen(go),
				Targetable = chosen.IsTargetable,
				Soft = Plugin.Targets.SoftTarget?.Name.ToString() ?? "none",
				Hard = Plugin.Targets.Target?.Name.ToString() ?? "none",
				Result = result,
			});
		}
		catch (Exception ex) {
			Plugin.Log.Warning($"Interact: probe could not record a press: {ex.Message}");
		}
	}

	/// <summary>A press with no object to interact with is still evidence about where you stood.</summary>
	public void CaptureMiss(string note) {
		if (!this.armed)
			return;
		this.pending.Add(new Row { Time = DateTime.Now.ToString("HH:mm:ss.fff"), Note = note });
	}

	public void Tick() {
		if (this.pending.Count > 0)
			this.Flush(all: false);
	}

	private void Flush(bool all) {
		var now = DateTime.UtcNow;
		var ready = new List<Row>();

		for (int i = this.pending.Count - 1; i >= 0; i--) {
			// ⚠ A row waits out Settle before it is written, so a refusal the game has not printed
			// yet cannot be missed off the row it belongs to.
			if (!all && now - this.pending[i].Born < Settle)
				continue;
			ready.Insert(0, this.pending[i]);
			this.pending.RemoveAt(i);
		}

		if (ready.Count == 0)
			return;

		try {
			using var file = new StreamWriter(this.Path, append: true);
			foreach (var row in ready)
				file.WriteLine(JsonConvert.SerializeObject(row));
		}
		catch (Exception ex) {
			Plugin.Log.Warning($"Interact: probe could not write {this.Path}: {ex.Message}");
		}
	}

	public void Dispose() {
		if (this.armed)
			Plugin.Chat.ChatMessage -= this.OnChatMessage;
		this.Flush(all: true);
	}
}
