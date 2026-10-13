using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Net;

public interface IPacketLink : IDisposable
{
	string Name { get; }

	int MaxMessageSize { get; }

	bool AuthenticatesPeers { get; }

	void Pump(List<LinkEvent> events, List<LinkMessage> messages);

	LinkSendResult Send(uint conn, Channel channel, bool reliable, ReadOnlySpan<byte> frame);

	int GetPendingReliableBytes(uint conn);

	bool HasLanePriorities(uint conn)
	{
		return false;
	}

	void Close(uint conn, int code, string reason, bool linger);
}
