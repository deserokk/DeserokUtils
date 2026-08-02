using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DeserokUtils.UI;

/// <summary>
/// One tab per feature. The window knows nothing about any feature -- it is handed a title and a
/// draw delegate, so a new utility never edits this file beyond the list it is constructed with.
/// </summary>
internal sealed class MainWindow: Window {
	private readonly IReadOnlyList<(string Title, Action Draw)> tabs;

	public MainWindow(IReadOnlyList<(string Title, Action Draw)> tabs): base("DeserokUtils") {
		this.tabs = tabs;
		this.SizeConstraints = new WindowSizeConstraints {
			MinimumSize = new Vector2(480, 340),
			MaximumSize = new Vector2(1400, 1000),
		};
	}

	public override void Draw() {
		if (ImGui.BeginTabBar("dsu_tabs")) {
			foreach (var (title, draw) in this.tabs) {
				if (ImGui.BeginTabItem(title)) {
					ImGui.BeginChild("dsu_tab_body", new Vector2(0, -28f), false);
					draw();
					ImGui.EndChild();
					ImGui.EndTabItem();
				}
			}
			ImGui.EndTabBar();
		}

		ImGui.Separator();
		bool verbose = Plugin.Verbose;
		if (ImGui.Checkbox("Diagnostics to the Debug channel", ref verbose))
			Plugin.Verbose = verbose;
		ImGui.SameLine();
		ImGui.TextDisabled("(/dsu debug)");
	}
}
