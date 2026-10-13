using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

public sealed class RemoteTypeTable
{
	public readonly record struct Resolved(TypeKeyDef Def, int LocalTypeId, string? Problem);

	private readonly object _sync = new object();

	private readonly Dictionary<ushort, Resolved> _byKey = new Dictionary<ushort, Resolved>();

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

	public List<string> Add(IEnumerable<TypeKeyDef> defs, Func<string, (int TypeId, int Size)?> resolve)
	{
		List<string> list = new List<string>();
		foreach (TypeKeyDef def in defs)
		{
			(int, int)? tuple;
			try
			{
				tuple = resolve(def.Name);
			}
			catch (Exception ex)
			{
				tuple = null;
				list.Add($"type key {def.Key} '{def.Name}': resolve threw {ex.GetType().Name}: {ex.Message}");
			}
			Resolved value;
			if (tuple.HasValue)
			{
				value = ((tuple.Value.Item2 == def.Size) ? new Resolved(def, tuple.Value.Item1, null) : new Resolved(def, -1, $"size mismatch (host {def.Size} B, here {tuple.Value.Item2} B)"));
			}
			else
			{
				value = new Resolved(def, -1, "type not found in this game build");
			}
			if (value.Problem != null)
			{
				list.Add($"type key {def.Key} '{def.ShortName}': {value.Problem}; its updates are ignored");
			}
			lock (_sync)
			{
				_byKey[def.Key] = value;
			}
		}
		return list;
	}

	public bool TryResolve(ushort key, out int localTypeId, out int size)
	{
		lock (_sync)
		{
			if (_byKey.TryGetValue(key, out var value) && value.LocalTypeId >= 0)
			{
				localTypeId = value.LocalTypeId;
				size = value.Def.Size;
				return true;
			}
		}
		localTypeId = -1;
		size = 0;
		return false;
	}

	public bool IsKnown(ushort key)
	{
		lock (_sync)
		{
			return _byKey.ContainsKey(key);
		}
	}

	public string Name(ushort key)
	{
		lock (_sync)
		{
			Resolved value;
			return _byKey.TryGetValue(key, out value) ? value.Def.ShortName : $"key#{key}";
		}
	}
}
