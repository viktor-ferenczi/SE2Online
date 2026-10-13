namespace Y51_SE2Online.Sync;

public readonly record struct EntityLocator(uint NetId, int Root, int[] Path);
