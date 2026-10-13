namespace Y51_SE2Online.Sync;

public enum SpawnKind : byte { Medbay, FactionMedbay, SharedMedbay, Suit, Ship, NearHost }

public sealed record SpawnOption(SpawnKind Kind, string Name, string Detail, bool Available);

/// <summary>Where a joiner without a character can spawn; the host sends it, the client answers with an index.</summary>
public sealed record SpawnMenu(SpawnOption[] Options, bool Respawn, string Message = "");
