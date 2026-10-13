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

	public readonly DirectionTotals[] Dir = [new DirectionTotals(), new DirectionTotals()];

	public readonly BundleTotals Bundles = new();

	public readonly List<ProbeRow> Rows = new();

	public const string CsvHeader = "utc,window_s,server_ticks,direction,category,key,count,bytes,count_per_tick,bytes_per_tick";

	public static string Csv(string s)
	{
		if (s.IndexOfAny(new[] { ',', '"', '\n' }) < 0)
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
		foreach (ProbeRow item in Rows.OrderBy(r => r.Direction).ThenBy(r => r.Category, StringComparer.Ordinal).ThenByDescending(r => r.Bytes))
		{
			yield return Row(item.Direction.ToString(), item.Category, item.Key, item.Count, item.Bytes);
		}

		string Row(string dirName, string cat, string key, long count, long bytes)
		{
			return string.Join(",", utc, w, ServerTicks.ToString(CultureInfo.InvariantCulture), dirName, cat, Csv(key),
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
		int[] sorted = values.ToArray();
		Array.Sort(sorted);
		int rank = (int)Math.Ceiling(p * sorted.Length) - 1;
		return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
	}

	public string Summary(int top = 6)
	{
		var sb = new StringBuilder();
		double ticks = Math.Max(1L, ServerTicks);
		double ticksPerSecond = Seconds > 0.0 ? ServerTicks / Seconds : 0.0;
		sb.Append(CultureInfo.InvariantCulture, $"probe {Seconds:0.0}s: server ticks {ServerTicks} ({ticksPerSecond:0.0}/s), client ticks {ClientTicks}");
		for (int d = 0; d < 2; d++)
		{
			DirectionTotals t = Dir[d];
			double bytesPerTick = t.InlineBytes / ticks;
			double kibPerSecond = Seconds > 0.0 ? t.InlineBytes / 1024.0 / Seconds : 0.0;
			sb.Append(CultureInfo.InvariantCulture,
				$"\n  {(Direction)d}: {t.InlineBytes / 1024.0:0.0} KiB in {t.Buffers} buffers = {bytesPerTick:0} B/tick ({kibPerSecond:0.0} KiB/s), "
				+ $"max {t.MaxTickBytes} B, p95 {Percentile(t.TickBytes, 0.95)} B; {t.Commands / ticks:0.0} cmds/tick, {t.ObjectRefs / ticks:0.0} objrefs/tick, "
				+ $"data payload {t.DataPayloadBytes / ticks:0} B/tick");
			foreach (string cat in new[] { "data", "signal", "story", "invoke" })
			{
				List<ProbeRow> topRows = Rows.Where(r => r.Direction == (Direction)d && r.Category == cat)
					.OrderByDescending(r => r.Count)
					.Take(top)
					.ToList();
				if (topRows.Count != 0)
				{
					long total = Rows.Where(r => r.Direction == (Direction)d && r.Category == cat).Sum(r => r.Count);
					sb.Append(CultureInfo.InvariantCulture, $"\n    {cat} ({total}): ");
					sb.Append(string.Join(", ", topRows.Select(r => $"{r.Key} x{r.Count}")));
				}
			}
		}
		BundleTotals bundles = Bundles;
		if (bundles.Bundles > 0)
		{
			string sizing = bundles.SizedBundles <= 0
				? "not sized"
				: string.Format(CultureInfo.InvariantCulture, "serialized {0:0.0} KiB, largest {1:0.0} KiB / {2} entities, {3:0} ms spent sizing",
					bundles.SerializedBytes / 1024.0, bundles.MaxBundleBytes / 1024.0, bundles.MaxBundleEntities, bundles.SerializeMs);
			sb.Append(CultureInfo.InvariantCulture, $"\n  bundles: {bundles.Bundles} with {bundles.Entities} entities (+{bundles.Skipped} skipped), {sizing}");
			if (bundles.SizeFailures > 0)
			{
				sb.Append(CultureInfo.InvariantCulture, $", {bundles.SizeFailures} sizing failures");
			}
		}
		return sb.ToString();
	}
}
