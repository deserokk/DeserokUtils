using System.Text;

namespace DeserokUtils.UI;

internal static class DiagLog {
	private static readonly StringBuilder Buffer = new();

	internal static void Begin() => Buffer.Clear();

	internal static void Section(string title) {
		if (Buffer.Length > 0) Buffer.AppendLine();
		Buffer.Append('[').Append(title).AppendLine("]");
	}

	internal static void Line(string text) => Buffer.Append("  ").AppendLine(text.TrimEnd());

	internal static void Row(string label, bool ok, string detail)
		=> Line($"{(ok ? "PASS" : "no  ")} {label}{(detail.Length > 0 ? "  " + detail : string.Empty)}");

	internal static string Text => Buffer.ToString();

	internal static bool Empty => Buffer.Length == 0;
}
