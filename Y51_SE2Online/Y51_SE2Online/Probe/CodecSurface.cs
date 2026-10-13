using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Y51_SE2Online.Probe;

public sealed class CodecSurface
{
	private readonly SortedDictionary<string, SortedDictionary<string, (string Detail, long Count, long Bytes)>> _byCategory = new SortedDictionary<string, SortedDictionary<string, (string, long, long)>>(StringComparer.Ordinal);

	public void Add(string category, string key, string detail, long count, long bytes)
	{
		if (!_byCategory.TryGetValue(category, out SortedDictionary<string, (string, long, long)> value))
		{
			value = (_byCategory[category] = new SortedDictionary<string, (string, long, long)>(StringComparer.Ordinal));
		}
		value.TryGetValue(key, out var value2);
		value[key] = (string.IsNullOrEmpty(detail) ? (value2.Item1 ?? "") : detail, value2.Item2 + count, value2.Item3 + bytes);
	}

	public int Count(string category)
	{
		if (!_byCategory.TryGetValue(category, out SortedDictionary<string, (string, long, long)> value))
		{
			return 0;
		}
		return value.Count;
	}

	public string Render(string header)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine(header);
		foreach (KeyValuePair<string, SortedDictionary<string, (string, long, long)>> item in _byCategory)
		{
			stringBuilder.AppendLine();
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			IFormatProvider invariantCulture = CultureInfo.InvariantCulture;
			IFormatProvider provider = invariantCulture;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(18, 2, stringBuilder2, invariantCulture);
			handler.AppendLiteral("== ");
			handler.AppendFormatted(item.Key);
			handler.AppendLiteral(" (");
			handler.AppendFormatted(item.Value.Count);
			handler.AppendLiteral(" distinct) ==");
			stringBuilder3.AppendLine(provider, ref handler);
			foreach (KeyValuePair<string, (string, long, long)> item2 in item.Value.OrderByDescending((KeyValuePair<string, (string Detail, long Count, long Bytes)> k) => k.Value.Count))
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder4 = stringBuilder2;
				invariantCulture = CultureInfo.InvariantCulture;
				IFormatProvider provider2 = invariantCulture;
				handler = new StringBuilder.AppendInterpolatedStringHandler(7, 3, stringBuilder2, invariantCulture);
				handler.AppendFormatted(item2.Value.Item2, 10);
				handler.AppendLiteral(" x ");
				handler.AppendFormatted(item2.Value.Item3, 12);
				handler.AppendLiteral(" B  ");
				handler.AppendFormatted(item2.Key);
				stringBuilder4.Append(provider2, ref handler);
				if (!string.IsNullOrEmpty(item2.Value.Item1))
				{
					stringBuilder.Append("  :: ").Append(item2.Value.Item1);
				}
				stringBuilder.AppendLine();
			}
		}
		return stringBuilder.ToString();
	}
}
