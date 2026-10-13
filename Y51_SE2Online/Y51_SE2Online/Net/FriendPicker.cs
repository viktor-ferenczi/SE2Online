using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Y51_SE2Online.Net;

public static class FriendPicker
{
	public readonly record struct Choice(FriendHost? Friend, IReadOnlyList<FriendHost> Hosting, string Reason);

	public static Choice Pick(IEnumerable<FriendHost> friends, string? nameFilter, ushort mySyncVersion)
	{
		List<FriendHost> list = friends.Where((FriendHost friendHost) => friendHost.Hosting).OrderBy((FriendHost friendHost) => friendHost.Name, StringComparer.OrdinalIgnoreCase).ThenBy((FriendHost friendHost) => friendHost.SteamId)
			.ToList();
		if (list.Count == 0)
		{
			return new Choice(null, list, "no friend is hosting SE2 multiplayer right now");
		}
		List<FriendHost> list2 = list;
		if (!string.IsNullOrWhiteSpace(nameFilter))
		{
			string f = nameFilter.Trim();
			list2 = list.Where((FriendHost h) => h.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || h.SteamId.ToString(CultureInfo.InvariantCulture) == f).ToList();
			if (list2.Count == 0)
			{
				return new Choice(null, list, $"{list.Count} friend(s) hosting, none matches '{f}'");
			}
		}
		List<FriendHost> list3 = list2.Where((FriendHost h) => h.Presence.SyncVersion == mySyncVersion).ToList();
		if (list3.Count > 0)
		{
			bool flag = !string.IsNullOrWhiteSpace(nameFilter);
			string reason;
			if (list3.Count > 1)
			{
				reason = $"first of {list3.Count} compatible hosts{(flag ? (" matching '" + nameFilter.Trim() + "'") : "")} (pick one with -y51online:join:friend:<name>)";
			}
			else if (list2.Count > 1)
			{
				reason = $"the only one{(flag ? (" matching '" + nameFilter.Trim() + "'") : "")} with sync v{mySyncVersion}";
			}
			else
			{
				reason = (flag ? ("the only friend hosting that matches '" + nameFilter.Trim() + "'") : "the only friend hosting");
			}
			return new Choice(list3[0], list, reason);
		}
		return new Choice(list2[0], list, $"no host runs sync v{mySyncVersion}; trying {list2[0].Name} (sync v{list2[0].Presence.SyncVersion}) anyway, expect a version rejection");
	}
}
