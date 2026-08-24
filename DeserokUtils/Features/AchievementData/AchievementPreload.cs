using System;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DeserokUtils.Features.AchievementData;

/// <summary>
/// Ask the server for your completed achievements once per login, so the rest of the game's own
/// achievement UI behaves.
///
/// ## ⭐⭐ What this replaces, and why the replacement is one call
///
/// This started as a full feature: decode achievement chat links, rewrite them, show a tooltip.
/// ⚠ All of that is deleted. Something already installed shows an achievement tooltip in the native
/// chat window -- not this plugin, and not VanillaPlus, provider unidentified -- so the gap it was
/// built for does not exist where it was thought to be.
///
/// ⭐ What DID hold up is the diagnosis underneath it. Clicking an achievement link opened "Last Five"
/// rather than the achievement, until the Achievements window had been opened once; afterwards it
/// navigated correctly. The client cannot look up what it has not loaded, and it only loads on demand.
///
/// ⭐ deserok's call: *"why not simply call for achievement data once at the beginning of a session."*
/// The game already has the capability and the request -- only the binding was missing, which is the
/// same shape as DisableLogMessage in EmoteQuiet and the menu-free interact.
///
/// ## ⚠ Sending a packet on somebody's behalf deserves its guards
///
/// This is the identical request the client fires when you open the Achievements window, so it is not
/// exotic traffic -- but it is still unprompted, so:
///
/// - ⚠ **Once per login.** The flag is set before the call, not after, so a throw cannot produce a
///   retry loop.
/// - ⚠ **Only when it is actually needed.** If IsLoaded() is already true, nothing is sent.
/// - ⚠ **Never retried.** If it does not take, the user opens the window as they always did.
/// - ⚠ **Logged either way**, because a plugin that talks to the server silently is the wrong kind of
///   quiet.
/// </summary>
internal sealed unsafe class AchievementPreload: IDisposable {
	/// ⚠ The client is not ready for this the instant Login fires, so the attempt is deferred rather
	/// than fired on the event. A couple of seconds, checked on the existing tick, no new timer.
	private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

	private bool doneThisLogin;
	private DateTime attemptAfter = DateTime.MaxValue;

	public AchievementPreload() {
		Plugin.ClientState.Login += this.OnLogin;

		// ⚠ Plugins load mid-session far more often than they load at a login screen -- every dev
		// reload is one -- so an already-logged-in client has to arm this too, or the feature would
		// only ever work on a path deserok rarely takes.
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

		// ⚠ Set BEFORE the call. If RequestCompletedAchievements throws, this must still not run again.
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
