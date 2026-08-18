using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DeserokUtils.UI;

/// <summary>
/// What a feature tells the window about itself.
///
/// ⚠ A DESCRIPTOR, not an interface. The temptation with five features is to declare IFeature and
/// have everything implement it -- and the feature shapes still refuse to agree (a hook, a poller, a
/// step machine, a bare command handler, a chat filter). What they DO agree on is how they present,
/// so that is the only thing given a type. See DeserokUtils.md on why the interface keeps not
/// happening.
///
/// ⭐ <see cref="Summary"/> is the future-proofing, and it costs one string. SimpleTweaks' entire UI
/// -- categories, collapsed rows, and the search box that makes a hundred tweaks usable -- is built
/// on every tweak having a name and a one-line description. Having that field from the start means
/// search is an addition later rather than a refactor. Nothing reads it yet except grouped tabs.
/// </summary>
internal readonly record struct TabEntry(string? Group, string Title, string Summary, Action Draw);

/// <summary>
/// One tab per feature, or one tab per GROUP of related features.
///
/// The window still knows nothing about any feature -- it is handed descriptors, so a new utility
/// never edits this file.
///
/// ⭐ Grouping exists because the macro tools crossed the threshold: CastWatch, /ifmo and the icon
/// resolver are three faces of "FFXIV macros are bad". Five top-level tabs was fine; seven, with two
/// of them near-duplicates of a third, is where a reader starts hunting. Anything ungrouped stays a
/// plain top-level tab, which is every other feature -- grouping is earned by having relatives, not
/// applied on principle.
/// </summary>
internal sealed class MainWindow: Window {
	private readonly IReadOnlyList<TabEntry> tabs;

	public MainWindow(IReadOnlyList<TabEntry> tabs): base("DeserokUtils") {
		this.tabs = tabs;
		this.SizeConstraints = new WindowSizeConstraints {
			MinimumSize = new Vector2(480, 340),
			MaximumSize = new Vector2(1400, 1000),
		};
	}

	public override void Draw() {
		if (ImGui.BeginTabBar("dsu_tabs")) {
			// ⚠ Ordered by first appearance, not alphabetically. The construction order in Plugin.cs
			// is the order features were built, which is the order deserok thinks about them in.
			foreach (var group in this.tabs.GroupBy(t => t.Group)) {
				if (group.Key is null) {
					foreach (var entry in group)
						DrawPlainTab(entry);
				}
				else {
					DrawGroupedTab(group.Key, group.ToList());
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

	private static void DrawPlainTab(TabEntry entry) {
		if (!ImGui.BeginTabItem(entry.Title))
			return;
		ImGui.BeginChild($"dsu_tab_{entry.Title}", new Vector2(0, -28f), false);
		entry.Draw();
		ImGui.EndChild();
		ImGui.EndTabItem();
	}

	/// <summary>
	/// Related features as collapsible sections, the shape SimpleTweaks uses.
	///
	/// ⭐ Collapsed by DEFAULT, and that is right rather than merely tidy: most of what these
	/// sections contain is reference -- command tables, token explanations, macro templates -- read
	/// once and then in the way. An expander turns permanent clutter into something you open when you
	/// have forgotten the syntax.
	///
	/// ⚠ Nested tab bars were the other option and were not taken. Tabs hide their siblings; headers
	/// let you see everything the group contains at a glance, which is what makes the grouping worth
	/// having in the first place.
	/// </summary>
	private static void DrawGroupedTab(string group, IReadOnlyList<TabEntry> entries) {
		if (!ImGui.BeginTabItem(group))
			return;

		ImGui.BeginChild($"dsu_group_{group}", new Vector2(0, -28f), false);
		foreach (var entry in entries) {
			bool open = ImGui.CollapsingHeader(entry.Title);
			if (entry.Summary.Length > 0) {
				ImGui.Indent();
				ImGui.TextDisabled(entry.Summary);
				ImGui.Unindent();
			}
			if (open) {
				ImGui.Indent();
				ImGui.Spacing();
				entry.Draw();
				ImGui.Unindent();
			}
			ImGui.Spacing();
		}
		ImGui.EndChild();
		ImGui.EndTabItem();
	}
}
