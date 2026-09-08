using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DeserokUtils.Features.Fanfare.Achievements;

internal sealed unsafe class AchievementWatcher: IDisposable {
	private delegate void SetAchievementCompletedDelegate(Achievement* achievement, uint achievementId);

	private readonly Hook<SetAchievementCompletedDelegate>? hook;
	private readonly Action<uint> onUnlocked;

	private DateTime quietUntil;

	private readonly TimeSpan settleTime = TimeSpan.FromSeconds(12);

	internal AchievementWatcher(Action<uint> onUnlocked) {
		this.onUnlocked = onUnlocked;

		if (Plugin.ClientState.IsLoggedIn) {
			this.quietUntil = DateTime.UtcNow;
			Plugin.Log.Information("achievements: already logged in, watching immediately.");
		} else {
			this.Rearm();
		}

		try {

			this.hook = Plugin.Interop.HookFromAddress<SetAchievementCompletedDelegate>(
				(nint)Achievement.MemberFunctionPointers.SetAchievementCompleted,
				this.Detour);

			this.hook.Enable();
			Plugin.Log.Information("achievements: hook installed.");
		} catch (Exception ex) {
			Plugin.Log.Error(ex, "achievements: could not install the hook; popups will not fire.");
			Plugin.Chat.PrintError("[Fanfare] could not hook achievements. /fanfare test still works.");
		}
	}

	internal void Rearm() => this.quietUntil = DateTime.UtcNow + this.settleTime;

	private void Detour(Achievement* achievement, uint achievementId) {

		this.hook!.Original(achievement, achievementId);

		try {
			if (DateTime.UtcNow < this.quietUntil) {
				Plugin.Log.Debug($"achievements: {achievementId} ignored, still settling.");
				return;
			}

			this.onUnlocked(achievementId);
		} catch (Exception ex) {

			Plugin.Log.Error(ex, $"achievements: handler failed for {achievementId}");
		}
	}

	public void Dispose() {
		this.hook?.Disable();
		this.hook?.Dispose();
	}
}
