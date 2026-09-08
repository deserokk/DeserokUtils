using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeserokUtils.Features.Interact;

internal static class TalkAdvance {
	private const string Addon = "Talk";

	public static unsafe bool TryAdvance() {
		var unit = Plugin.GameGui.GetAddonByName(Addon);
		if (unit.IsNull || !unit.IsVisible || !unit.IsReady)
			return false;

		var talk = (AtkUnitBase*)(nint)unit;

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
