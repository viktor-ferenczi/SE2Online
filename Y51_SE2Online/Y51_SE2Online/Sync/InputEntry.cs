namespace Y51_SE2Online.Sync;

public readonly record struct InputEntry(uint NetId, ushort Key, byte[] Bytes);
