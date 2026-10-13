namespace Y51_SE2Online.Sync;

/// <summary>A GPS marker sent to one other player; the host adds it to that player's marker list.</summary>
public sealed record GpsShare(ushort To, string Name, double X, double Y, double Z, byte R, byte G, byte B, int Icon);
