namespace Y51_SE2Online.Sync;

public readonly record struct HostSettings(bool Identities, GridPermissionMode Grids, ResearchMode Research)
{
	public byte Pack()
	{
		return (byte)((Identities ? 1u : 0u) | (uint)((Grids == GridPermissionMode.Strict) ? 2 : 0) | (uint)((Research == ResearchMode.Own) ? 4 : 0));
	}

	public static HostSettings Unpack(byte b)
	{
		return new HostSettings((b & 1) != 0, ((b & 2) != 0) ? GridPermissionMode.Strict : GridPermissionMode.Team, ((b & 4) != 0) ? ResearchMode.Own : ResearchMode.Shared);
	}

	public override string ToString()
	{
		return $"identities {(Identities ? "per player" : "shared (off)")}, grids {((Grids == GridPermissionMode.Team) ? "team" : "strict")}, research {((Research == ResearchMode.Shared) ? "shared" : "own")}";
	}
}
