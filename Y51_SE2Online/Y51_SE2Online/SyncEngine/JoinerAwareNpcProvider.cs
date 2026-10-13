using System;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.VRage.DCS.Components;

namespace Y51_SE2Online.SyncEngine;

internal sealed class JoinerAwareNpcProvider : INpcIdentityProvider
{
	public readonly INpcIdentityProvider Inner;

	private readonly Func<long, bool> _isJoiner;

	public JoinerAwareNpcProvider(INpcIdentityProvider inner, Func<long, bool> isJoiner)
	{
		Inner = inner;
		_isJoiner = isJoiner;
	}

	public bool IsNpc(IdentityId id)
	{
		if (!Inner.IsNpc(id))
		{
			return _isJoiner(id);
		}
		return true;
	}

	public bool IsNpc(Entity entity)
	{
		return Inner.IsNpc(entity);
	}

	public bool IsHostileNpc(IdentityId id)
	{
		return Inner.IsHostileNpc(id);
	}
}
