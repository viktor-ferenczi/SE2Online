using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using Keen.VRage.Library.Utils;

namespace Y51_SE2Online.SyncEngine;

internal sealed class StringIdTable
{
	public const string Prefix = "\u0001sid\u0001";

	private readonly HashSet<int> _announced = new();

	private readonly List<string> _pending = new();

	private readonly object _sync = new();

	public long Announced;

	private readonly Dictionary<int, int> _toLocal = new();

	private readonly Dictionary<int, int> _toHost = new();

	public long Registered;

	public long Translated;

	public long Unknown;

	public static StringIdTable? Client;

	public static string Entry(int hostIndex, string text) =>
		Prefix + hostIndex.ToString(CultureInfo.InvariantCulture) + "\u0001" + text;

	public void NoteHost(ReadOnlySpan<byte> payload, int[] offsets)
	{
		foreach (int offset in offsets)
		{
			if (offset < 0 || offset + 4 > payload.Length)
			{
				continue;
			}
			int id = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
			if (id == 0)
			{
				continue;
			}
			lock (_sync)
			{
				if (_announced.Add(id))
				{
					string text;
					try
					{
						text = StringId.FromId(id).String ?? "";
					}
					catch (Exception)
					{
						text = "";
					}
					_pending.Add(Entry(id, text));
					Announced++;
				}
			}
		}
	}

	public List<string>? TakePending()
	{
		lock (_sync)
		{
			if (_pending.Count == 0)
			{
				return null;
			}
			var result = new List<string>(_pending);
			_pending.Clear();
			return result;
		}
	}

	public bool TryRegister(string entry)
	{
		if (!entry.StartsWith(Prefix, StringComparison.Ordinal))
		{
			return false;
		}
		int separator = entry.IndexOf('\u0001', Prefix.Length);
		if (separator < 0 || !int.TryParse(entry.AsSpan(Prefix.Length, separator - Prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hostId))
		{
			return true;
		}
		int localId = StringId.Get(entry.Substring(separator + 1)).Id;
		lock (_toLocal)
		{
			_toLocal[hostId] = localId;
			_toHost[localId] = hostId;
		}
		Registered++;
		return true;
	}

	public byte[]? Map(byte[] payload, int[] offsets, bool toLocal)
	{
		byte[] mapped = (byte[])payload.Clone();
		lock (_toLocal)
		{
			Dictionary<int, int> map = toLocal ? _toLocal : _toHost;
			foreach (int offset in offsets)
			{
				if (offset < 0 || offset + 4 > mapped.Length)
				{
					return null;
				}
				int id = BinaryPrimitives.ReadInt32LittleEndian(mapped.AsSpan(offset));
				if (id != 0)
				{
					if (!map.TryGetValue(id, out int other))
					{
						Unknown++;
						return null;
					}
					BinaryPrimitives.WriteInt32LittleEndian(mapped.AsSpan(offset), other);
				}
			}
		}
		Translated++;
		return mapped;
	}

	public string Describe() =>
		$"StringIds: {Registered} from the host, {Translated} translated, {Unknown} unknown (dropped)";
}
