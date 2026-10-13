using System;
using System.Collections.Generic;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class PlayerNamesPublisher
{
	private readonly ITransport _t;

	private readonly IMpLog _log;

	private readonly PlayerRoster _roster;

	private readonly Func<PeerId, bool> _isSynced;

	private readonly Dictionary<PeerId, uint> _sent = new Dictionary<PeerId, uint>();

	private uint _loggedVersion;

	public long Messages;

	public PlayerNamesPublisher(ITransport transport, IMpLog log, PlayerRoster roster, Func<PeerId, bool> isSynced)
	{
		_t = transport;
		_log = log;
		_roster = roster;
		_isSynced = isSynced;
		_t.PeerDisconnected += (PeerId p, DisconnectInfo _) =>
		{
			_sent.Remove(p);
		};
	}

	public void Tick()
	{
		var (num, tags) = _roster.Snapshot();
		if (num == 0)
		{
			return;
		}
		byte[] array = null;
		foreach (PeerId peer in _t.Peers)
		{
			if ((!_sent.TryGetValue(peer, out var value) || value != num) && _isSynced(peer))
			{
				if (array == null)
				{
					array = PlayerNamesMessage.Encode(num, tags);
				}
				_t.Send(peer, Channel.Control, array);
				_sent[peer] = num;
				Messages++;
			}
		}
		if (array != null && _loggedVersion != num)
		{
			_loggedVersion = num;
			_log.Info("nametags(host): player list " + _roster.Describe() + " sent to the joined players");
		}
	}
}
