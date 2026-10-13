namespace Y51_SE2Online.Probe;

public readonly struct ProbeRow(Direction direction, string category, string key, long count, long bytes)
{
	public readonly Direction Direction = direction;

	public readonly string Category = category;

	public readonly string Key = key;

	public readonly long Count = count;

	public readonly long Bytes = bytes;
}
