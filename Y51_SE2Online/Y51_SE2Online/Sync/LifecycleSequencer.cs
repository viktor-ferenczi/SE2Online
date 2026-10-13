using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class LifecycleSequencer<T>
{
	private readonly object _sync = new object();

	private readonly SortedDictionary<uint, T> _early = new SortedDictionary<uint, T>();

	private readonly Queue<(uint Seq, T Item)> _ready = new Queue<(uint, T)>();

	private uint _next = 1u;

	public long Duplicates;

	public long Overflow;

	public int MaxBuffered { get; set; } = 100000;

	public uint NextExpected
	{
		get
		{
			lock (_sync)
			{
				return _next;
			}
		}
	}

	public int Buffered
	{
		get
		{
			lock (_sync)
			{
				return _early.Count;
			}
		}
	}

	public int Ready
	{
		get
		{
			lock (_sync)
			{
				return _ready.Count;
			}
		}
	}

	public bool Offer(uint seq, T item)
	{
		lock (_sync)
		{
			if (Seq.Newer(_next, seq) || _early.ContainsKey(seq))
			{
				Duplicates++;
				return false;
			}
			if (seq == _next)
			{
				_ready.Enqueue((seq, item));
				_next++;
				T value;
				while (_early.Remove(_next, out value))
				{
					_ready.Enqueue((_next, value));
					_next++;
				}
				return true;
			}
			if (_early.Count >= MaxBuffered)
			{
				Overflow++;
				return false;
			}
			_early[seq] = item;
			return true;
		}
	}

	public bool TryTake(out uint seq, out T item)
	{
		lock (_sync)
		{
			if (_ready.Count > 0)
			{
				(seq, item) = _ready.Dequeue();
				return true;
			}
		}
		seq = 0u;
		item = default;
		return false;
	}

	public bool TryPeek(out uint seq, out T item)
	{
		lock (_sync)
		{
			if (_ready.Count > 0)
			{
				(seq, item) = _ready.Peek();
				return true;
			}
		}
		seq = 0u;
		item = default;
		return false;
	}
}
