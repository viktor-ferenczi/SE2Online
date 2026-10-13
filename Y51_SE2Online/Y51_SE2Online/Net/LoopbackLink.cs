using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Net;

public sealed class LoopbackLink : IPacketLink, IDisposable
{
	private readonly object _sync;

	private readonly Random _rng;

	private readonly Queue<LinkEvent> _events = new Queue<LinkEvent>();

	private readonly List<(uint Conn, byte[] Data, bool Reliable)> _inbound = new List<(uint, byte[], bool)>();

	private readonly Dictionary<uint, (LoopbackLink Peer, uint PeerConn, ulong RemoteId)> _conns = new Dictionary<uint, (LoopbackLink, uint, ulong)>();

	private readonly Dictionary<uint, int> _pendingReliable = new Dictionary<uint, int>();

	private static uint _nextConn = 100u;

	public string Name { get; }

	public ulong LocalId { get; }

	public int MaxMessageSize { get; set; } = 524288;

	public bool AuthenticatesPeers => true;

	public double UnreliableDropRate { get; set; }

	public double UnreliableReorderRate { get; set; }

	public int SendBufferBytes { get; set; } = 4194304;

	public long SentMessages { get; private set; }

	public long DroppedUnreliable { get; private set; }

	public int MaxPendingReliableSeen { get; private set; }

	public LoopbackLink(string name, ulong localId, int seed = 1, object? sync = null)
	{
		Name = name;
		LocalId = localId;
		_rng = new Random(seed);
		_sync = sync ?? new object();
	}

	public static (uint ConnA, uint ConnB) Connect(LoopbackLink a, LoopbackLink b)
	{
		lock (a._sync)
		{
			uint num = _nextConn++;
			uint num2 = _nextConn++;
			a._conns[num] = (b, num2, b.LocalId);
			b._conns[num2] = (a, num, a.LocalId);
			a._events.Enqueue(new LinkEvent(LinkEventKind.Connected, num, b.LocalId));
			b._events.Enqueue(new LinkEvent(LinkEventKind.Connected, num2, a.LocalId));
			return (ConnA: num, ConnB: num2);
		}
	}

	public void Pump(List<LinkEvent> events, List<LinkMessage> messages)
	{
		lock (_sync)
		{
			while (_events.Count > 0)
			{
				events.Add(_events.Dequeue());
			}
			foreach (var (num, array, flag) in _inbound)
			{
				messages.Add(new LinkMessage(num, array));
				if (flag && _conns.TryGetValue(num, out (LoopbackLink, uint, ulong) value))
				{
					value.Item1.AddPending(value.Item2, -array.Length);
				}
			}
			_inbound.Clear();
		}
	}

	public LinkSendResult Send(uint conn, Channel channel, bool reliable, ReadOnlySpan<byte> frame)
	{
		lock (_sync)
		{
			if (!_conns.TryGetValue(conn, out (LoopbackLink, uint, ulong) value))
			{
				return LinkSendResult.Failed;
			}
			if (frame.Length > MaxMessageSize)
			{
				return LinkSendResult.Failed;
			}
			if (reliable && GetPendingReliableBytes(conn) + frame.Length > SendBufferBytes)
			{
				return LinkSendResult.Busy;
			}
			SentMessages++;
			if (!reliable && UnreliableDropRate > 0.0 && _rng.NextDouble() < UnreliableDropRate)
			{
				DroppedUnreliable++;
				return LinkSendResult.Ok;
			}
			byte[] array = frame.ToArray();
			List<(uint, byte[], bool)> inbound = value.Item1._inbound;
			if (!reliable && UnreliableReorderRate > 0.0 && inbound.Count > 0 && _rng.NextDouble() < UnreliableReorderRate)
			{
				int index = inbound.Count - 1;
				if (!inbound[index].Item3)
				{
					inbound.Insert(index, (value.Item2, array, false));
					return LinkSendResult.Ok;
				}
			}
			inbound.Add((value.Item2, array, reliable));
			if (reliable)
			{
				AddPending(conn, array.Length);
			}
			return LinkSendResult.Ok;
		}
	}

	private void AddPending(uint conn, int delta)
	{
		_pendingReliable.TryGetValue(conn, out var value);
		value += delta;
		_pendingReliable[conn] = value;
		if (value > MaxPendingReliableSeen)
		{
			MaxPendingReliableSeen = value;
		}
	}

	public int GetPendingReliableBytes(uint conn)
	{
		lock (_sync)
		{
			int value;
			return _pendingReliable.TryGetValue(conn, out value) ? value : 0;
		}
	}

	public void Close(uint conn, int code, string reason, bool linger)
	{
		lock (_sync)
		{
			if (!_conns.Remove(conn, out (LoopbackLink, uint, ulong) value))
			{
				return;
			}
			_pendingReliable.Remove(conn);
			if (!linger)
			{
				_inbound.RemoveAll(((uint Conn, byte[] Data, bool Reliable) m) => m.Conn == conn);
			}
			value.Item1._conns.Remove(value.Item2);
			value.Item1._pendingReliable.Remove(value.Item2);
			value.Item1._events.Enqueue(new LinkEvent(LinkEventKind.Disconnected, value.Item2, LocalId, code, reason, byPeer: true));
		}
	}

	public void SimulateConnectionLost(uint conn, int code, string reason)
	{
		lock (_sync)
		{
			if (_conns.Remove(conn, out (LoopbackLink, uint, ulong) value))
			{
				_events.Enqueue(new LinkEvent(LinkEventKind.Disconnected, conn, value.Item3, code, reason));
				value.Item1._conns.Remove(value.Item2);
				value.Item1._events.Enqueue(new LinkEvent(LinkEventKind.Disconnected, value.Item2, LocalId, code, reason));
			}
		}
	}

	public void Dispose()
	{
		lock (_sync)
		{
			foreach (uint item in new List<uint>(_conns.Keys))
			{
				Close(item, 1004, "dispose", linger: false);
			}
		}
	}
}
