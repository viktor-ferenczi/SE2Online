using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Spike;

public sealed class SpikeDriver
{
	private enum AppKind : byte
	{
		BlobStart = 1,
		Blob,
		BlobAck
	}

	public sealed class PeerProgress
	{
		public long ConnectedMs;

		public bool BlobSent;

		public uint BlobId;

		public long BlobSentMs;

		public int BlobLength;

		public bool? TheyVerifiedOurs;

		public bool? WeVerifiedTheirs;

		public long TheirStartMs = -1L;

		public long TheirBlobMs = -1L;

		public long LastPingLogMs;

		public long PongsAtLastLog;

		public bool ResultLogged;

		public string? Result;
	}

	private const int ShaLen = 32;

	private readonly ITransport _t;

	private readonly IMpLog _log;

	private readonly Func<long> _clock;

	private readonly int _blobBytes;

	private readonly ulong _seed;

	private readonly int _blobDelayMs;

	private readonly Dictionary<PeerId, PeerProgress> _peers = new Dictionary<PeerId, PeerProgress>();

	private uint _nextBlobId = 1u;

	public bool LogPings { get; set; } = true;

	public IReadOnlyDictionary<PeerId, PeerProgress> Progress => _peers;

	public SpikeDriver(ITransport transport, IMpLog log, Func<long> clock, int blobBytes, ulong seed, int blobDelayMs = 2000)
	{
		_t = transport;
		_log = log;
		_clock = clock;
		_blobBytes = blobBytes;
		_seed = ((seed == 0L) ? 11400714819323198485uL : seed);
		_blobDelayMs = blobDelayMs;
		_t.PeerConnected += OnConnected;
		_t.PeerDisconnected += OnDisconnected;
		_t.MessageReceived += OnMessage;
	}

	private void OnConnected(PeerId peer)
	{
		_peers[peer] = new PeerProgress
		{
			ConnectedMs = _clock(),
			LastPingLogMs = _clock()
		};
		_log.Info((_blobBytes > 0) ? $"spike: peer {peer} connected; ping every 1 s, {MpLogExtensions.Bytes(_blobBytes)} blob in {(double)_blobDelayMs / 1000.0:0.#} s" : $"spike: peer {peer} connected; ping every 1 s, blob test disabled");
	}

	private void OnDisconnected(PeerId peer, DisconnectInfo info)
	{
		if (_peers.Remove(peer, out PeerProgress value) && !value.ResultLogged && _blobBytes > 0)
		{
			_log.Warn($"SPIKE RESULT: INCOMPLETE peer={peer} (disconnected before both blobs were verified: {info})");
		}
	}

	public void Tick()
	{
		long num = _clock();
		foreach (KeyValuePair<PeerId, PeerProgress> peer in _peers)
		{
			PeerId key = peer.Key;
			PeerProgress value = peer.Value;
			if (_blobBytes > 0 && !value.BlobSent && num - value.ConnectedMs >= _blobDelayMs)
			{
				SendBlob(key, value, num);
			}
			if (LogPings && num - value.LastPingLogMs >= 1000 && _t.TryGetStats(key, out PeerStats stats))
			{
				value.LastPingLogMs = num;
				if (stats.PongsReceived > value.PongsAtLastLog)
				{
					_log.Info($"ping {key}: rtt={stats.LastRttMs:0} ms (min {stats.MinRttMs:0} / avg {stats.AvgRttMs:0.0} / max {stats.MaxRttMs:0}, n={stats.PongsReceived})");
				}
				else
				{
					_log.Info($"ping {key}: no pong for {(double)(num - ((stats.LastPongMs > 0) ? stats.LastPongMs : stats.ConnectedMs)) / 1000.0:0.0} s (sent {stats.PingsSent})");
				}
				value.PongsAtLastLog = stats.PongsReceived;
			}
		}
	}

	private void SendBlob(PeerId peer, PeerProgress p, long now)
	{
		p.BlobSent = true;
		p.BlobId = _nextBlobId++;
		p.BlobLength = _blobBytes;
		byte[] array = new byte[37 + _blobBytes];
		Span<byte> span = array.AsSpan(37);
		Fill(span, _seed ^ p.BlobId);
		byte[] array2 = SHA256.HashData(span);
		array[0] = 2;
		BitConverter.TryWriteBytes(array.AsSpan(1), p.BlobId);
		array2.CopyTo(array.AsSpan(5));
		WireWriter wireWriter = new WireWriter().U8(1).U32(p.BlobId).I32(_blobBytes)
			.Bytes(array2);
		_t.Send(peer, Channel.Control, wireWriter.ToArray());
		_t.Send(peer, Channel.Bulk, array);
		p.BlobSentMs = now;
		_log.Info($"blob #{p.BlobId} -> {peer}: {MpLogExtensions.Bytes(_blobBytes)} queued on Bulk, sha256={MpLogExtensions.Hex(array2)}");
	}

	private void OnMessage(PeerId peer, Channel ch, byte[] data)
	{
		if (data.Length == 0 || !_peers.TryGetValue(peer, out PeerProgress value))
		{
			return;
		}
		long num = _clock();
		try
		{
			switch ((AppKind)data[0])
			{
			case AppKind.BlobStart:
				if (ch == Channel.Control)
				{
					WireReader wireReader2 = new WireReader(data.AsSpan(1));
					uint value3 = wireReader2.U32();
					int num5 = wireReader2.I32();
					value.TheirStartMs = num;
					_log.Info($"blob #{value3} <- {peer}: incoming {MpLogExtensions.Bytes(num5)}, sha256={MpLogExtensions.Hex(wireReader2.Bytes(32))}");
				}
				break;
			case AppKind.Blob:
				if (ch == Channel.Bulk)
				{
					uint num6 = BitConverter.ToUInt32(data, 1);
					Span<byte> span = data.AsSpan(5, 32);
					Span<byte> span2 = data.AsSpan(37);
					byte[] array = SHA256.HashData(span2);
					bool flag2 = span.SequenceEqual(array);
					value.WeVerifiedTheirs = flag2;
					value.TheirBlobMs = ((value.TheirStartMs >= 0) ? (num - value.TheirStartMs) : (-1));
					string value4 = ((value.TheirBlobMs > 0) ? $" in {(double)value.TheirBlobMs / 1000.0:0.00} s ({(double)span2.Length / 1024.0 / ((double)value.TheirBlobMs / 1000.0):0} KiB/s)" : "");
					string message2 = $"blob #{num6} <- {peer}: {MpLogExtensions.Bytes(span2.Length)}{value4}, sha256 {(flag2 ? "OK" : "MISMATCH")} ({MpLogExtensions.Hex(array)})";
					if (flag2)
					{
						_log.Info(message2);
					}
					else
					{
						_log.Error(message2);
					}
					WireWriter wireWriter = new WireWriter(32).U8(3).U32(num6).U8(flag2 ? ((byte)1) : ((byte)0))
						.I32(span2.Length)
						.I64(value.TheirBlobMs);
					_t.Send(peer, Channel.Control, wireWriter.ToArray());
					CheckDone(peer, value);
				}
				break;
			case AppKind.BlobAck:
				if (ch == Channel.Control)
				{
					WireReader wireReader = new WireReader(data.AsSpan(1));
					uint value2 = wireReader.U32();
					bool flag = wireReader.U8() == 1;
					int num2 = wireReader.I32();
					long num3 = wireReader.I64();
					value.TheyVerifiedOurs = flag;
					long num4 = num - value.BlobSentMs;
					string message = $"blob #{value2} -> {peer}: peer verified {MpLogExtensions.Bytes(num2)} {(flag ? "OK" : "MISMATCH")}; queue-to-ack {(double)num4 / 1000.0:0.00} s" + ((num3 > 0) ? $", their receive time {(double)num3 / 1000.0:0.00} s" : "");
					if (flag)
					{
						_log.Info(message);
					}
					else
					{
						_log.Error(message);
					}
					CheckDone(peer, value);
				}
				break;
			}
		}
		catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException)
		{
			_log.Warn($"spike: malformed {ch} message from {peer} ({data.Length} B)");
		}
	}

	private void CheckDone(PeerId peer, PeerProgress p)
	{
		if (!p.ResultLogged && p.TheyVerifiedOurs.HasValue && p.WeVerifiedTheirs.HasValue)
		{
			p.ResultLogged = true;
			_t.TryGetStats(peer, out PeerStats stats);
			bool flag = p.TheyVerifiedOurs.Value && p.WeVerifiedTheirs.Value;
			p.Result = (flag ? "PASS" : "FAIL");
			string message = $"SPIKE RESULT: {p.Result} peer={peer} blob out={(p.TheyVerifiedOurs.Value ? "OK" : "BAD")} in={(p.WeVerifiedTheirs.Value ? "OK" : "BAD")}" + ((stats != null) ? $" rtt avg={stats.AvgRttMs:0.0} ms min={stats.MinRttMs:0} max={stats.MaxRttMs:0} pongs={stats.PongsReceived} seqGaps={stats.SeqGaps} protoErrors={stats.ProtocolErrors}" : "");
			if (flag)
			{
				_log.Info(message);
			}
			else
			{
				_log.Error(message);
			}
		}
	}

	public static void Fill(Span<byte> dst, ulong seed)
	{
		ulong num = ((seed == 0L) ? 2685821657736338717uL : seed);
		int num2 = 0;
		while (num2 < dst.Length)
		{
			num ^= num >> 12;
			num ^= num << 25;
			num ^= num >> 27;
			ulong num3 = num * 2685821657736338717L;
			int num4 = 0;
			while (num4 < 8 && num2 < dst.Length)
			{
				dst[num2] = (byte)(num3 >> 8 * num4);
				num4++;
				num2++;
			}
		}
	}
}
