using System;

namespace DeserokUtils.UI;

internal readonly record struct TabEntry(string Title, string Summary, Action? Draw) {

	public string[] Domains { get; init; } = System.Array.Empty<string>();

	public Dalamud.Interface.FontAwesomeIcon Icon { get; init; }

	public Func<bool>? Get { get; init; }

	public Action<bool>? Set { get; init; }

	public Func<Input.Keybind?>? Bind { get; init; }

	public string? BindName { get; init; }

	public bool BindRepeats { get; init; }

	public Action? Diagnostics { get; init; }
}
