using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

public sealed class AudioRouting
{
	public const long JoinerBuildWindowMs = 2000L;

	public const long OwnBuildWindowMs = 4000L;

	private readonly object _sync = new object();

	private readonly Queue<(ushort ClientId, long Ms)> _joinerBuilds = new Queue<(ushort, long)>();

	private readonly Queue<long> _ownBuilds = new Queue<long>();

	public long HostMuted;

	public long HostPlayed;

	public long JoinerMuted;

	public long JoinerPlayed;

	public long CharacterMuted;

	public static AudioRouting Instance { get; } = new AudioRouting();

	public static bool IsBuildStory(string? storyName)
	{
		return storyName?.StartsWith("BlockPlacerNetworkStories.", StringComparison.Ordinal) ?? false;
	}

	public static bool IsBuildSound(string shortName)
	{
		if (shortName == "IBlockPlacement+OnBlocksAddedAudioSignal" || shortName == "IBlockPlacement+OnBlocksRemovedAudioSignal")
		{
			return true;
		}
		return false;
	}

	public static bool IsCharacterSound(string shortName)
	{
		if (shortName == "CharacterMovementComponent+OnMagneticBootStateChangedSignal" || shortName == "CharacterMovementComponent+OnCharacterPhysicsStateChangedSignal")
		{
			return true;
		}
		return false;
	}

	public void NoteJoinerBuild(ushort clientId, long nowMs)
	{
		lock (_sync)
		{
			_joinerBuilds.Enqueue((clientId, nowMs));
			while (_joinerBuilds.Count > 64)
			{
				_joinerBuilds.Dequeue();
			}
		}
	}

	public ushort TakeJoinerBuild(long nowMs)
	{
		lock (_sync)
		{
			while (_joinerBuilds.Count > 0)
			{
				var (result, num) = _joinerBuilds.Dequeue();
				if (nowMs - num <= 2000)
				{
					HostMuted++;
					return result;
				}
			}
			HostPlayed++;
			return 0;
		}
	}

	public void NoteOwnBuild(long nowMs)
	{
		lock (_sync)
		{
			_ownBuilds.Enqueue(nowMs);
			while (_ownBuilds.Count > 64)
			{
				_ownBuilds.Dequeue();
			}
		}
	}

	public bool TakeOwnBuild(long nowMs)
	{
		lock (_sync)
		{
			while (_ownBuilds.Count > 0)
			{
				long num = _ownBuilds.Dequeue();
				if (nowMs - num <= 4000)
				{
					JoinerPlayed++;
					return true;
				}
			}
			JoinerMuted++;
			return false;
		}
	}

	public void Reset()
	{
		lock (_sync)
		{
			_joinerBuilds.Clear();
			_ownBuilds.Clear();
		}
	}

	public string Describe()
	{
		return $"audio routing: build sounds host played {HostPlayed} / left to joiners {HostMuted}, joiner played own {JoinerPlayed} / muted others' {JoinerMuted}; other characters' 2D sounds muted {CharacterMuted}";
	}
}
