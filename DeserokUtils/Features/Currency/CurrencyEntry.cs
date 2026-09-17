using System;

namespace DeserokUtils.Features.Currency;

[Serializable]
public sealed class CurrencyEntry {
	public uint ItemId { get; set; }

	public uint Icon { get; set; }

	public string Name { get; set; } = string.Empty;
}
