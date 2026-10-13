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
		List<FriendHost> hosting = friends.Where(h => h.Hosting)
			.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(h => h.SteamId)
			.ToList();
		if (hosting.Count == 0)
		{
			return new Choice(null, hosting, "no friend is hosting SE2 multiplayer right now");
		}
		List<FriendHost> candidates = hosting;
		if (!string.IsNullOrWhiteSpace(nameFilter))
		{
			string f = nameFilter.Trim();
			candidates = hosting.Where(h => h.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || h.SteamId.ToString(CultureInfo.InvariantCulture) == f).ToList();
			if (candidates.Count == 0)
			{
				return new Choice(null, hosting, $"{hosting.Count} friend(s) hosting, none matches '{f}'");
			}
		}
		List<FriendHost> compatible = candidates.Where(h => h.Presence.SyncVersion == mySyncVersion).ToList();
		if (compatible.Count > 0)
		{
			bool filtered = !string.IsNullOrWhiteSpace(nameFilter);
			string reason;
			if (compatible.Count > 1)
			{
				reason = $"first of {compatible.Count} compatible hosts{(filtered ? " matching '" + nameFilter.Trim() + "'" : "")} (pick one with -y51online:join:friend:<name>)";
			}
			else if (candidates.Count > 1)
			{
				reason = $"the only one{(filtered ? " matching '" + nameFilter.Trim() + "'" : "")} with sync v{mySyncVersion}";
			}
			else
			{
				reason = filtered ? "the only friend hosting that matches '" + nameFilter.Trim() + "'" : "the only friend hosting";
			}
			return new Choice(compatible[0], hosting, reason);
		}
		return new Choice(candidates[0], hosting, $"no host runs sync v{mySyncVersion}; trying {candidates[0].Name} (sync v{candidates[0].Presence.SyncVersion}) anyway, expect a version rejection");
	}
}
