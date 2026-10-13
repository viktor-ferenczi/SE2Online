using System;
using System.Collections.Generic;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Sync;

public sealed class ClientBlobStore
{
	public enum Gate
	{
		Ready,
		Waiting,
		Stalled
	}

	private sealed class Partial
	{
		public byte[] Data;

		public int Received;
	}

	private readonly object _sync = new object();

	private readonly Dictionary<Guid, byte[]> _complete = new Dictionary<Guid, byte[]>();

	private readonly HashSet<Guid> _missingOnHost = new HashSet<Guid>();

	private readonly Dictionary<Guid, Partial> _partial = new Dictionary<Guid, Partial>();

	private readonly List<Guid> _fresh = new List<Guid>();

	private long _lastProgressMs;

	public long Pieces;

	public long BytesReceived;

	public long Rejected;

	public long StallMs { get; set; } = 30000L;

	public int MaxBlobBytes { get; set; } = 536870912;

	public int Count
	{
		get
		{
			lock (_sync)
			{
				return _complete.Count;
			}
		}
	}

	public long CompleteBytes
	{
		get
		{
			lock (_sync)
			{
				long num = 0L;
				foreach (byte[] value in _complete.Values)
				{
					num += value.Length;
				}
				return num;
			}
		}
	}

	public int MissingOnHostCount
	{
		get
		{
			lock (_sync)
			{
				return _missingOnHost.Count;
			}
		}
	}

	public bool HasFresh
	{
		get
		{
			lock (_sync)
			{
				return _fresh.Count > 0;
			}
		}
	}

	public bool Accept(BlobPiece p, long now)
	{
		lock (_sync)
		{
			Pieces++;
			_lastProgressMs = now;
			if (_complete.ContainsKey(p.Id))
			{
				return true;
			}
			if (p.MissingOnHost)
			{
				_partial.Remove(p.Id);
				if (_missingOnHost.Add(p.Id))
				{
					_fresh.Add(p.Id);
				}
				return true;
			}
			if (p.Total > MaxBlobBytes || p.Offset < 0 || (long)p.Offset + (long)p.Bytes.Length > p.Total)
			{
				Rejected++;
				return false;
			}
			BytesReceived += p.Bytes.Length;
			if (p.Offset == 0 && p.Bytes.Length == p.Total)
			{
				Complete(p.Id, p.Bytes);
				return true;
			}
			if (!_partial.TryGetValue(p.Id, out Partial value) || value.Data.Length != p.Total)
			{
				Dictionary<Guid, Partial> partial = _partial;
				Guid id = p.Id;
				Partial partial2 = new Partial
				{
					Data = new byte[p.Total]
				};
				value = partial2;
				partial[id] = partial2;
			}
			p.Bytes.CopyTo(value.Data, p.Offset);
			value.Received += p.Bytes.Length;
			if (value.Received >= p.Total)
			{
				_partial.Remove(p.Id);
				Complete(p.Id, value.Data);
			}
			return true;
		}
	}

	private void Complete(Guid id, byte[] data)
	{
		_complete[id] = data;
		_missingOnHost.Remove(id);
		_fresh.Add(id);
	}

	public bool Has(Guid id)
	{
		lock (_sync)
		{
			return _complete.ContainsKey(id);
		}
	}

	public bool IsMissingOnHost(Guid id)
	{
		lock (_sync)
		{
			return _missingOnHost.Contains(id);
		}
	}

	public Gate Check(IReadOnlyList<Guid> required, long now, out int absent)
	{
		absent = 0;
		if (required.Count == 0)
		{
			return Gate.Ready;
		}
		lock (_sync)
		{
			int num = 0;
			foreach (Guid item in required)
			{
				if (!_complete.ContainsKey(item))
				{
					absent++;
					if (!_missingOnHost.Contains(item))
					{
						num++;
					}
				}
			}
			if (num == 0)
			{
				return Gate.Ready;
			}
			return (now - _lastProgressMs < StallMs) ? Gate.Waiting : Gate.Stalled;
		}
	}

	public void Touch(long now)
	{
		lock (_sync)
		{
			if (_lastProgressMs < now - StallMs)
			{
				_lastProgressMs = now;
			}
		}
	}

	public List<(Guid Id, byte[]? Data)> TakeFresh()
	{
		lock (_sync)
		{
			List<(Guid, byte[])> list = new List<(Guid, byte[])>(_fresh.Count);
			foreach (Guid item in _fresh)
			{
				list.Add((item, _complete.TryGetValue(item, out byte[] value) ? value : null));
			}
			_fresh.Clear();
			return list;
		}
	}

	public List<(Guid Id, byte[] Data)> All()
	{
		lock (_sync)
		{
			List<(Guid, byte[])> list = new List<(Guid, byte[])>(_complete.Count);
			foreach (KeyValuePair<Guid, byte[]> item in _complete)
			{
				list.Add((item.Key, item.Value));
			}
			return list;
		}
	}

	public string Describe()
	{
		return $"{Count} blobs ({MpLogExtensions.Bytes(CompleteBytes)}), {MissingOnHostCount} missing on the host, {Pieces} pieces";
	}
}
