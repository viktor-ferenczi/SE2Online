using System.Collections.Generic;

namespace Y51_SE2Online.Probe;

public sealed class DirectionTotals
{
	public long Buffers;

	public long InlineBytes;

	public long Commands;

	public long ObjectRefs;

	public long DataPayloadBytes;

	public long MaxTickBytes;

	public readonly List<int> TickBytes = new List<int>();
}
