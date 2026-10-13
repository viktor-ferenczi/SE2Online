using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

public sealed class SignalKeyTable
{
	private readonly object _sync = new object();

	private readonly Dictionary<string, SignalKeyDef> _byName = new Dictionary<string, SignalKeyDef>(StringComparer.Ordinal);

	private readonly List<SignalKeyDef> _all = new List<SignalKeyDef>();

	private readonly List<SignalKeyDef> _unannounced = new List<SignalKeyDef>();

	private ushort _next = 1;

	public int Count
	{
		get
		{
			lock (_sync)
			{
				return _all.Count;
			}
		}
	}

	public SignalKeyDef GetOrAdd(string name, ushort payloadBytes, string signature)
	{
		lock (_sync)
		{
			if (_byName.TryGetValue(name, out var value))
			{
				return value;
			}
			if (_next == ushort.MaxValue)
			{
				throw new InvalidOperationException("signal key space exhausted");
			}
			value = new SignalKeyDef(_next++, name, payloadBytes, signature);
			_byName[name] = value;
			_all.Add(value);
			_unannounced.Add(value);
			return value;
		}
	}

	public List<SignalKeyDef> All()
	{
		lock (_sync)
		{
			return new List<SignalKeyDef>(_all);
		}
	}

	public List<SignalKeyDef> TakeUnannounced()
	{
		lock (_sync)
		{
			List<SignalKeyDef> result = new List<SignalKeyDef>(_unannounced);
			_unannounced.Clear();
			return result;
		}
	}
}
