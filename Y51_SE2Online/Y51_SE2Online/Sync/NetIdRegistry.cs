using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

public sealed class NetIdRegistry<T> where T : class
{
	private readonly object _sync = new object();

	private readonly Dictionary<T, uint> _ids = new Dictionary<T, uint>(ReferenceEqualityComparer.Instance);

	private readonly Dictionary<uint, T> _objects = new Dictionary<uint, T>();

	private uint _next = 2u;

	public uint NextId
	{
		get
		{
			lock (_sync)
			{
				return _next;
			}
		}
	}

	public int Count
	{
		get
		{
			lock (_sync)
			{
				return _ids.Count;
			}
		}
	}

	public uint GetOrAssign(T obj)
	{
		lock (_sync)
		{
			if (_ids.TryGetValue(obj, out var value))
			{
				return value;
			}
			value = _next++;
			_ids[obj] = value;
			_objects[value] = obj;
			return value;
		}
	}

	public void Bind(T obj, uint id)
	{
		lock (_sync)
		{
			if (_objects.TryGetValue(id, out var value))
			{
				_ids.Remove(value);
			}
			_ids[obj] = id;
			_objects[id] = obj;
		}
	}

	public bool TryGet(T obj, out uint id)
	{
		lock (_sync)
		{
			return _ids.TryGetValue(obj, out id);
		}
	}

	public bool TryGet(uint id, out T obj)
	{
		lock (_sync)
		{
			return _objects.TryGetValue(id, out obj);
		}
	}

	public bool Remove(T obj, out uint id)
	{
		lock (_sync)
		{
			if (!_ids.Remove(obj, out id))
			{
				return false;
			}
			_objects.Remove(id);
			return true;
		}
	}

	public void Clear()
	{
		lock (_sync)
		{
			_ids.Clear();
			_objects.Clear();
			_next = 2u;
		}
	}
}
