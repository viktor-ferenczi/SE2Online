using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

public sealed class PlayerRoster
{
	private readonly object _sync = new object();

	private List<PlayerTag> _tags = new List<PlayerTag>();

	private uint _version;

	public uint Version
	{
		get
		{
			lock (_sync)
			{
				return _version;
			}
		}
	}

	public bool Set(IEnumerable<PlayerTag> tags)
	{
		List<PlayerTag> list = (from t in tags
			where t.NetId != 0
			orderby t.ClientId, t.NetId
			select t).ToList();
		lock (_sync)
		{
			if (list.SequenceEqual(_tags))
			{
				return false;
			}
			_tags = list;
			_version++;
			return true;
		}
	}

	public (uint Version, IReadOnlyList<PlayerTag> Tags) Snapshot()
	{
		lock (_sync)
		{
			return (Version: _version, Tags: _tags);
		}
	}

	public string Describe()
	{
		var (value, tags) = Snapshot();
		return $"v{value}: {PlayerNamesMessage.Describe(tags)}";
	}
}
