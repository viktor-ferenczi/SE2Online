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

	private readonly Dictionary<PeerId, PeerProgress> _peers = new();

	private uint _nextBlobId = 1u;

	public bool LogPings { get; set; } = true;

	public IReadOnlyDictionary<PeerId, PeerProgress> Progress => _peers;

	public SpikeDriver(ITransport transport, IMpLog log, Func<long> clock, int blobBytes, ulong seed, int blobDelayMs = 2000)
	{
		_t = transport;
		_log = log;
		_clock = clock;
		_blobBytes = blobBytes;
		_seed = seed == 0 ? 11400714819323198485uL : seed;
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
		_log.Info(_blobBytes > 0
			? $"spike: peer {peer} connected; ping every 1 s, {MpLogExtensions.Bytes(_blobBytes)} blob in {_blobDelayMs / 1000.0:0.#} s"
			: $"spike: peer {peer} connected; ping every 1 s, blob test disabled");
	}

	private void OnDisconnected(PeerId peer, DisconnectInfo info)
	{
		if (_peers.Remove(peer, out PeerProgress progress) && !progress.ResultLogged && _blobBytes > 0)
		{
			_log.Warn($"SPIKE RESULT: INCOMPLETE peer={peer} (disconnected before both blobs were verified: {info})");
		}
	}

	public void Tick()
	{
		long now = _clock();
		foreach (var entry in _peers)
		{
			PeerId peer = entry.Key;
			PeerProgress p = entry.Value;
			if (_blobBytes > 0 && !p.BlobSent && now - p.ConnectedMs >= _blobDelayMs)
			{
				SendBlob(peer, p, now);
			}
			if (LogPings && now - p.LastPingLogMs >= 1000 && _t.TryGetStats(peer, out PeerStats stats))
			{
				p.LastPingLogMs = now;
				if (stats.PongsReceived > p.PongsAtLastLog)
				{
					_log.Info($"ping {peer}: rtt={stats.LastRttMs:0} ms (min {stats.MinRttMs:0} / avg {stats.AvgRttMs:0.0} / max {stats.MaxRttMs:0}, n={stats.PongsReceived})");
				}
				else
				{
					_log.Info($"ping {peer}: no pong for {(now - (stats.LastPongMs > 0 ? stats.LastPongMs : stats.ConnectedMs)) / 1000.0:0.0} s (sent {stats.PingsSent})");
				}
				p.PongsAtLastLog = stats.PongsReceived;
			}
		}
	}

	private void SendBlob(PeerId peer, PeerProgress p, long now)
	{
		p.BlobSent = true;
		p.BlobId = _nextBlobId++;
		p.BlobLength = _blobBytes;
		// Bulk message: kind (1) + blob id (4) + sha256 (32) + blob.
		byte[] message = new byte[37 + _blobBytes];
		Span<byte> blob = message.AsSpan(37);
		Fill(blob, _seed ^ p.BlobId);
		byte[] sha = SHA256.HashData(blob);
		message[0] = (byte)AppKind.Blob;
		BitConverter.TryWriteBytes(message.AsSpan(1), p.BlobId);
		sha.CopyTo(message.AsSpan(5));
		WireWriter start = new WireWriter().U8((byte)AppKind.BlobStart).U32(p.BlobId).I32(_blobBytes)
			.Bytes(sha);
		_t.Send(peer, Channel.Control, start.ToArray());
		_t.Send(peer, Channel.Bulk, message);
		p.BlobSentMs = now;
		_log.Info($"blob #{p.BlobId} -> {peer}: {MpLogExtensions.Bytes(_blobBytes)} queued on Bulk, sha256={MpLogExtensions.Hex(sha)}");
	}

	private void OnMessage(PeerId peer, Channel ch, byte[] data)
	{
		if (data.Length == 0 || !_peers.TryGetValue(peer, out PeerProgress p))
		{
			return;
		}
		long now = _clock();
		try
		{
			switch ((AppKind)data[0])
			{
			case AppKind.BlobStart:
				if (ch == Channel.Control)
				{
					var reader = new WireReader(data.AsSpan(1));
					uint blobId = reader.U32();
					int length = reader.I32();
					p.TheirStartMs = now;
					_log.Info($"blob #{blobId} <- {peer}: incoming {MpLogExtensions.Bytes(length)}, sha256={MpLogExtensions.Hex(reader.Bytes(32))}");
				}
				break;
			case AppKind.Blob:
				if (ch == Channel.Bulk)
				{
					uint blobId = BitConverter.ToUInt32(data, 1);
					Span<byte> expectedSha = data.AsSpan(5, 32);
					Span<byte> blob = data.AsSpan(37);
					byte[] sha = SHA256.HashData(blob);
					bool ok = expectedSha.SequenceEqual(sha);
					p.WeVerifiedTheirs = ok;
					p.TheirBlobMs = p.TheirStartMs >= 0 ? now - p.TheirStartMs : -1;
					string timing = p.TheirBlobMs > 0 ? $" in {p.TheirBlobMs / 1000.0:0.00} s ({blob.Length / 1024.0 / (p.TheirBlobMs / 1000.0):0} KiB/s)" : "";
					string message = $"blob #{blobId} <- {peer}: {MpLogExtensions.Bytes(blob.Length)}{timing}, sha256 {(ok ? "OK" : "MISMATCH")} ({MpLogExtensions.Hex(sha)})";
					if (ok)
					{
						_log.Info(message);
					}
					else
					{
						_log.Error(message);
					}
					WireWriter ack = new WireWriter(32).U8((byte)AppKind.BlobAck).U32(blobId).U8(ok ? (byte)1 : (byte)0)
						.I32(blob.Length)
						.I64(p.TheirBlobMs);
					_t.Send(peer, Channel.Control, ack.ToArray());
					CheckDone(peer, p);
				}
				break;
			case AppKind.BlobAck:
				if (ch == Channel.Control)
				{
					var reader = new WireReader(data.AsSpan(1));
					uint blobId = reader.U32();
					bool ok = reader.U8() == 1;
					int length = reader.I32();
					long theirReceiveMs = reader.I64();
					p.TheyVerifiedOurs = ok;
					long queueToAckMs = now - p.BlobSentMs;
					string message = $"blob #{blobId} -> {peer}: peer verified {MpLogExtensions.Bytes(length)} {(ok ? "OK" : "MISMATCH")}; queue-to-ack {queueToAckMs / 1000.0:0.00} s"
						+ (theirReceiveMs > 0 ? $", their receive time {theirReceiveMs / 1000.0:0.00} s" : "");
					if (ok)
					{
						_log.Info(message);
					}
					else
					{
						_log.Error(message);
					}
					CheckDone(peer, p);
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
			bool pass = p.TheyVerifiedOurs.Value && p.WeVerifiedTheirs.Value;
			p.Result = pass ? "PASS" : "FAIL";
			string message = $"SPIKE RESULT: {p.Result} peer={peer} blob out={(p.TheyVerifiedOurs.Value ? "OK" : "BAD")} in={(p.WeVerifiedTheirs.Value ? "OK" : "BAD")}"
				+ (stats != null ? $" rtt avg={stats.AvgRttMs:0.0} ms min={stats.MinRttMs:0} max={stats.MaxRttMs:0} pongs={stats.PongsReceived} seqGaps={stats.SeqGaps} protoErrors={stats.ProtocolErrors}" : "");
			if (pass)
			{
				_log.Info(message);
			}
			else
			{
				_log.Error(message);
			}
		}
	}

	/// <summary>Deterministic xorshift64* fill, so both ends can regenerate the same blob from the seed.</summary>
	public static void Fill(Span<byte> dst, ulong seed)
	{
		ulong state = seed == 0 ? 2685821657736338717uL : seed;
		int i = 0;
		while (i < dst.Length)
		{
			state ^= state >> 12;
			state ^= state << 25;
			state ^= state >> 27;
			ulong word = state * 2685821657736338717uL;
			for (int b = 0; b < 8 && i < dst.Length; b++, i++)
			{
				dst[i] = (byte)(word >> 8 * b);
			}
		}
	}
}
