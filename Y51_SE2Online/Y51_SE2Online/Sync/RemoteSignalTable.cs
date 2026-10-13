using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

public sealed class RemoteSignalTable
{
	private readonly object _sync = new object();

	private readonly Dictionary<ushort, (SignalKeyDef Def, string? Problem)> _byKey = new Dictionary<ushort, (SignalKeyDef, string)>();

	public int Count
	{
		get
		{
			lock (_sync)
			{
				return _byKey.Count;
			}
		}
	}

	public List<string> Add(IEnumerable<SignalKeyDef> defs, Func<SignalKeyDef, string?>? check)
	{
		List<string> list = new List<string>();
		foreach (SignalKeyDef def in defs)
		{
			string text;
			try
			{
				text = ((check == null) ? "signals disabled" : check(def));
			}
			catch (Exception ex)
			{
				text = "check threw " + ex.GetType().Name + ": " + ex.Message;
			}
			if (text != null)
			{
				list.Add("signal " + def.Name + ": " + text);
			}
			lock (_sync)
			{
				_byKey[def.Key] = (def, text);
			}
		}
		return list;
	}

	public bool TryGet(ushort key, out SignalKeyDef def)
	{
		lock (_sync)
		{
			if (_byKey.TryGetValue(key, out (SignalKeyDef, string) value) && value.Item2 == null)
			{
				(def, _) = value;
				return true;
			}
		}
		def = default;
		return false;
	}

	public bool IsKnown(ushort key)
	{
		lock (_sync)
		{
			return _byKey.ContainsKey(key);
		}
	}
}
