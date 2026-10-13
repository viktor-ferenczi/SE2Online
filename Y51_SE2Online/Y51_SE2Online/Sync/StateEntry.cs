namespace Y51_SE2Online.Sync;

public readonly record struct StateEntry(uint NetId, ushort Key, uint Version, byte[] Bytes, uint HostMs = 0u)
{
	public int WireSize => 16 + Bytes.Length;

	public const int Overhead = 16;
}
