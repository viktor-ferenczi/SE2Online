using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.SecureIds;

namespace Y51_SE2Online.SyncEngine;

internal static class HarnessStories
{
	public static readonly List<string> Trace = new List<string>();

	public static Func<Entity?, string> Tag = (Entity? _) => "?";

	public static Entity? Spawned;

	public static async NetworkStory<int> RoundTrip(Entity target, int x, string label, StringId key)
	{
		ReplicationEventContext result = await MoveTo.Server(null);
		Trace.Add($"server:{Tag(target)}:{(result.IsLocal ? "local" : "remote")}:{result.ClientId.Id}:{key.String}");
		int y = x * 2;
		await MoveTo.Client();
		Trace.Add($"client:{Tag(target)}:{y}");
		return y + label.Length;
	}

	public static async NetworkStory<string> ServerOnly(Entity target, int[] values)
	{
		await MoveTo.Server(null);
		Trace.Add($"server-only:{Tag(target)}:{values.Sum()}");
		return $"sum {values.Sum()}";
	}

	public static async NetworkStory<Entity?> ServerSpawns(Entity target)
	{
		await MoveTo.Server(null);
		Trace.Add("server-spawns:" + Tag(target));
		return Spawned;
	}

	public static async NetworkStory Unsendable(EntityId id)
	{
		await MoveTo.Server(null);
		Trace.Add("unsendable ran " + id.EntityGuid);
	}
}
