using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Net;

public sealed class TcpLink : IPacketLink, IDisposable
{
	private sealed class Conn
	{
		public uint Id;

		public Socket Socket;

		public string Endpoint;

		public bool Outgoing;

		public bool Connected;

		public long StartedMs;

		public readonly Queue<byte[]>[] Out = new Queue<byte[]>[3]
		{
			new Queue<byte[]>(),
			new Queue<byte[]>(),
			new Queue<byte[]>()
		};

		public byte[]? Current;

		public int CurrentOffset;

		public int PendingBytes;

		public byte[] Recv = new byte[65536];

		public int RecvLen;

		public int RemoteCloseCode;

		public string? RemoteCloseReason;

		public bool Lingering;

		public long LingerDeadlineMs;
	}

	public const ulong SyntheticIdBase = 9079256848778919936uL;

	private const byte CloseLane = byte.MaxValue;

	private const int HeaderBytes = 5;

	private readonly IMpLog _log;

	private readonly Func<long> _clock;

	private readonly Dictionary<uint, Conn> _conns = new Dictionary<uint, Conn>();

	private readonly List<Conn> _lingering = new List<Conn>();

	private readonly List<LinkEvent> _pendingEvents = new List<LinkEvent>();

	private Socket? _listener;

	private uint _nextId = 1u;

	private bool _disposed;

	public string Name { get; }

	public int MaxMessageSize { get; set; } = 524288;

	public bool AuthenticatesPeers => false;

	public int ConnectTimeoutMs { get; set; } = 10000;

	public int LingerTimeoutMs { get; set; } = 2000;

	public IPEndPoint? ListenEndpoint => _listener?.LocalEndPoint as IPEndPoint;

	public TcpLink(string name, IMpLog log, Func<long>? clock = null)
	{
		Name = name;
		_log = log;
		_clock = clock ?? new Func<long>(MpClock.NowMs);
	}

	public static ulong SyntheticId(uint conn)
	{
		return 0x7E00000000000000uL | (ulong)conn;
	}

	public static bool IsSyntheticId(ulong id)
	{
		return (id & 0xFF00000000000000uL) == 9079256848778919936L;
	}

	public static IPEndPoint ParseEndpoint(string text, IPAddress defaultAddress)
	{
		text = text.Trim();
		if (int.TryParse(text, out var result))
		{
			return new IPEndPoint(defaultAddress, CheckPort(result));
		}
		if (IPEndPoint.TryParse(text, out IPEndPoint result2) && result2.Port != 0)
		{
			return new IPEndPoint(result2.Address, CheckPort(result2.Port));
		}
		int num = text.LastIndexOf(':');
		if (num > 0 && int.TryParse(text.AsSpan(num + 1), out var result3))
		{
			string text2 = text.Substring(0, num);
			if (text2.Equals("localhost", StringComparison.OrdinalIgnoreCase))
			{
				return new IPEndPoint(IPAddress.Loopback, CheckPort(result3));
			}
			if (IPAddress.TryParse(text2, out IPAddress address))
			{
				return new IPEndPoint(address, CheckPort(result3));
			}
		}
		throw new FormatException("'" + text + "' is not <port>, <ipv4>:<port> or localhost:<port>");
	}

	private static int CheckPort(int p)
	{
		if (p <= 0 || p > 65535)
		{
			throw new FormatException($"port {p} out of range");
		}
		return p;
	}

	public void Listen(IPEndPoint endpoint)
	{
		Socket socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
		try
		{
			socket.ExclusiveAddressUse = true;
		}
		catch (Exception)
		{
		}
		socket.Bind(endpoint);
		socket.Listen(8);
		socket.Blocking = false;
		_listener = socket;
		_log.Info($"tcp: listening on {socket.LocalEndPoint}");
	}

	public uint Connect(IPEndPoint endpoint)
	{
		Socket socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
		{
			NoDelay = true,
			Blocking = false
		};
		Conn conn = new Conn
		{
			Id = _nextId++,
			Socket = socket,
			Endpoint = endpoint.ToString(),
			Outgoing = true,
			StartedMs = _clock()
		};
		try
		{
			socket.Connect(endpoint);
			conn.Connected = true;
		}
		catch (SocketException ex) when ((uint)(ex.SocketErrorCode - 10035) <= 2u)
		{
		}
		catch (SocketException ex2)
		{
			socket.Dispose();
			_pendingEvents.Add(new LinkEvent(LinkEventKind.Disconnected, conn.Id, SyntheticId(conn.Id), (int)ex2.SocketErrorCode, $"connect to {endpoint} failed: {ex2.SocketErrorCode}"));
			return conn.Id;
		}
		_conns[conn.Id] = conn;
		_log.Info($"tcp: connecting to {endpoint} (conn {conn.Id})");
		if (conn.Connected)
		{
			OnConnected(conn, _pendingEvents);
		}
		return conn.Id;
	}

	public bool HasConnection(uint conn)
	{
		return _conns.ContainsKey(conn);
	}

	public void Pump(List<LinkEvent> events, List<LinkMessage> messages)
	{
		if (_disposed)
		{
			return;
		}
		if (_pendingEvents.Count > 0)
		{
			events.AddRange(_pendingEvents);
			_pendingEvents.Clear();
		}
		Accept(events);
		long now = _clock();
		foreach (Conn item in Snapshot())
		{
			if (!item.Connected)
			{
				PollConnect(item, events, now);
			}
			else if (Flush(item, events))
			{
				Receive(item, events, messages);
			}
		}
		ServiceLingering(now);
	}

	private List<Conn> Snapshot()
	{
		return new List<Conn>(_conns.Values);
	}

	private void Accept(List<LinkEvent> events)
	{
		if (_listener == null)
		{
			return;
		}
		for (int i = 0; i < 16; i++)
		{
			Socket socket;
			try
			{
				socket = _listener.Accept();
			}
			catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
			{
				break;
			}
			catch (SocketException ex2)
			{
				_log.WarnOnce("tcp-accept-" + ex2.SocketErrorCode, $"tcp: accept failed: {ex2.SocketErrorCode}");
				break;
			}
			socket.Blocking = false;
			socket.NoDelay = true;
			Conn conn = new Conn
			{
				Id = _nextId++,
				Socket = socket,
				Endpoint = (socket.RemoteEndPoint?.ToString() ?? "?"),
				StartedMs = _clock()
			};
			_conns[conn.Id] = conn;
			_log.Info($"tcp: accepted connection from {conn.Endpoint} (conn {conn.Id})");
			OnConnected(conn, events);
		}
	}

	private void PollConnect(Conn c, List<LinkEvent> events, long now)
	{
		try
		{
			if (c.Socket.Poll(0, SelectMode.SelectWrite))
			{
				c.Connected = true;
				OnConnected(c, events);
				return;
			}
			if (c.Socket.Poll(0, SelectMode.SelectError))
			{
				SocketError socketError = (SocketError)(int)c.Socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
				Fail(c, events, (int)socketError, $"connect to {c.Endpoint} failed: {socketError}");
				return;
			}
		}
		catch (SocketException ex)
		{
			Fail(c, events, (int)ex.SocketErrorCode, $"connect to {c.Endpoint} failed: {ex.SocketErrorCode}");
			return;
		}
		if (now - c.StartedMs > ConnectTimeoutMs)
		{
			Fail(c, events, 10060, $"connect to {c.Endpoint} timed out after {ConnectTimeoutMs} ms");
		}
	}

	private void OnConnected(Conn c, List<LinkEvent> events)
	{
		_log.Info($"tcp: conn {c.Id} {(c.Outgoing ? "to" : "from")} {c.Endpoint}: CONNECTED (lanes: per-connection priority queues)");
		events.Add(new LinkEvent(LinkEventKind.Connected, c.Id, SyntheticId(c.Id)));
	}

	private void Fail(Conn c, List<LinkEvent> events, int code, string reason)
	{
		if (_conns.Remove(c.Id))
		{
			Dispose(c.Socket);
			events.Add(new LinkEvent(LinkEventKind.Disconnected, c.Id, SyntheticId(c.Id), code, reason));
		}
	}

	private bool Flush(Conn c, List<LinkEvent>? events)
	{
		while (true)
		{
			if (c.Current == null)
			{
				c.Current = NextMessage(c);
				c.CurrentOffset = 0;
				if (c.Current == null)
				{
					return true;
				}
			}
			int num;
			try
			{
				num = c.Socket.Send(c.Current, c.CurrentOffset, c.Current.Length - c.CurrentOffset, SocketFlags.None);
			}
			catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
			{
				return true;
			}
			catch (SocketException ex2)
			{
				if (events != null)
				{
					Fail(c, events, (int)ex2.SocketErrorCode, $"send failed: {ex2.SocketErrorCode}");
				}
				return false;
			}
			catch (ObjectDisposedException)
			{
				return false;
			}
			c.CurrentOffset += num;
			c.PendingBytes -= num;
			if (c.CurrentOffset < c.Current.Length)
			{
				break;
			}
			c.Current = null;
		}
		return true;
	}

	private static byte[]? NextMessage(Conn c)
	{
		Channel[] serviceOrder = LanePlan.ServiceOrder;
		foreach (Channel channel in serviceOrder)
		{
			Queue<byte[]> queue = c.Out[(uint)channel];
			if (queue.Count > 0)
			{
				return queue.Dequeue();
			}
		}
		return null;
	}

	private void Receive(Conn c, List<LinkEvent> events, List<LinkMessage> messages)
	{
		for (int i = 0; i < 64; i++)
		{
			if (c.RecvLen == c.Recv.Length)
			{
				Array.Resize(ref c.Recv, c.Recv.Length * 2);
			}
			int num;
			try
			{
				num = c.Socket.Receive(c.Recv, c.RecvLen, c.Recv.Length - c.RecvLen, SocketFlags.None);
			}
			catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
			{
				break;
			}
			catch (SocketException ex2)
			{
				SocketError socketErrorCode = ex2.SocketErrorCode;
				bool flag = (uint)(socketErrorCode - 10053) <= 1u;
				bool byPeer = flag;
				Closed(c, events, byPeer, (int)ex2.SocketErrorCode, $"receive failed: {ex2.SocketErrorCode}");
				break;
			}
			if (num == 0)
			{
				Parse(c, messages);
				Closed(c, events, byPeer: true, c.RemoteCloseCode, c.RemoteCloseReason ?? "connection closed by peer");
				break;
			}
			c.RecvLen += num;
			if (!Parse(c, messages))
			{
				Closed(c, events, byPeer: false, 1001, "bad tcp framing");
				break;
			}
		}
	}

	private bool Parse(Conn c, List<LinkMessage> messages)
	{
		int i;
		int num;
		for (i = 0; c.RecvLen - i >= 4; i += 4 + num)
		{
			num = (int)BinaryPrimitives.ReadUInt32LittleEndian(c.Recv.AsSpan(i));
			if (num < 1 || num > MaxMessageSize + 1)
			{
				return false;
			}
			if (c.RecvLen - i - 4 < num)
			{
				break;
			}
			byte b = c.Recv[i + 4];
			Span<byte> span = c.Recv.AsSpan(i + 5, num - 1);
			if (b == byte.MaxValue)
			{
				if (span.Length >= 6)
				{
					c.RemoteCloseCode = BinaryPrimitives.ReadInt32LittleEndian(span);
					int val = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
					c.RemoteCloseReason = Encoding.UTF8.GetString(span.Slice(6, Math.Min(val, span.Length - 6)));
				}
			}
			else
			{
				if (b > 2)
				{
					return false;
				}
				messages.Add(new LinkMessage(c.Id, span.ToArray()));
			}
		}
		if (i > 0)
		{
			Buffer.BlockCopy(c.Recv, i, c.Recv, 0, c.RecvLen - i);
			c.RecvLen -= i;
		}
		return true;
	}

	private void Closed(Conn c, List<LinkEvent> events, bool byPeer, int code, string reason)
	{
		if (_conns.Remove(c.Id))
		{
			Dispose(c.Socket);
			events.Add(new LinkEvent(LinkEventKind.Disconnected, c.Id, SyntheticId(c.Id), code, reason, byPeer));
		}
	}

	public LinkSendResult Send(uint conn, Channel channel, bool reliable, ReadOnlySpan<byte> frame)
	{
		if (!_conns.TryGetValue(conn, out Conn value) || !value.Connected)
		{
			return LinkSendResult.Failed;
		}
		if (frame.Length > MaxMessageSize)
		{
			return LinkSendResult.Failed;
		}
		byte[] array = new byte[5 + frame.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(array, (uint)(frame.Length + 1));
		array[4] = (byte)channel;
		frame.CopyTo(array.AsSpan(5));
		value.Out[(uint)channel].Enqueue(array);
		value.PendingBytes += array.Length;
		Flush(value, _pendingEvents);
		return LinkSendResult.Ok;
	}

	public int GetPendingReliableBytes(uint conn)
	{
		if (!_conns.TryGetValue(conn, out Conn value))
		{
			return int.MaxValue;
		}
		return value.PendingBytes;
	}

	public void Close(uint conn, int code, string reason, bool linger)
	{
		if (!_conns.Remove(conn, out Conn value))
		{
			return;
		}
		byte[] bytes = Encoding.UTF8.GetBytes((reason.Length > 200) ? reason.Substring(0, 200) : reason);
		byte[] array = new byte[11 + bytes.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(array, (uint)(7 + bytes.Length));
		array[4] = byte.MaxValue;
		BinaryPrimitives.WriteInt32LittleEndian(array.AsSpan(5), code);
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(9), (ushort)bytes.Length);
		bytes.CopyTo(array.AsSpan(11));
		if (!value.Connected)
		{
			Dispose(value.Socket);
			return;
		}
		if (!linger)
		{
			for (int i = 0; i < value.Out.Length; i++)
			{
				value.Out[i].Clear();
			}
			if (value.Current != null && value.CurrentOffset == 0)
			{
				value.Current = null;
			}
		}
		value.Out[1].Enqueue(array);
		value.PendingBytes += array.Length;
		value.Lingering = true;
		value.LingerDeadlineMs = _clock() + (linger ? LingerTimeoutMs : 200);
		_lingering.Add(value);
		ServiceLingering(_clock());
	}

	private void ServiceLingering(long now)
	{
		for (int num = _lingering.Count - 1; num >= 0; num--)
		{
			Conn conn = _lingering[num];
			if (!Flush(conn, null) || (conn.Current == null && conn.Out[0].Count == 0 && conn.Out[1].Count == 0 && conn.Out[2].Count == 0) || now > conn.LingerDeadlineMs)
			{
				try
				{
					conn.Socket.Shutdown(SocketShutdown.Both);
				}
				catch (Exception)
				{
				}
				Dispose(conn.Socket);
				_lingering.RemoveAt(num);
			}
		}
	}

	public IEnumerable<(uint Conn, string Endpoint, bool Connected)> Connections()
	{
		foreach (Conn value in _conns.Values)
		{
			yield return (Conn: value.Id, Endpoint: value.Endpoint, Connected: value.Connected);
		}
	}

	public string QuickStatus(uint conn)
	{
		if (!_conns.TryGetValue(conn, out Conn value))
		{
			return "no such connection";
		}
		return $"route=TCP {value.Endpoint} pending={value.PendingBytes} B queued ctl/state/bulk={value.Out[0].Count}/{value.Out[2].Count}/{value.Out[1].Count}";
	}

	private static void Dispose(Socket s)
	{
		try
		{
			s.Dispose();
		}
		catch (Exception)
		{
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		foreach (uint item in new List<uint>(_conns.Keys))
		{
			Close(item, 1004, "shutting down", linger: true);
		}
		long num = _clock() + 500;
		while (_lingering.Count > 0 && _clock() < num)
		{
			ServiceLingering(_clock());
			Thread.Sleep(1);
		}
		ServiceLingering(long.MaxValue);
		_disposed = true;
		if (_listener != null)
		{
			Dispose(_listener);
		}
	}
}
