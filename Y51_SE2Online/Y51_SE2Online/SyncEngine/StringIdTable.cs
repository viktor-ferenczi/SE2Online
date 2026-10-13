using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using Keen.VRage.Library.Utils;

namespace Y51_SE2Online.SyncEngine;

internal sealed class StringIdTable
{
	public const string Prefix = "\u0001sid\u0001";

	private readonly HashSet<int> _announced = new HashSet<int>();

	private readonly List<string> _pending = new List<string>();

	private readonly object _sync = new object();

	public long Announced;

	private readonly Dictionary<int, int> _toLocal = new Dictionary<int, int>();

	private readonly Dictionary<int, int> _toHost = new Dictionary<int, int>();

	public long Registered;

	public long Translated;

	public long Unknown;

	public static StringIdTable? Client;

	public static string Entry(int hostIndex, string text)
	{
		return "\u0001sid\u0001" + hostIndex.ToString(CultureInfo.InvariantCulture) + "\u0001" + text;
	}

	public void NoteHost(ReadOnlySpan<byte> payload, int[] offsets)
	{
		foreach (int num in offsets)
		{
			if (num < 0 || num + 4 > payload.Length)
			{
				continue;
			}
			int num2 = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(num));
			if (num2 == 0)
			{
				continue;
			}
			lock (_sync)
			{
				if (_announced.Add(num2))
				{
					string text;
					try
					{
						text = StringId.FromId(num2).String ?? "";
					}
					catch (Exception)
					{
						text = "";
					}
					_pending.Add(Entry(num2, text));
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
			List<string> result = new List<string>(_pending);
			_pending.Clear();
			return result;
		}
	}

	public bool TryRegister(string entry)
	{
		if (!entry.StartsWith("\u0001sid\u0001", StringComparison.Ordinal))
		{
			return false;
		}
		int num = entry.IndexOf('\u0001', "\u0001sid\u0001".Length);
		if (num < 0 || !int.TryParse(entry.AsSpan("\u0001sid\u0001".Length, num - "\u0001sid\u0001".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			return true;
		}
		int id = StringId.Get(entry.Substring(num + 1)).Id;
		lock (_toLocal)
		{
			_toLocal[result] = id;
			_toHost[id] = result;
		}
		Registered++;
		return true;
	}

	public byte[]? Map(byte[] payload, int[] offsets, bool toLocal)
	{
		byte[] array = (byte[])payload.Clone();
		lock (_toLocal)
		{
			Dictionary<int, int> dictionary = (toLocal ? _toLocal : _toHost);
			foreach (int num in offsets)
			{
				if (num < 0 || num + 4 > array.Length)
				{
					return null;
				}
				int num2 = BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(num));
				if (num2 != 0)
				{
					if (!dictionary.TryGetValue(num2, out var value))
					{
						Unknown++;
						return null;
					}
					BinaryPrimitives.WriteInt32LittleEndian(array.AsSpan(num), value);
				}
			}
		}
		Translated++;
		return array;
	}

	public string Describe()
	{
		return $"StringIds: {Registered} from the host, {Translated} translated, {Unknown} unknown (dropped)";
	}
}
