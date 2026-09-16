using System;
using System.Collections.Generic;

using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.Resource;

namespace DeserokUtils.Features.PvpEffects;

internal static unsafe class VfxSniffer {

	private static readonly TimeSpan DefaultFor = TimeSpan.FromMinutes(5);
	private static readonly TimeSpan MaxFor = TimeSpan.FromMinutes(15);

	private const int LineCap = 4000;

	private static readonly TimeSpan SamePathGap = TimeSpan.FromMilliseconds(100);

	private static volatile bool armed;
	private static DateTime until;
	private static int lines;
	private static int actions;
	private static int effects;

	private static readonly Dictionary<string, DateTime> LastSeen = new(StringComparer.Ordinal);

	private delegate nint GetResourceSyncDelegate(nint manager, nint category, nint type, nint hash, byte* path,
	                                              nint unknown, nint debugPtr, uint debugInt);

	private delegate nint GetResourceAsyncDelegate(nint manager, nint category, nint type, nint hash, byte* path,
	                                               nint unknown, byte isUnknown, nint debugPtr, uint debugInt);

	private delegate void ActionEffectDelegate(uint casterEntityId, nint casterPtr, nint targetPos,
	                                           ActionEffectHandler.Header* header, nint targetEffects, nint targetIds);

	private static Hook<GetResourceSyncDelegate>? syncHook;
	private static Hook<GetResourceAsyncDelegate>? asyncHook;
	private static Hook<ActionEffectDelegate>? effectHook;

	public static void Register() {
		Plugin.RegisterSub("vfxsniff", "record which effect files play after which ability, 5 minutes by default",
			(_, rest) => Arm(rest));
	}

	private static void Arm(string rest) {
		if (armed) {
			Plugin.Chat.Print("[DeserokUtils] vfxsniff is already running. It stops on its own.");
			return;
		}

		var span = DefaultFor;
		if (double.TryParse(rest.Trim(), out var minutes) && minutes > 0)
			span = TimeSpan.FromMinutes(Math.Min(minutes, MaxFor.TotalMinutes));

		if (!Hooks()) {
			Plugin.Chat.PrintError("[DeserokUtils] vfxsniff could not attach to the game. Nothing recorded.");
			return;
		}

		lines = actions = effects = 0;
		LastSeen.Clear();
		until = DateTime.UtcNow + span;
		armed = true;

		syncHook!.Enable();
		asyncHook!.Enable();
		effectHook!.Enable();

		Plugin.Framework.Update += Tick;

		SniffLog.Mark($"VFX SNIFF ARMED for {span.TotalMinutes:0} minutes");
		Plugin.Chat.Print($"[DeserokUtils] recording abilities and effect files for {span.TotalMinutes:0} minutes.");
	}

	private static bool Hooks() {
		if (syncHook is not null && asyncHook is not null && effectHook is not null)
			return true;

		try {
			syncHook ??= Plugin.Interop.HookFromAddress<GetResourceSyncDelegate>(
				(nint)ResourceManager.MemberFunctionPointers.GetResourceSync, ResourceSync);

			asyncHook ??= Plugin.Interop.HookFromAddress<GetResourceAsyncDelegate>(
				(nint)ResourceManager.MemberFunctionPointers.GetResourceAsync, ResourceAsync);

			effectHook ??= Plugin.Interop.HookFromAddress<ActionEffectDelegate>(
				(nint)ActionEffectHandler.MemberFunctionPointers.Receive, ActionEffect);

			return true;
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "vfxsniff: could not create the hooks.");
			return false;
		}
	}

	private static nint ResourceSync(nint manager, nint category, nint type, nint hash, byte* path,
	                                 nint unknown, nint debugPtr, uint debugInt) {
		var result = syncHook!.Original(manager, category, type, hash, path, unknown, debugPtr, debugInt);
		Note(path);
		return result;
	}

	private static nint ResourceAsync(nint manager, nint category, nint type, nint hash, byte* path,
	                                  nint unknown, byte isUnknown, nint debugPtr, uint debugInt) {
		var result = asyncHook!.Original(manager, category, type, hash, path, unknown, isUnknown, debugPtr, debugInt);
		Note(path);
		return result;
	}

	private static void Note(byte* path) {
		if (!armed || path is null || lines >= LineCap) return;

		try {
			var text = MemoryHelper.ReadStringNullTerminated((nint)path);
			if (text.Length == 0 || !text.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)) return;

			var now = DateTime.UtcNow;
			if (LastSeen.TryGetValue(text, out var last) && now - last < SamePathGap) return;

			LastSeen[text] = now;
			effects++;
			lines++;
			SniffLog.Write($"vfx  {text}");
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "vfxsniff: reading a path failed.");
		}
	}

	private static void ActionEffect(uint casterEntityId, nint casterPtr, nint targetPos,
	                                 ActionEffectHandler.Header* header, nint targetEffects, nint targetIds) {
		effectHook!.Original(casterEntityId, casterPtr, targetPos, header, targetEffects, targetIds);

		if (!armed || header is null || lines >= LineCap) return;

		try {
			var id = header->ActionId;
			var name = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>()
			                      .GetRowOrDefault(id)?.Name.ExtractText() ?? string.Empty;

			var me = Plugin.Objects.LocalPlayer;
			var mine = me is not null && me.EntityId == casterEntityId;

			actions++;
			lines++;
			SniffLog.Write($"act  {id,6} \"{name}\" by {(mine ? "me" : $"entity {casterEntityId:X}")} targets {header->NumTargets}");
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "vfxsniff: reading an action failed.");
		}
	}

	private static void Tick(IFramework framework) {
		if (DateTime.UtcNow < until && lines < LineCap) return;

		var full = lines >= LineCap;
		Stop();

		SniffLog.Mark($"VFX SNIFF STOPPED - {actions} actions, {effects} effect files{(full ? ", line cap reached" : string.Empty)}");
		Plugin.Chat.Print($"[DeserokUtils] vfxsniff done: {actions} abilities, {effects} effect files in sniff.log"
			+ (full ? " (cap reached early)." : "."));
	}

	private static void Stop() {
		armed = false;
		Plugin.Framework.Update -= Tick;

		syncHook?.Disable();
		asyncHook?.Disable();
		effectHook?.Disable();
	}

	public static void Dispose() {
		if (armed) Stop();

		syncHook?.Dispose();
		asyncHook?.Dispose();
		effectHook?.Dispose();

		syncHook = null;
		asyncHook = null;
		effectHook = null;
	}
}
