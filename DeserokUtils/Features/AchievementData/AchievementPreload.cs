using System;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DeserokUtils.Features.AchievementData;

internal sealed unsafe class AchievementPreload: IDisposable {

	private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

	private bool doneThisLogin;
	private DateTime attemptAfter = DateTime.MaxValue;

	public AchievementPreload() {
		Plugin.ClientState.Login += this.OnLogin;

		if (Plugin.ClientState.IsLoggedIn)
			this.OnLogin();
	}

	private void OnLogin() {
		this.doneThisLogin = false;
		this.attemptAfter = DateTime.UtcNow + Settle;
	}

	public void Tick() {
		if (this.doneThisLogin || DateTime.UtcNow < this.attemptAfter)
			return;

		var state = Achievement.Instance();
		if (state is null)
			return;

		this.doneThisLogin = true;

		if (state->IsLoaded()) {
			Plugin.Log.Information("AchievementData: already loaded; no request sent.");
			return;
		}

		try {
			state->RequestCompletedAchievements();
			Plugin.Log.Information(
				"AchievementData: requested completed achievements once for this login, so achievement "
				+ "links can resolve without opening the window first.");
		}
		catch (Exception ex) {
			Plugin.Log.Error(ex, "AchievementData: the request failed; open the Achievements window as usual.");
		}
	}

	public void Dispose() => Plugin.ClientState.Login -= this.OnLogin;
}
