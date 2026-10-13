namespace Y51_SE2Online.Sync;

public readonly record struct SignalKeyDef(ushort Key, string Name, ushort PayloadBytes, string RefOffsets)
{
	public const ushort Dynamic = ushort.MaxValue;
}
