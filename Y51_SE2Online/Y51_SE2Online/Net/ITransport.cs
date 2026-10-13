using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Net;

public interface ITransport : IDisposable
{
	IReadOnlyList<PeerId> Peers { get; }

	event Action<PeerId>? PeerConnected;

	event Action<PeerId, DisconnectInfo>? PeerDisconnected;

	event Action<PeerId, Channel, byte[]>? MessageReceived;

	event Action<PeerId, IReadOnlyList<ulong>>? StateAcked;

	event Action<PeerId, IReadOnlyList<ulong>>? StateLost;

	ulong Send(PeerId peer, Channel channel, ReadOnlyMemory<byte> payload);

	void Disconnect(PeerId peer, int code, string reason);

	void Poll();

	bool TryGetStats(PeerId peer, out PeerStats stats);
}
