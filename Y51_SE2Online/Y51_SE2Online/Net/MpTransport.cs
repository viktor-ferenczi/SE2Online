using System.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Net;

public sealed class MpTransport : ITransport, IDisposable
{
	private sealed class BulkOut
	{
		public uint TransferId;

		public ReadOnlyMemory<byte> Data;

		public uint Count;

		public uint Next;
	}

	private sealed class PeerState
	{
		public uint Conn;

		public ulong InstanceId;

		public PeerId Id;

		public bool Ready;

		public bool Closed;

		public long LinkConnectedMs;

		public readonly uint[] TxSeq = new uint[3];

		public readonly ReliableSequenceChecker ControlRx = new ReliableSequenceChecker();

		public readonly ReliableSequenceChecker BulkRx = new ReliableSequenceChecker();

		public readonly ReceiveWindow StateRx = new ReceiveWindow();

		public readonly AckTracker StateAcks = new AckTracker();

		public Reassembler Reassembler;

		public readonly Queue<BulkOut> Bulk = new Queue<BulkOut>();

		public long BulkQueuedBytes;

		public uint NextTransferId = 1u;

		public List<byte[]>? Early;

		public bool StateAckDirty;

		public long LastStateSentMs;

		public long LastPingSentMs;

		public uint NextPingId = 1u;

		public double RttSum;

		public readonly PeerStats Stats = new PeerStats();
	}

	private readonly struct OutRequest
	{
		public readonly PeerId Peer;

		public readonly Channel Channel;

		public readonly ReadOnlyMemory<byte> Payload;

		public readonly ulong Token;

		public readonly int DisconnectCode;

		public readonly string? DisconnectReason;

		public bool IsDisconnect => DisconnectReason != null;

		public OutRequest(PeerId peer, Channel channel, ReadOnlyMemory<byte> payload, ulong token)
		{
			Peer = peer;
			Channel = channel;
			Payload = payload;
			Token = token;
			DisconnectCode = 0;
			DisconnectReason = null;
		}

		public OutRequest(PeerId peer, int code, string reason)
		{
			Peer = peer;
			Channel = Channel.Control;
			Payload = default;
			Token = 0uL;
			DisconnectCode = code;
			DisconnectReason = reason;
		}
	}

	private readonly IPacketLink _link;

	private readonly TransportOptions _o;

	private readonly IMpLog _log;

	private readonly Func<long> _clock;

	private readonly Dictionary<uint, PeerState> _byConn = new Dictionary<uint, PeerState>();

	private readonly Dictionary<PeerId, PeerState> _byPeer = new Dictionary<PeerId, PeerState>();

	private readonly ConcurrentQueue<OutRequest> _out = new ConcurrentQueue<OutRequest>();

	private readonly List<LinkEvent> _events = new List<LinkEvent>();

	private readonly List<LinkMessage> _messages = new List<LinkMessage>();

	private readonly List<ulong> _scratchTokens = new List<ulong>();

	private PeerId[] _peers = Array.Empty<PeerId>();

	private long _nextToken;

	private bool _disposed;

	public IReadOnlyList<PeerId> Peers => Volatile.Read(in _peers);

	public IMpLog Log => _log;

	public event Action<PeerId>? PeerConnected;

	public event Action<PeerId, DisconnectInfo>? PeerDisconnected;

	public event Action<PeerId, Channel, byte[]>? MessageReceived;

	public event Action<PeerId, IReadOnlyList<ulong>>? StateAcked;

	public event Action<PeerId, IReadOnlyList<ulong>>? StateLost;

	public MpTransport(IPacketLink link, TransportOptions options, IMpLog log, Func<long>? clock = null)
	{
		_link = link;
		_o = options;
		_log = log;
		_clock = clock ?? new Func<long>(MpClock.NowMs);
		int num = 29 + _o.FragmentPayload;
		if (num > link.MaxMessageSize)
		{
			throw new ArgumentException($"fragment frame {num} B exceeds link limit {link.MaxMessageSize} B");
		}
	}

	public ulong Send(PeerId peer, Channel channel, ReadOnlyMemory<byte> payload)
	{
		int num = MaxDirectPayload(channel);
		if (channel != Channel.Bulk && payload.Length > num)
		{
			_log.WarnOnce($"oversize-{channel}", $"{channel} message of {payload.Length} B exceeds {num} B; use Channel.Bulk");
			return 0uL;
		}
		if (channel == Channel.Bulk && payload.Length > _o.MaxBulkMessageBytes)
		{
			_log.WarnOnce("oversize-bulk", $"bulk message of {payload.Length} B exceeds {_o.MaxBulkMessageBytes} B");
			return 0uL;
		}
		ulong num2 = (ulong)Interlocked.Increment(ref _nextToken);
		_out.Enqueue(new OutRequest(peer, channel, payload, num2));
		return num2;
	}

	public void Disconnect(PeerId peer, int code, string reason)
	{
		_out.Enqueue(new OutRequest(peer, code, reason));
	}

	public int MaxDirectPayload(Channel channel)
	{
		int num = _link.MaxMessageSize - 9;
		if (channel != Channel.State)
		{
			return num;
		}
		return num - 8;
	}

	public bool TryGetStats(PeerId peer, out PeerStats stats)
	{
		if (_byPeer.TryGetValue(peer, out PeerState value))
		{
			FillStats(value);
			stats = value.Stats.Clone();
			return true;
		}
		stats = null;
		return false;
	}

	public void Poll()
	{
		if (_disposed)
		{
			return;
		}
		long now = _clock();
		_events.Clear();
		_messages.Clear();
		_link.Pump(_events, _messages);
		foreach (LinkEvent @event in _events)
		{
			LinkEvent e = @event;
			if (e.Kind == LinkEventKind.Connected)
			{
				OnLinkEvent(in e, now);
			}
		}
		foreach (LinkMessage message in _messages)
		{
			OnLinkMessage(message, now);
		}
		foreach (LinkEvent event2 in _events)
		{
			LinkEvent e2 = event2;
			if (e2.Kind != LinkEventKind.Connected)
			{
				OnLinkEvent(in e2, now);
			}
		}
		OutRequest result;
		while (_out.TryDequeue(out result))
		{
			Dispatch(in result, now);
		}
		if (_byConn.Count == 0)
		{
			return;
		}
		PeerState[] array = ToArray();
		foreach (PeerState peerState in array)
		{
			if (!peerState.Closed)
			{
				Service(peerState, now);
			}
		}
	}

	private PeerState[] ToArray()
	{
		PeerState[] array = new PeerState[_byConn.Count];
		_byConn.Values.CopyTo(array, 0);
		return array;
	}

	private void Service(PeerState p, long now)
	{
		if (!p.Ready)
		{
			if (now - p.LinkConnectedMs > _o.HelloTimeoutMs)
			{
				Close(p, 1002, $"no Hello within {_o.HelloTimeoutMs} ms", byPeer: false, "timeout", notifyPeer: true);
			}
			return;
		}
		if (p.StateAckDirty && now - p.LastStateSentMs >= _o.StateAckDelayMs)
		{
			SendStateFrame(p, FrameKind.Ack, default, 0uL, now);
		}
		if (now - p.LastPingSentMs >= _o.PingIntervalMs)
		{
			p.LastPingSentMs = now;
			WireWriter wireWriter = new WireWriter(16).U32(p.NextPingId++).I64(_clock());
			SendFrame(p, Channel.Control, FrameKind.Ping, wireWriter.ToArray(), reliable: true);
			p.Stats.PingsSent++;
		}
		long num = ((p.Stats.LastPongMs > 0) ? p.Stats.LastPongMs : p.Stats.ConnectedMs);
		if (now - num > _o.PingTimeoutMs)
		{
			Close(p, 1003, $"no Pong for {_o.PingTimeoutMs} ms", byPeer: false, "timeout", notifyPeer: true);
			return;
		}
		_scratchTokens.Clear();
		p.StateAcks.Expire(now, _o.StateAckTimeoutMs, null, _scratchTokens);
		if (_scratchTokens.Count > 0)
		{
			Raise(StateLost, p.Id, _scratchTokens.ToArray());
		}
		if (p.Reassembler.Expire(now) > 0)
		{
			_log.Warn($"peer {p.Id}: {p.Reassembler.LastError}");
		}
		PumpBulk(p);
	}

	private void PumpBulk(PeerState p)
	{
		while (p.Bulk.Count > 0 && _link.GetPendingReliableBytes(p.Conn) < (_link.HasLanePriorities(p.Conn) ? _o.BulkHighWaterLanesBytes : _o.BulkHighWaterBytes))
		{
			BulkOut bulkOut = p.Bulk.Peek();
			ReadOnlyMemory<byte> readOnlyMemory = Fragmenter.Slice(bulkOut.Data, bulkOut.Next, (uint)_o.FragmentPayload);
			FragmentHeader fragmentHeader = new FragmentHeader(bulkOut.TransferId, bulkOut.Next, bulkOut.Count, (uint)bulkOut.Data.Length, (uint)_o.FragmentPayload);
			byte[] array = new byte[29 + readOnlyMemory.Length];
			uint num = p.TxSeq[1] + 1;
			Frame.Write(array, new FrameHeader(Channel.Bulk, FrameKind.Fragment, num, 0));
			fragmentHeader.Write(array.AsSpan(9));
			readOnlyMemory.Span.CopyTo(array.AsSpan(29));
			switch (_link.Send(p.Conn, Channel.Bulk, reliable: true, array))
			{
			case LinkSendResult.Busy:
				p.Stats.SendBusy++;
				return;
			case LinkSendResult.Failed:
				return;
			}
			p.TxSeq[1] = num;
			ref ChannelStats reference = ref p.Stats.Channels[1];
			reference.FramesSent++;
			reference.BytesSent += array.Length;
			if (++bulkOut.Next == bulkOut.Count)
			{
				p.Bulk.Dequeue();
				p.BulkQueuedBytes -= bulkOut.Data.Length;
				reference.MessagesSent++;
			}
		}
	}

	private void Dispatch(in OutRequest req, long now)
	{
		if (!_byPeer.TryGetValue(req.Peer, out PeerState value) || value.Closed)
		{
			if (!req.IsDisconnect)
			{
				_log.WarnOnce($"send-unknown-{req.Peer}", $"dropping {req.Channel} message for unknown/closed peer {req.Peer}");
			}
			return;
		}
		if (req.IsDisconnect)
		{
			Close(value, req.DisconnectCode, req.DisconnectReason, byPeer: false, "local", notifyPeer: true);
			return;
		}
		if (!value.Ready)
		{
			_log.WarnOnce($"send-notready-{req.Peer}", $"dropping {req.Channel} message for peer {req.Peer}: handshake not finished");
			return;
		}
		switch (req.Channel)
		{
		case Channel.Control:
			SendFrame(value, Channel.Control, FrameKind.Data, req.Payload.Span, reliable: true);
			value.Stats.Channels[0].MessagesSent++;
			break;
		case Channel.State:
			SendStateFrame(value, FrameKind.Data, req.Payload.Span, req.Token, now);
			value.Stats.Channels[2].MessagesSent++;
			break;
		case Channel.Bulk:
			value.Bulk.Enqueue(new BulkOut
			{
				TransferId = value.NextTransferId++,
				Data = req.Payload,
				Count = Fragmenter.CountFor((uint)req.Payload.Length, (uint)_o.FragmentPayload)
			});
			value.BulkQueuedBytes += req.Payload.Length;
			break;
		}
	}

	private void SendStateFrame(PeerState p, FrameKind kind, ReadOnlySpan<byte> payload, ulong token, long now)
	{
		byte[] array = new byte[8 + payload.Length];
		StatePrefix.Write(array, p.StateRx.Latest, p.StateRx.Bits);
		payload.CopyTo(array.AsSpan(8));
		uint num = SendFrame(p, Channel.State, kind, array, reliable: false);
		if (kind == FrameKind.Data && token != 0L && num != 0)
		{
			p.StateAcks.Register(num, token, now);
		}
		p.StateAckDirty = false;
		p.LastStateSentMs = now;
	}

	private uint SendFrame(PeerState p, Channel ch, FrameKind kind, ReadOnlySpan<byte> payload, bool reliable)
	{
		uint num = p.TxSeq[(uint)ch] + 1;
		byte[] array = new byte[9 + payload.Length];
		Frame.Write(array, new FrameHeader(ch, kind, num, 0));
		payload.CopyTo(array.AsSpan(9));
		LinkSendResult linkSendResult = _link.Send(p.Conn, ch, reliable, array);
		if (linkSendResult != LinkSendResult.Ok)
		{
			if (linkSendResult == LinkSendResult.Busy)
			{
				p.Stats.SendBusy++;
			}
			_log.WarnOnce($"send-{linkSendResult}-{p.Id}-{ch}", $"peer {p.Id}: link refused {ch}/{kind} frame ({linkSendResult})");
			return 0u;
		}
		p.TxSeq[(uint)ch] = num;
		ref ChannelStats reference = ref p.Stats.Channels[(uint)ch];
		reference.FramesSent++;
		reference.BytesSent += array.Length;
		return num;
	}

	private void OnLinkEvent(in LinkEvent e, long now)
	{
		PeerState value2;
		if (e.Kind == LinkEventKind.Connected)
		{
			if (!_byConn.ContainsKey(e.Conn))
			{
				PeerId peerId = new PeerId(e.RemoteId);
				if (_byPeer.TryGetValue(peerId, out PeerState value))
				{
					_log.Warn($"peer {peerId} reconnected on a new connection; dropping the old one");
					Close(value, 1000, "replaced by new connection", byPeer: false, "local", notifyPeer: false);
				}
				PeerState peerState = new PeerState
				{
					Conn = e.Conn,
					Id = peerId,
					LinkConnectedMs = now,
					Reassembler = new Reassembler(_o.MaxBulkMessageBytes, 8, 536870912L)
				};
				peerState.Stats.Peer = peerId;
				peerState.Stats.ConnectedMs = now;
				_byConn[e.Conn] = peerState;
				_byPeer[peerId] = peerState;
				WireWriter wireWriter = new WireWriter(128).U16(2).U8(_o.Role).U64(_o.LocalId)
					.Str(_o.PluginVersion)
					.Str(_o.GameVersion)
					.Str(_o.LocalName)
					.U64(_o.InstanceId);
				SendFrame(peerState, Channel.Control, FrameKind.Hello, wireWriter.ToArray(), reliable: true);
				_log.Info($"link up to {peerId} (conn {e.Conn}), Hello sent");
			}
		}
		else if (_byConn.TryGetValue(e.Conn, out value2))
		{
			Close(value2, e.Code, e.Reason, e.ByPeer, "link", notifyPeer: false);
		}
	}

	private void OnLinkMessage(in LinkMessage m, long now)
	{
		if (!_byConn.TryGetValue(m.Conn, out PeerState value) || value.Closed)
		{
			return;
		}
		if (!Frame.TryRead(m.Data, out FrameHeader header, out ReadOnlySpan<byte> payload, out string error))
		{
			ProtocolError(value, error);
		}
		else if (!value.Ready && header.Kind != FrameKind.Hello && header.Kind != FrameKind.Bye)
		{
			PeerState peerState = value;
			if (peerState.Early == null)
			{
				peerState.Early = new List<byte[]>();
			}
			if (value.Early.Count >= _o.MaxEarlyFrames)
			{
				ProtocolError(value, $"more than {_o.MaxEarlyFrames} frames before Hello");
			}
			else
			{
				value.Early.Add(m.Data);
			}
		}
		else
		{
			HandleFrame(value, in header, payload, m.Data.Length, now);
		}
	}

	private void HandleFrame(PeerState p, in FrameHeader h, ReadOnlySpan<byte> payload, int frameBytes, long now)
	{
		ref ChannelStats reference = ref p.Stats.Channels[(uint)h.Channel];
		reference.FramesReceived++;
		reference.BytesReceived += frameBytes;
		if (h.Channel == Channel.State)
		{
			HandleState(p, in h, payload, now);
			return;
		}
		ReliableCheck reliableCheck = ((h.Channel == Channel.Control) ? p.ControlRx : p.BulkRx).Observe(h.Seq);
		if (reliableCheck != ReliableCheck.Ok)
		{
			ProtocolError(p, $"{h.Channel} sequence {reliableCheck} at #{h.Seq}");
			if (reliableCheck == ReliableCheck.Duplicate)
			{
				return;
			}
		}
		switch (h.Kind)
		{
		case FrameKind.Hello:
			HandleHello(p, payload);
			break;
		case FrameKind.Ping:
		{
			WireWriter wireWriter = new WireWriter(payload.Length);
			wireWriter.Bytes(payload);
			SendFrame(p, Channel.Control, FrameKind.Pong, wireWriter.ToArray(), reliable: true);
			break;
		}
		case FrameKind.Pong:
		{
			if (payload.Length < 12)
			{
				ProtocolError(p, "short Pong");
				break;
			}
			WireReader wireReader = new WireReader(payload);
			wireReader.U32();
			long num = wireReader.I64();
			double num2 = Math.Max(0L, _clock() - num);
			PeerStats stats = p.Stats;
			stats.PongsReceived++;
			stats.LastPongMs = now;
			stats.LastRttMs = num2;
			stats.MinRttMs = Math.Min(stats.MinRttMs, num2);
			stats.MaxRttMs = Math.Max(stats.MaxRttMs, num2);
			p.RttSum += num2;
			stats.AvgRttMs = p.RttSum / (double)stats.PongsReceived;
			break;
		}
		case FrameKind.Bye:
		{
			int code = 1000;
			string reason = "bye";
			if (payload.Length >= 6)
			{
				WireReader wireReader2 = new WireReader(payload);
				code = wireReader2.I32();
				reason = wireReader2.Str();
			}
			Close(p, code, reason, byPeer: true, "bye", notifyPeer: false);
			break;
		}
		case FrameKind.Data:
			reference.MessagesReceived++;
			Raise(p.Id, h.Channel, payload.ToArray());
			break;
		case FrameKind.Fragment:
		{
			if (h.Channel != Channel.Bulk || !FragmentHeader.TryRead(payload, out var h2, out var payload2))
			{
				ProtocolError(p, "malformed fragment");
				break;
			}
			byte[] complete;
			switch (p.Reassembler.Add(in h2, payload2, now, out complete))
			{
			case ReassemblyResult.Rejected:
				ProtocolError(p, "fragment rejected: " + p.Reassembler.LastError);
				break;
			case ReassemblyResult.Complete:
				reference.MessagesReceived++;
				Raise(p.Id, Channel.Bulk, complete);
				break;
			}
			break;
		}
		default:
			ProtocolError(p, $"unexpected {h.Kind} on {h.Channel}");
			break;
		}
	}

	private void HandleState(PeerState p, in FrameHeader h, ReadOnlySpan<byte> payload, long now)
	{
		if (!StatePrefix.TryRead(payload, out var ackSeq, out var ackBits, out var rest))
		{
			ProtocolError(p, "short State frame");
			return;
		}
		if (ackSeq != 0 || ackBits != 0)
		{
			_scratchTokens.Clear();
			p.StateAcks.ProcessAck(ackSeq, ackBits, _scratchTokens);
			if (_scratchTokens.Count > 0)
			{
				Raise(StateAcked, p.Id, _scratchTokens.ToArray());
			}
			_scratchTokens.Clear();
			p.StateAcks.Expire(now, _o.StateAckTimeoutMs, ackSeq, _scratchTokens);
			if (_scratchTokens.Count > 0)
			{
				Raise(StateLost, p.Id, _scratchTokens.ToArray());
			}
		}
		if (h.Kind == FrameKind.Ack)
		{
			return;
		}
		if (h.Kind != FrameKind.Data)
		{
			ProtocolError(p, $"unexpected {h.Kind} on State");
			return;
		}
		ReceiveResult receiveResult = p.StateRx.Observe(h.Seq);
		if (receiveResult != ReceiveResult.Duplicate && receiveResult != ReceiveResult.TooOld)
		{
			p.StateAckDirty = true;
			p.Stats.Channels[2].MessagesReceived++;
			Raise(p.Id, Channel.State, rest.ToArray());
		}
	}

	private void HandleHello(PeerState p, ReadOnlySpan<byte> payload)
	{
		if (p.Ready)
		{
			ProtocolError(p, "second Hello");
			return;
		}
		ushort num;
		byte b;
		ulong num2;
		string text;
		string text2;
		string text3;
		try
		{
			WireReader wireReader = new WireReader(payload);
			num = wireReader.U16();
			b = wireReader.U8();
			num2 = wireReader.U64();
			text = wireReader.Str();
			text2 = wireReader.Str();
			text3 = wireReader.Str();
			p.InstanceId = wireReader.Remaining >= 8 ? wireReader.U64() : 0;
		}
		catch (ArgumentOutOfRangeException)
		{
			ProtocolError(p, "malformed Hello");
			return;
		}
		if (num != 2)
		{
			string text4 = $"version mismatch: peer runs Y51_SE2Online {text} (protocol {num}), this PC runs {_o.PluginVersion} (protocol {2}); " + "both players need the same plugin build";
			_log.Error($"peer {p.Id} '{text3}': {text4}");
			Close(p, 1007, text4, byPeer: false, "local", notifyPeer: true);
			return;
		}
		if (p.InstanceId != 0)
		{
			foreach (PeerState other in _byConn.Values.ToList())
			{
				if (other != p && !other.Closed && other.InstanceId == p.InstanceId && PeerAlias.SteamOf(other.Id.Value) == PeerAlias.SteamOf(p.Id.Value))
				{
					_log.Warn($"peer {other.Id} reconnected from the same game instance as {p.Id}; dropping the old connection");
					Close(other, 1000, "replaced by new connection", byPeer: false, "local", notifyPeer: false);
				}
			}
		}
		if (num2 != 0L && num2 != PeerAlias.SteamOf(p.Id.Value) && _link.AuthenticatesPeers)
		{
			_log.Warn($"peer {p.Id}: Hello claims id {num2}");
		}
		p.Stats.RemoteSteamId = num2;
		if (b == _o.Role)
		{
			_log.Warn($"peer {p.Id}: both sides are {((b == 0) ? "host" : "client")}");
		}
		if (text != _o.PluginVersion)
		{
			_log.Warn($"peer {p.Id}: plugin version {text} differs from ours ({_o.PluginVersion})");
		}
		if (text2 != _o.GameVersion)
		{
			_log.Warn($"peer {p.Id}: game version {text2} differs from ours ({_o.GameVersion})");
		}
		p.Ready = true;
		p.Stats.Ready = true;
		p.Stats.RemoteName = text3;
		p.Stats.RemotePluginVersion = text;
		p.Stats.RemoteGameVersion = text2;
		RefreshPeers();
		_log.Info($"handshake OK with {p.Id} '{text3}' ({((b == 0) ? "host" : "client")}, plugin {text}, game {text2})");
		try
		{
			PeerConnected?.Invoke(p.Id);
		}
		catch (Exception value)
		{
			_log.Error($"PeerConnected handler threw: {value}");
		}
		if (p.Early == null)
		{
			return;
		}
		List<byte[]>? early = p.Early;
		p.Early = null;
		long now = _clock();
		foreach (byte[] item in early)
		{
			if (p.Closed)
			{
				break;
			}
			if (Frame.TryRead(item, out FrameHeader header, out ReadOnlySpan<byte> payload2, out string _))
			{
				HandleFrame(p, in header, payload2, item.Length, now);
			}
		}
	}

	private void ProtocolError(PeerState p, string error)
	{
		p.Stats.ProtocolErrors++;
		_log.Warn($"peer {p.Id}: protocol error: {error}");
		if (p.Stats.ProtocolErrors >= _o.MaxProtocolErrors && !p.Closed)
		{
			Close(p, 1001, "too many protocol errors (last: " + error + ")", byPeer: false, "local", notifyPeer: true);
		}
	}

	private void Close(PeerState p, int code, string reason, bool byPeer, string source, bool notifyPeer)
	{
		if (p.Closed)
		{
			return;
		}
		p.Closed = true;
		if (notifyPeer)
		{
			WireWriter wireWriter = new WireWriter(32 + reason.Length).I32(code).Str(reason);
			SendFrame(p, Channel.Control, FrameKind.Bye, wireWriter.ToArray(), reliable: true);
		}
		if (source != "link")
		{
			_link.Close(p.Conn, code, reason, notifyPeer);
		}
		_byConn.Remove(p.Conn);
		if (_byPeer.TryGetValue(p.Id, out PeerState value) && value == p)
		{
			_byPeer.Remove(p.Id);
		}
		bool ready = p.Ready;
		RefreshPeers();
		DisconnectInfo disconnectInfo = new DisconnectInfo(code, reason, byPeer, source);
		_log.Info($"peer {p.Id} disconnected: {disconnectInfo}");
		if (!ready)
		{
			return;
		}
		try
		{
			PeerDisconnected?.Invoke(p.Id, disconnectInfo);
		}
		catch (Exception value2)
		{
			_log.Error($"PeerDisconnected handler threw: {value2}");
		}
	}

	private void RefreshPeers()
	{
		List<PeerId> list = new List<PeerId>();
		foreach (PeerState value in _byPeer.Values)
		{
			if (value.Ready && !value.Closed)
			{
				list.Add(value.Id);
			}
		}
		Volatile.Write(ref _peers, list.ToArray());
	}

	private void FillStats(PeerState p)
	{
		PeerStats stats = p.Stats;
		stats.BulkQueuedMessages = p.Bulk.Count;
		stats.BulkQueuedBytes = p.BulkQueuedBytes;
		stats.ReassemblyInProgress = p.Reassembler.InProgress;
		stats.SeqGaps = p.ControlRx.Gaps + p.BulkRx.Gaps;
		stats.SeqDuplicates = p.ControlRx.Duplicates + p.BulkRx.Duplicates;
		stats.StateLate = p.StateRx.Late;
		stats.StateMissing = p.StateRx.Missing;
		stats.StateDuplicates = p.StateRx.Duplicates;
		stats.StateAcked = p.StateAcks.Acked;
		stats.StateLost = p.StateAcks.Failed;
		stats.StateInFlight = p.StateAcks.InFlight;
	}

	public bool TryGetIncomingBulkProgress(PeerId peer, uint transferId, out int received, out int count)
	{
		received = (count = 0);
		long startedMs;
		if (_byPeer.TryGetValue(peer, out PeerState value))
		{
			return value.Reassembler.TryGetProgress(transferId, out received, out count, out startedMs);
		}
		return false;
	}

	private void Raise(PeerId peer, Channel ch, byte[] data)
	{
		try
		{
			MessageReceived?.Invoke(peer, ch, data);
		}
		catch (Exception value)
		{
			_log.Error($"MessageReceived handler threw: {value}");
		}
	}

	private void Raise(Action<PeerId, IReadOnlyList<ulong>>? ev, PeerId peer, ulong[] tokens)
	{
		try
		{
			ev?.Invoke(peer, tokens);
		}
		catch (Exception value)
		{
			_log.Error($"State ack handler threw: {value}");
		}
	}

	public void DisconnectAll(int code, string reason)
	{
		PeerState[] array = ToArray();
		foreach (PeerState p in array)
		{
			Close(p, code, reason, byPeer: false, "local", notifyPeer: true);
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			DisconnectAll(1004, "shutting down");
			_disposed = true;
		}
	}
}
