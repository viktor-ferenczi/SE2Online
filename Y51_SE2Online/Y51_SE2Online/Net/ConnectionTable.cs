using System.Collections.Generic;

namespace Y51_SE2Online.Net;

public sealed class ConnectionTable
{
	public sealed class Entry
	{
		public uint Conn;

		public ulong Remote;

		public bool Incoming;

		public long CreatedMs;

		public bool Connected;
	}

	private readonly Dictionary<uint, Entry> _byConn = new Dictionary<uint, Entry>();

	public int Count => _byConn.Count;

	public IEnumerable<Entry> Entries => _byConn.Values;

	public bool TryGet(uint conn, out Entry entry)
	{
		return _byConn.TryGetValue(conn, out entry);
	}

	public List<Entry> Add(uint conn, ulong remote, bool incoming, long nowMs)
	{
		List<Entry> list = new List<Entry>();
		foreach (Entry value in _byConn.Values)
		{
			if (value.Remote == remote && value.Conn != conn)
			{
				list.Add(value);
			}
		}
		foreach (Entry item in list)
		{
			_byConn.Remove(item.Conn);
		}
		_byConn[conn] = new Entry
		{
			Conn = conn,
			Remote = remote,
			Incoming = incoming,
			CreatedMs = nowMs
		};
		return list;
	}

	public bool MarkConnected(uint conn)
	{
		if (!_byConn.TryGetValue(conn, out Entry value) || value.Connected)
		{
			return false;
		}
		value.Connected = true;
		return true;
	}

	public bool Remove(uint conn)
	{
		return _byConn.Remove(conn);
	}

	public bool Remove(uint conn, out Entry entry)
	{
		return _byConn.Remove(conn, out entry);
	}

	public int CountFor(ulong remote)
	{
		int num = 0;
		foreach (Entry value in _byConn.Values)
		{
			if (value.Remote == remote)
			{
				num++;
			}
		}
		return num;
	}

	public void Clear()
	{
		_byConn.Clear();
	}
}
