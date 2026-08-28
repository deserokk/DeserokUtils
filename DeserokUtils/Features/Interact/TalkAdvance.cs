using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Interact;

/// <summary>
/// Advance the NPC dialogue box, one line per press.
///
/// ## Why
///
/// Bunny, on the day the key first worked for her: *"Is there a way to make it skip dialogue, on
/// press, but keep the no menu benefit because I used to spam the interact key to advance
/// dialogue"*. Confirm advances the Talk box; a key that does not is a downgrade during every
/// quest, and she had years of muscle memory saying otherwise.
///
/// ⭐ ON PRESS, never automatically. YesAlready has a Talk feature that advances by itself once a
/// target matches, and that is a different product -- it decides you did not want to read. This
/// does exactly what her thumb already does and nothing more.
///
/// ## ⭐ Why this does not spend the "no menu" benefit
///
/// Talk is a dialogue box, not a menu: it has no cursor and nothing to land on. The thing that
/// makes Confirm dangerous is the CHOICE lists -- SelectString, SelectIconString -- where a cursor
/// moves over options. Those are deliberately untouched, so a conversation that stops to ask you
/// something still stops. That is the whole distinction Bunny asked to keep.
/// </summary>
internal static class TalkAdvance {
	private const string Addon = "Talk";

	/// <summary>True if a dialogue box is up and this press was spent on it.</summary>
	public static unsafe bool TryAdvance() {
		var unit = Plugin.GameGui.GetAddonByName(Addon);
		if (unit.IsNull || !unit.IsVisible || !unit.IsReady)
			return false;

		var talk = (AtkUnitBase*)(nint)unit;

		// ⚠⚠ A SYNTHETIC CLICK, not a callback, and the difference is not cosmetic. FireCallbackInt
		// is what answers SelectYesno; Talk does not advance from it. Talk advances from a mouse
		// event on the box itself, which is why all three of down/click/up are sent -- the addon
		// tracks the press, and a click without its down is ignored.
		//
		// ⭐ Adapted from ECommons' AddonMaster.Talk (NightmareXIV, MIT), which is where the shape of
		// this came from. MIT, unlike YesAlready, so adapting it is fine; it is rewritten to our
		// idiom rather than copied, and it needs none of the library.
		//
		// ⚠ 132 is a magic value carried over verbatim rather than reasoned about. It decodes as
		// Pooled | Unk3 in AtkEventStateFlags, and "Unk3" is the honest state of knowledge there. It
		// is written as the number, not as the flags, so that an enum renumbering upstream cannot
		// silently change what we send -- this is the value that is known to work.
		var evt = new AtkEvent {
			Listener = (AtkEventListener*)talk,
			Target = &AtkStage.Instance()->AtkEventTarget,
			State = new AtkEventState { StateFlags = (AtkEventStateFlags)132 },
		};
		var data = default(AtkEventData);

		talk->ReceiveEvent(AtkEventType.MouseDown, 0, &evt, &data);
		talk->ReceiveEvent(AtkEventType.MouseClick, 0, &evt, &data);
		talk->ReceiveEvent(AtkEventType.MouseUp, 0, &evt, &data);
		return true;
	}
}
