using System;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Sessions;

namespace Y51_SE2Online.Probe;

internal interface IS2CTap
{
	void OnPostData(Entity? source, int typeId, ReadOnlySpan<byte> data, ReplicationCommandBuffer cb);

	void OnRemoveData(Entity? source, int typeId);

	void OnInvoke(Delegate callback);

	void OnSignal(ReplicationCommandBuffer cb, int stub, ReadOnlySpan<byte> payload);
}
