using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Y51_SE2Online.Probe;

public sealed class ProbeWindow
{
	public DateTime StartUtc;

	public double Seconds;

	public long ServerTicks;

	public long ClientTicks;

	public readonly DirectionTotals[] Dir = new DirectionTotals[2]
	{
		new DirectionTotals(),
		new DirectionTotals()
	};

	public readonly BundleTotals Bundles = new BundleTotals();

	public readonly List<ProbeRow> Rows = new List<ProbeRow>();

	public const string CsvHeader = "utc,window_s,server_ticks,direction,category,key,count,bytes,count_per_tick,bytes_per_tick";

	public static string Csv(string s)
	{
		if (s.IndexOfAny(new char[3] { ',', '"', '\n' }) < 0)
		{
			return s;
		}
		return "\"" + s.Replace("\"", "\"\"") + "\"";
	}

	public IEnumerable<string> ToCsvRows()
	{
		string utc = StartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
		string w = Seconds.ToString("0.0", CultureInfo.InvariantCulture);
		long ticks = Math.Max(1L, ServerTicks);
		for (int d = 0; d < 2; d++)
		{
			DirectionTotals t = Dir[d];
			Direction direction = (Direction)d;
			string dir = direction.ToString();
			yield return Row(dir, "total", "buffers", t.Buffers, t.InlineBytes);
			yield return Row(dir, "total", "commands", t.Commands, t.InlineBytes);
			yield return Row(dir, "total", "object_refs", t.ObjectRefs, 0L);
			yield return Row(dir, "total", "data_payload", 0L, t.DataPayloadBytes);
			yield return Row(dir, "total", "max_tick_bytes", 0L, t.MaxTickBytes);
			yield return Row(dir, "total", "p95_tick_bytes", 0L, Percentile(t.TickBytes, 0.95));
		}
		BundleTotals b = Bundles;
		yield return Row("S2C", "bundle", "bundles", b.Bundles, b.SerializedBytes);
		yield return Row("S2C", "bundle", "entities", b.Entities, b.SerializedBytes);
		yield return Row("S2C", "bundle", "skipped_entities", b.Skipped, 0L);
		yield return Row("S2C", "bundle", "max_bundle", b.MaxBundleEntities, b.MaxBundleBytes);
		yield return Row("S2C", "bundle", "size_failures", b.SizeFailures, 0L);
		foreach (ProbeRow item in Rows.OrderBy((ProbeRow r) => r.Direction).ThenBy((ProbeRow r) => r.Category, StringComparer.Ordinal).ThenByDescending((ProbeRow r) => r.Bytes))
		{
			yield return Row(item.Direction.ToString(), item.Category, item.Key, item.Count, item.Bytes);
		}
		string Row(string text, string cat, string key, long count, long bytes)
		{
			return string.Join(",", utc, w, ServerTicks.ToString(CultureInfo.InvariantCulture), text, cat, Csv(key),
				count.ToString(CultureInfo.InvariantCulture), bytes.ToString(CultureInfo.InvariantCulture),
				((double)count / ticks).ToString("0.###", CultureInfo.InvariantCulture), ((double)bytes / ticks).ToString("0.#", CultureInfo.InvariantCulture));
		}
	}

	public static int Percentile(List<int> values, double p)
	{
		if (values.Count == 0)
		{
			return 0;
		}
		int[] array = values.ToArray();
		Array.Sort(array);
		int value = (int)Math.Ceiling(p * (double)array.Length) - 1;
		return array[Math.Clamp(value, 0, array.Length - 1)];
	}

	public string Summary(int top = 6)
	{
		StringBuilder stringBuilder = new StringBuilder();
		double num = Math.Max(1L, ServerTicks);
		double value = ((Seconds > 0.0) ? ((double)ServerTicks / Seconds) : 0.0);
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		IFormatProvider invariantCulture = CultureInfo.InvariantCulture;
		IFormatProvider provider = invariantCulture;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(42, 4, stringBuilder2, invariantCulture);
		handler.AppendLiteral("probe ");
		handler.AppendFormatted(Seconds, "0.0");
		handler.AppendLiteral("s: server ticks ");
		handler.AppendFormatted(ServerTicks);
		handler.AppendLiteral(" (");
		handler.AppendFormatted(value, "0.0");
		handler.AppendLiteral("/s), client ticks ");
		handler.AppendFormatted(ClientTicks);
		stringBuilder3.Append(provider, ref handler);
		for (int d = 0; d < 2; d++)
		{
			DirectionTotals directionTotals = Dir[d];
			double value2 = (double)directionTotals.InlineBytes / num;
			double value3 = ((Seconds > 0.0) ? ((double)directionTotals.InlineBytes / 1024.0 / Seconds) : 0.0);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			invariantCulture = CultureInfo.InvariantCulture;
			IFormatProvider provider2 = invariantCulture;
			handler = new StringBuilder.AppendInterpolatedStringHandler(105, 10, stringBuilder2, invariantCulture);
			handler.AppendLiteral("\n  ");
			handler.AppendFormatted((Direction)d);
			handler.AppendLiteral(": ");
			handler.AppendFormatted((double)directionTotals.InlineBytes / 1024.0, "0.0");
			handler.AppendLiteral(" KiB in ");
			handler.AppendFormatted(directionTotals.Buffers);
			handler.AppendLiteral(" buffers = ");
			handler.AppendFormatted(value2, "0");
			handler.AppendLiteral(" B/tick (");
			handler.AppendFormatted(value3, "0.0");
			handler.AppendLiteral(" KiB/s), ");
			handler.AppendLiteral("max ");
			handler.AppendFormatted(directionTotals.MaxTickBytes);
			handler.AppendLiteral(" B, p95 ");
			handler.AppendFormatted(Percentile(directionTotals.TickBytes, 0.95));
			handler.AppendLiteral(" B; ");
			handler.AppendFormatted((double)directionTotals.Commands / num, "0.0");
			handler.AppendLiteral(" cmds/tick, ");
			handler.AppendFormatted((double)directionTotals.ObjectRefs / num, "0.0");
			handler.AppendLiteral(" objrefs/tick, ");
			handler.AppendLiteral("data payload ");
			handler.AppendFormatted((double)directionTotals.DataPayloadBytes / num, "0");
			handler.AppendLiteral(" B/tick");
			stringBuilder4.Append(provider2, ref handler);
			string[] array = new string[4] { "data", "signal", "story", "invoke" };
			foreach (string cat in array)
			{
				List<ProbeRow> list = (from r in Rows
					where r.Direction == (Direction)d && r.Category == cat
					orderby r.Count descending
					select r).Take(top).ToList();
				if (list.Count != 0)
				{
					long value4 = Rows.Where((ProbeRow r) => r.Direction == (Direction)d && r.Category == cat).Sum((ProbeRow r) => r.Count);
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder5 = stringBuilder2;
					invariantCulture = CultureInfo.InvariantCulture;
					IFormatProvider provider3 = invariantCulture;
					handler = new StringBuilder.AppendInterpolatedStringHandler(10, 2, stringBuilder2, invariantCulture);
					handler.AppendLiteral("\n    ");
					handler.AppendFormatted(cat);
					handler.AppendLiteral(" (");
					handler.AppendFormatted(value4);
					handler.AppendLiteral("): ");
					stringBuilder5.Append(provider3, ref handler);
					stringBuilder.Append(string.Join(", ", list.Select((ProbeRow r) => $"{r.Key} x{r.Count}")));
				}
			}
		}
		BundleTotals bundles = Bundles;
		if (bundles.Bundles > 0)
		{
			string text;
			if (bundles.SizedBundles <= 0)
			{
				text = "not sized";
			}
			else
			{
				text = string.Format(CultureInfo.InvariantCulture, "serialized {0:0.0} KiB, largest {1:0.0} KiB / {2} entities, {3:0} ms spent sizing",
					bundles.SerializedBytes / 1024.0, bundles.MaxBundleBytes / 1024.0, bundles.MaxBundleEntities, bundles.SerializeMs);
			}
			string value5 = text;
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			invariantCulture = CultureInfo.InvariantCulture;
			IFormatProvider provider4 = invariantCulture;
			handler = new StringBuilder.AppendInterpolatedStringHandler(41, 4, stringBuilder2, invariantCulture);
			handler.AppendLiteral("\n  bundles: ");
			handler.AppendFormatted(bundles.Bundles);
			handler.AppendLiteral(" with ");
			handler.AppendFormatted(bundles.Entities);
			handler.AppendLiteral(" entities (+");
			handler.AppendFormatted(bundles.Skipped);
			handler.AppendLiteral(" skipped), ");
			handler.AppendFormatted(value5);
			stringBuilder6.Append(provider4, ref handler);
			if (bundles.SizeFailures > 0)
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder7 = stringBuilder2;
				invariantCulture = CultureInfo.InvariantCulture;
				IFormatProvider provider5 = invariantCulture;
				handler = new StringBuilder.AppendInterpolatedStringHandler(18, 1, stringBuilder2, invariantCulture);
				handler.AppendLiteral(", ");
				handler.AppendFormatted(bundles.SizeFailures);
				handler.AppendLiteral(" sizing failures");
				stringBuilder7.Append(provider5, ref handler);
			}
		}
		return stringBuilder.ToString();
	}
}
