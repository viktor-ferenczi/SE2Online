namespace Y51_SE2Online.Sync;

public readonly record struct PlayerInfo(ushort ClientId, long Player, long Identity, string Name, bool Connected);
