using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

public sealed class TypeKeyTable
{
	private readonly object _sync = new object();

	private readonly Dictionary<int, TypeKeyDef> _byTypeId = new Dictionary<int, TypeKeyDef>();

	private readonly List<TypeKeyDef> _all = new List<TypeKeyDef>();

	private readonly List<TypeKeyDef> _unannounced = new List<TypeKeyDef>();

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

	public bool TryGet(int localTypeId, out TypeKeyDef def)
	{
		lock (_sync)
		{
			return _byTypeId.TryGetValue(localTypeId, out def);
		}
	}

	public TypeKeyDef GetOrAdd(int localTypeId, Func<int, (string Name, int Size)> describe)
	{
		lock (_sync)
		{
			if (_byTypeId.TryGetValue(localTypeId, out var value))
			{
				return value;
			}
			var (name, size) = describe(localTypeId);
			if (_next == ushort.MaxValue)
			{
				throw new InvalidOperationException("type key space exhausted");
			}
			value = new TypeKeyDef(_next++, (ushort)Math.Clamp(size, 0, 65535), name);
			_byTypeId[localTypeId] = value;
			_all.Add(value);
			_unannounced.Add(value);
			return value;
		}
	}

	public List<TypeKeyDef> All()
	{
		lock (_sync)
		{
			return new List<TypeKeyDef>(_all);
		}
	}

	public List<TypeKeyDef> TakeUnannounced()
	{
		lock (_sync)
		{
			List<TypeKeyDef> result = new List<TypeKeyDef>(_unannounced);
			_unannounced.Clear();
			return result;
		}
	}
}
