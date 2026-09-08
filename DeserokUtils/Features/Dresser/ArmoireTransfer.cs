using System;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Dresser;

internal sealed unsafe class ArmoireTransfer {
	internal enum State { Idle, Opening, Unpacking, Restoring, Storing, Done, Failed }

	private const int SlotsToLeaveFree = 5;

	private const int MaxBatch = 40;

	private const int PaceTicks = 30;

	private const int PaceJitter = 25;

	private readonly Random jitter = new();

	private (uint ItemId, uint CabinetRow, string Name)? inFlight;

	private const int Settle = 20;

	private const int StepTimeoutTicks = 400;

	private const int MaxYesno = 3;

	private readonly List<(uint ItemId, uint CabinetRow, string Name)> queue = new();
	private readonly List<(uint ItemId, uint CabinetRow, string Name)> batch = new();
	private readonly List<string> failed = new();

	private State state = State.Idle;
	private int settle;
	private int waited;
	private int yesno;

	internal Action<DresserScan.Result>? Rescanned;

	public State Current => this.state;
	public string Status { get; private set; } = string.Empty;
	public int Stored { get; private set; }
	public IReadOnlyList<string> Failed => this.failed;
	public bool Running
		=> this.state is State.Opening or State.Unpacking or State.Restoring or State.Storing;

	public int Unpacked { get; private set; }

	public void Start(DresserScan.Result r) {
		this.queue.Clear();
		this.batch.Clear();
		this.failed.Clear();
		this.inFlight = null;
		this.dissolving = null;
		this.Stored = 0;
		this.yesno = 0;

		var mirage = MirageManager.Instance();
		if (mirage is null || !mirage->PrismBoxLoaded) {
			this.Fail("Open your glamour dresser once first, then try again.");
			return;
		}

		if (r.ArmoireTransfer.Count == 0 && r.Dissolvable.Count == 0) {
			this.state = State.Done;
			this.Status = "Nothing the Armoire would take.";
			return;
		}

		if (Room() < 1 && r.ArmoireTransfer.Exists(x => !InBags(x.ItemId))) {
			this.Fail($"Needs at least {SlotsToLeaveFree + 1} free bag slots; make a little room.");
			return;
		}

		foreach (var piece in r.ArmoireTransfer) {
			if (Equipped(piece.ItemId)) {
				DresserLog.Step($"  {piece.Name}: you are wearing one, leaving it alone");
				continue;
			}

			this.queue.Add(piece);
		}

		this.dissolve.Clear();
		this.dissolve.AddRange(r.Dissolvable);
		this.Unpacked = 0;

		if (this.queue.Count == 0 && this.dissolve.Count == 0) {
			this.state = State.Done;
			this.Status = "Nothing to move — you are wearing the pieces the Armoire would take.";
			Plugin.Chat.Print($"Dresser: {this.Status}");
			return;
		}

		this.state = UIState.Instance()->Cabinet.IsCabinetLoaded()
			? (this.dissolve.Count > 0 ? State.Unpacking : State.Restoring)
			: State.Opening;
		this.settle = 0;
		this.waited = 0;
		this.opens = 0;
		this.menuAnswers = 0;

		this.Status = this.state == State.Opening
			? "Opening your Armoire..."
			: $"Moving {this.queue.Count} piece(s) to your Armoire...";

		DresserLog.Step($"=== ARMOIRE START: {this.queue.Count} piece(s), "
			+ $"{this.dissolve.Count} outfit(s) ===");
	}

	public void Stop(string why) {
		if (!this.Running) return;

		this.state = State.Failed;
		this.Status = why;
		DresserLog.Step($"ARMOIRE STOPPED: {why}");
	}

	public void Tick() {
		if (!this.Running) return;
		if (this.settle > 0) { this.settle--; return; }

		if (this.yesno < MaxYesno && FireYes()) {
			this.yesno++;
			DresserLog.Trace("  fired: SelectYesno [0]");
			this.settle = Settle;
			return;
		}

		if (++this.waited > StepTimeoutTicks) {
			this.GiveUpOnCurrent("the game did not respond in time");
			return;
		}

		switch (this.state) {
			case State.Opening: this.TickOpening(); break;
			case State.Unpacking: this.TickUnpack(); break;
			case State.Restoring: this.TickRestore(); break;
			default: this.TickStore(); break;
		}
	}

	private void TickOpening() {
		if (UIState.Instance()->Cabinet.IsCabinetLoaded()) {
			CloseCabinetWindow();
			DresserLog.Step("  armoire opened");
			this.state = this.dissolve.Count > 0 ? State.Unpacking : State.Restoring;
			this.waited = 0;
			this.Status = $"Moving {this.queue.Count} piece(s) to your Armoire...";
			return;
		}

		if (this.menuAnswers < MaxOpenAttempts && FireMenuEntry(0)) {
			this.menuAnswers++;
			DresserLog.Step($"  chose 'Store an item' (attempt {this.menuAnswers})");
			this.settle = OpenSettle;
			return;
		}

		if (this.menuAnswers > 0) return;

		if (this.opens >= MaxOpenAttempts) {
			DresserProbe.Visible("after interacting with the Armoire");
			this.Fail("Could not open your Armoire — open it once yourself and try again.");
			return;
		}

		if (FindArmoire() is not { } armoire) {
			this.Fail("No Armoire nearby — stand next to one and try again.");
			return;
		}

		this.opens++;
		DresserLog.Step($"  interacting with the Armoire (attempt {this.opens})");

		TargetSystem.Instance()->InteractWithObject(
			(FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)armoire.Address, true);

		this.settle = OpenSettle;
	}

	private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindArmoire() {
		var player = Plugin.Objects.LocalPlayer;
		if (player is null) return null;

		foreach (var obj in Plugin.Objects) {
			if (obj is null) continue;
			if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj) continue;
			if (!obj.IsTargetable) continue;
			if (obj.BaseId != ArmoireDataId) continue;

			return obj;
		}

		return null;
	}

	private const uint ArmoireDataId = 2005630;

	private const int MaxOpenAttempts = 3;

	private const int OpenSettle = 60;

	private int opens;

	private int menuAnswers;

	private static bool FireMenuEntry(int index) {
		var addon = Plugin.GameGui.GetAddonByName("SelectString", 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return false;

		var unit = (AtkUnitBase*)addon.Address;
		var values = stackalloc AtkValue[1];
		values[0].Type = AtkValueType.Int;
		values[0].Int = index;

		unit->FireCallback(1, values, true);
		return true;
	}

	private static void CloseCabinetWindow() {

		var values = stackalloc AtkValue[1];
		values[0].Type = AtkValueType.Int;
		values[0].Int = -1;

		foreach (var name in new[] { "Cabinet", "SelectString" }) {
			var addon = Plugin.GameGui.GetAddonByName(name, 1);
			if (addon.Address == nint.Zero || !addon.IsVisible) continue;

			var unit = (AtkUnitBase*)addon.Address;
			unit->FireCallback(1, values, true);
		}
	}

	private void TickUnpack() {
		var mirage = MirageManager.Instance();
		if (mirage is null || !mirage->PrismBoxLoaded) {
			this.Stop("lost sight of the dresser contents");
			return;
		}

		if (this.dissolving is { } pending) {
			if (Used(mirage) < this.usedBeforeDissolve) {
				DresserLog.Step($"  unpacked {pending.Name}");
				this.Unpacked++;

				foreach (var piece in pending.Contents) {
					if (this.queue.Exists(x => x.ItemId == piece.ItemId)) continue;
					this.queue.Add(piece);
				}

				DresserLog.Step($"    {pending.Contents.Count} piece(s) added to the queue");
				this.dissolving = null;
				this.waited = 0;
				this.settle = this.Pace();
				return;
			}

			if (++this.layoutWait < DissolveTimeout) return;

			this.layoutWait = 0;
			DresserLog.Step($"  {pending.Name}: nothing came back");
			this.failed.Add($"{pending.Name} (could not be unpacked)");
			this.dissolving = null;
			this.waited = 0;
			return;
		}

		if (this.dissolve.Count == 0) {
			this.state = State.Restoring;
			this.waited = 0;
			DresserLog.Step($"  {this.queue.Count} piece(s) to store");
			return;
		}

		var next = this.dissolve[0];
		if (DresserPacker.FreeBagSlots() < next.Pieces + SlotsToLeaveFree) {
			DresserLog.Step($"  {next.Name} needs {next.Pieces} slots; storing what we have first");
			this.state = State.Restoring;
			this.waited = 0;
			return;
		}

		this.dissolve.RemoveAt(0);

		var ids = mirage->PrismBoxItemIds;
		var index = -1;
		for (var i = 0; i < ids.Length; i++) {
			if (ids[i] != next.SetItemId) continue;
			index = i;
			break;
		}

		if (index < 0) {
			DresserLog.Step($"  {next.Name}: no longer in the dresser, skipping");
			return;
		}

		this.yesno = 0;
		this.dissolving = next;
		this.Dissolve(mirage, next);
	}

	private void Dissolve(
		MirageManager* mirage, (uint Index, uint SetItemId, string Name, ushort Mask, int Pieces, List<(uint ItemId, uint CabinetRow, string Name)> Contents) outfit) {
		var ids = mirage->PrismBoxItemIds;
		var index = -1;
		for (var i = 0; i < ids.Length; i++) {
			if (ids[i] != outfit.SetItemId) continue;
			index = i;
			break;
		}

		if (index < 0) {
			DresserLog.Step($"  {outfit.Name}: no longer in the dresser");
			this.dissolving = null;
			return;
		}

		var bits = stackalloc byte[SetSlots];
		for (var i = 0; i < SetSlots; i++) bits[i] = 0;

		bits[0] = (byte)(outfit.Mask & 0xFF);
		bits[1] = (byte)((outfit.Mask >> 8) & 0xFF);

		this.usedBeforeDissolve = Used(mirage);
		DresserLog.Step($"  unpacking {outfit.Name} at index {index}, mask {outfit.Mask}, "
			+ $"({outfit.Pieces} piece(s))");

		var ok = MirageManager.MemberFunctionPointers.RestorePrismBoxSetItem(
			mirage, (uint)index, bits);

		DresserLog.Trace($"  RestorePrismBoxSetItem -> {ok}");

		this.waited = 0;
		this.settle = this.Pace();
	}

	private const int SetSlots = 11;

	private int layoutWait;

	private const int DissolveTimeout = 300;

	private static int Used(MirageManager* mirage) {
		var ids = mirage->PrismBoxItemIds;
		var used = 0;
		for (var i = 0; i < ids.Length; i++) {
			if (ids[i] != 0) used++;
		}

		return used;
	}

	private readonly List<(uint Index, uint SetItemId, string Name, ushort Mask, int Pieces, List<(uint ItemId, uint CabinetRow, string Name)> Contents)> dissolve
		= new();

	private (uint Index, uint SetItemId, string Name, ushort Mask, int Pieces, List<(uint ItemId, uint CabinetRow, string Name)> Contents)? dissolving;
	private int usedBeforeDissolve;

	private void TickRestore() {

		if (this.inFlight is { } flying) {
			if (!InBags(flying.ItemId)) return;

			DresserLog.Trace($"  landed: {flying.Name}");
			this.batch.Add(flying);
			this.inFlight = null;
			this.waited = 0;
			this.settle = Pace();
			return;
		}

		var free = DresserPacker.FreeBagSlots();
		var room = Math.Min(Math.Max(0, free - SlotsToLeaveFree), MaxBatch);

		if (this.batch.Count == 0 && this.queue.Count == 0) {
			this.Finish();
			return;
		}

		if (this.batch.Count > 0 && (this.batch.Count >= room || this.queue.Count == 0)) {
			DresserLog.Step($"  storing {this.batch.Count} piece(s)");
			this.state = State.Storing;
			this.waited = 0;
			return;
		}

		if (room < 1) {
			this.Stop($"your bags are too full to carry any more; {this.Stored} moved so far");
			return;
		}

		var piece = this.queue[0];
		this.queue.RemoveAt(0);

		if (InBags(piece.ItemId)) {
			this.batch.Add(piece);
			return;
		}

		var mirage = MirageManager.Instance();
		if (mirage is null || !mirage->PrismBoxLoaded) {
			this.Stop("lost sight of the dresser contents");
			return;
		}

		var ids = mirage->PrismBoxItemIds;
		var index = -1;
		for (var i = 0; i < ids.Length; i++) {
			if (ids[i] != piece.ItemId) continue;
			index = i;
			break;
		}

		if (index < 0) {
			DresserLog.Step($"  {piece.Name}: no longer in the dresser, skipping");
			return;
		}

		if (!MirageManager.MemberFunctionPointers.RestorePrismBoxItem(mirage, (uint)index)) {
			DresserLog.Step($"  {piece.Name}: the game refused to restore it");
			this.failed.Add($"{piece.Name} (could not be taken out of the dresser)");
			return;
		}

		DresserLog.Trace($"  restore: {piece.Name} from index {index} ({free} free)");
		this.yesno = 0;
		this.inFlight = piece;
		this.waited = 0;
		this.settle = Pace();
	}

	private int Pace() => PaceTicks + this.jitter.Next(PaceJitter);

	private void TickStore() {
		if (this.batch.Count == 0) {

			this.state = this.dissolve.Count > 0 ? State.Unpacking : State.Restoring;
			this.waited = 0;
			return;
		}

		var piece = this.batch[0];

		if (UIState.Instance()->Cabinet.IsItemInCabinet(piece.CabinetRow)) {
			DresserLog.Step($"  stored {piece.Name}");
			this.batch.RemoveAt(0);
			this.Stored++;
			this.Status = $"Moved {this.Stored} piece(s) to your Armoire...";
			this.waited = 0;
			this.settle = Settle;
			return;
		}

		if (!Locate(piece.ItemId, out var where, out var slot)) {
			DresserLog.Step($"  {piece.Name}: not in your bags or armoury any more");
			this.batch.RemoveAt(0);
			this.waited = 0;
			return;
		}

		if (IsArmoury(where)) {
			if (!FreeBagSlot(out var destination, out var destinationSlot)) {
				DresserLog.Step($"  {piece.Name}: in your {where} and no bag slot to move it to");
				this.failed.Add($"{piece.Name} (no room to move it out of your armoury chest)");
				this.batch.RemoveAt(0);
				this.waited = 0;
				return;
			}

			DresserLog.Step($"  moving {piece.Name} from {where} to your bags");
			InventoryManager.Instance()->MoveItemSlot(
				where, slot, destination, destinationSlot, true);

			this.waited = 0;
			this.settle = this.Pace();
			return;
		}

		this.yesno = 0;
		UIState.Instance()->Cabinet.StoreCabinetItem(piece.CabinetRow);
		DresserLog.Trace($"  store: {piece.Name} (cabinet {piece.CabinetRow})");

		this.settle = this.Pace();
	}

	private void GiveUpOnCurrent(string why) {

		if (this.dissolving is { } outfit) {
			this.failed.Add($"{outfit.Name} ({why})");
			DresserLog.Step($"  SKIPPED {outfit.Name}: {why}");
			this.dissolving = null;
			this.layoutWait = 0;
			this.waited = 0;
			return;
		}

		if (this.inFlight is { } flying) {
			this.failed.Add($"{flying.Name} ({why})");
			DresserLog.Step($"  SKIPPED {flying.Name}: {why}");
			this.inFlight = null;
			this.waited = 0;
			this.yesno = 0;
			return;
		}

		if (this.batch.Count > 0) {
			var piece = this.batch[0];
			this.batch.RemoveAt(0);
			this.failed.Add($"{piece.Name} ({why})");
			DresserLog.Step($"  SKIPPED {piece.Name}: {why}");
		}

		this.waited = 0;
		this.yesno = 0;

		if (this.batch.Count == 0 && this.queue.Count == 0 && this.inFlight is null) this.Finish();
	}

	private void Finish() {
		this.state = State.Done;

		var after = new DresserScan().Scan();
		if (after.Loaded && after.Problem is null) this.Rescanned?.Invoke(after);

		if (this.Stored > 0 && this.Unpacked > 0) {
			this.Status = $"Took apart {Plural(this.Unpacked, "outfit")} and moved "
			            + $"{Plural(this.Stored, "piece")} to your Armoire — "
			            + $"{Plural(this.Stored + this.Unpacked, "dresser slot")} freed.";
		}
		else if (this.Stored > 0) {
			this.Status = $"Moved {Plural(this.Stored, "piece")} to your Armoire — "
			            + $"{Plural(this.Stored, "dresser slot")} freed.";
		}
		else if (this.Unpacked > 0) {
			this.Status = $"Took apart {Plural(this.Unpacked, "outfit")}; "
			            + "their pieces are in your bags.";
		}
		else {
			this.Status = "Nothing was moved to your Armoire.";
		}

		if (this.failed.Count > 0)
			this.Status += $" {this.failed.Count} could not be moved — see the list.";

		Plugin.Chat.Print($"Dresser: {this.Status}");
		DresserLog.Step($"=== ARMOIRE DONE: {this.Status} ===");

		foreach (var entry in this.failed) DresserLog.Step($"  failed: {entry}");
	}

	private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

	private void Fail(string why) {
		this.state = State.Failed;
		this.Status = why;
		Plugin.Chat.Print($"Dresser: {why}");
	}

	private static int Room() => Math.Max(0, DresserPacker.FreeBagSlots() - SlotsToLeaveFree);

	private static bool AllLanded(List<(uint ItemId, uint CabinetRow, string Name)> pieces) {
		foreach (var piece in pieces) {
			if (!InBags(piece.ItemId)) return false;
		}

		return true;
	}

	private static bool Locate(uint itemId, out InventoryType where, out ushort slot) {
		where = default;
		slot = 0;

		var manager = InventoryManager.Instance();
		if (manager is null) return false;

		foreach (var bag in Anywhere) {
			var page = manager->GetInventoryContainer(bag);
			if (page is null || !page->IsLoaded) continue;

			for (var i = 0; i < page->Size; i++) {
				var item = page->GetInventorySlot(i);
				if (item is null || DresserCache.PureItemId(item->ItemId) != itemId) continue;

				where = bag;
				slot = (ushort)i;
				return true;
			}
		}

		return false;
	}

	private static bool FreeBagSlot(out InventoryType where, out ushort slot) {
		where = default;
		slot = 0;

		var manager = InventoryManager.Instance();
		if (manager is null) return false;

		foreach (var bag in Bags) {
			var page = manager->GetInventoryContainer(bag);
			if (page is null || !page->IsLoaded) continue;

			for (var i = 0; i < page->Size; i++) {
				var item = page->GetInventorySlot(i);
				if (item is not null && item->ItemId != 0) continue;

				where = bag;
				slot = (ushort)i;
				return true;
			}
		}

		return false;
	}

	private static bool IsArmoury(InventoryType type) {
		foreach (var bag in Bags) {
			if (bag == type) return false;
		}

		return true;
	}

	private static readonly InventoryType[] Bags = {
		InventoryType.Inventory1, InventoryType.Inventory2,
		InventoryType.Inventory3, InventoryType.Inventory4,
	};

	private static bool InBags(uint itemId) {
		var manager = InventoryManager.Instance();
		if (manager is null) return false;

		foreach (var bag in Anywhere) {
			var page = manager->GetInventoryContainer(bag);
			if (page is null || !page->IsLoaded) continue;

			for (var i = 0; i < page->Size; i++) {
				var item = page->GetInventorySlot(i);
				if (item is not null && item->ItemId == itemId) return true;
			}
		}

		return false;
	}

	private static bool Equipped(uint itemId) {
		var manager = InventoryManager.Instance();
		if (manager is null) return false;

		var page = manager->GetInventoryContainer(InventoryType.EquippedItems);
		if (page is null || !page->IsLoaded) return false;

		for (var i = 0; i < page->Size; i++) {
			var item = page->GetInventorySlot(i);
			if (item is not null && item->ItemId == itemId) return true;
		}

		return false;
	}

	private static readonly InventoryType[] Anywhere = {
		InventoryType.Inventory1, InventoryType.Inventory2,
		InventoryType.Inventory3, InventoryType.Inventory4,
		InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
		InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
		InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
		InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
	};

	private static bool FireYes() {
		var addon = Plugin.GameGui.GetAddonByName("SelectYesno", 1);
		if (addon.Address == nint.Zero || !addon.IsVisible) return false;

		var unit = (AtkUnitBase*)addon.Address;
		var values = stackalloc AtkValue[1];
		values[0].Type = AtkValueType.Int;
		values[0].Int = 0;

		unit->FireCallback(1, values, true);
		return true;
	}
}
