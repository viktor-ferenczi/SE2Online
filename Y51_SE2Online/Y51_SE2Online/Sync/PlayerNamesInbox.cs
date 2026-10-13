using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

public sealed class PlayerNamesInbox
{
	private readonly object _sync = new object();

	private IReadOnlyList<PlayerTag> _tags = Array.Empty<PlayerTag>();

	private uint _version;

	private long _changes;

	public long Changes
	{
		get
		{
			lock (_sync)
			{
				return _changes;
			}
		}
	}

	public bool Accept(uint version, IReadOnlyList<PlayerTag> tags)
	{
		lock (_sync)
		{
			if (_changes > 0 && (int)(version - _version) <= 0)
			{
				return false;
			}
			_version = version;
			_tags = tags;
			_changes++;
			return true;
		}
	}

	public List<PlayerTag> Others(ushort ownClientId)
	{
		lock (_sync)
		{
			return _tags.Where((PlayerTag t) => t.ClientId != ownClientId && t.NetId != 0).ToList();
		}
	}
}
