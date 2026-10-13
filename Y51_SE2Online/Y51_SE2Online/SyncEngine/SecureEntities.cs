using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.SecureIds;

namespace Y51_SE2Online.SyncEngine;

internal static class SecureEntities
{
	internal static Func<ISecureEntityIds?>? ServiceOverride;

	public static async NetworkStory<Entity?> ResolveSecureEntity(Session session, EntityId entityId)
	{
		ReplicationEventContext result = await MoveTo.Server(null);
		Session session2 = result.Session ?? session;
		return (ServiceOverride?.Invoke() ?? session2?.SessionComponents.Components.OfType<ISecureEntityIds>().FirstOrDefault())?.TryGetEntity(entityId, null, result.ClientId);
	}
}
