using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Y51_SE2Online.Probe;

public sealed class CodecSurface
{
	private readonly SortedDictionary<string, SortedDictionary<string, (string Detail, long Count, long Bytes)>> _byCategory = new(StringComparer.Ordinal);

	public void Add(string category, string key, string detail, long count, long bytes)
	{
		if (!_byCategory.TryGetValue(category, out var entries))
		{
			entries = _byCategory[category] = new SortedDictionary<string, (string, long, long)>(StringComparer.Ordinal);
		}
		entries.TryGetValue(key, out var existing);
		entries[key] = (string.IsNullOrEmpty(detail) ? existing.Detail ?? "" : detail, existing.Count + count, existing.Bytes + bytes);
	}

	public int Count(string category) => _byCategory.TryGetValue(category, out var entries) ? entries.Count : 0;

	public string Render(string header)
	{
		var sb = new StringBuilder();
		sb.AppendLine(header);
		foreach (var category in _byCategory)
		{
			sb.AppendLine();
			sb.AppendLine(CultureInfo.InvariantCulture, $"== {category.Key} ({category.Value.Count} distinct) ==");
			foreach (var entry in category.Value.OrderByDescending(k => k.Value.Count))
			{
				sb.Append(CultureInfo.InvariantCulture, $"{entry.Value.Count,10} x {entry.Value.Bytes,12} B  {entry.Key}");
				if (!string.IsNullOrEmpty(entry.Value.Detail))
				{
					sb.Append("  :: ").Append(entry.Value.Detail);
				}
				sb.AppendLine();
			}
		}
		return sb.ToString();
	}
}
