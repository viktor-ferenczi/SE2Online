namespace Y51_SE2Online.Sync;

/// <summary>
/// Smallest value seen in the last <see cref="WindowMs"/>: the host-clock offset from the quickest recent packet.
/// An all-time minimum with a slow upward creep (2 ms a second) took minutes to recover after a server hitch or a
/// slower simulation shifted every arrival, and the whole time remote bodies played out against the wrong clock.
/// </summary>
public sealed class WindowMin
{
	private readonly (long At, long Value)[] _ring = new (long, long)[256];

	private int _head, _count;

	public long WindowMs { get; init; } = 2000;

	public long Add(long nowMs, long value)
	{
		_ring[_head] = (nowMs, value);
		_head = (_head + 1) % _ring.Length;
		if (_count < _ring.Length) _count++;
		long min = value;
		for (int i = 0; i < _count; i++)
		{
			var (at, v) = _ring[(_head - 1 - i + _ring.Length) % _ring.Length];
			if (nowMs - at > WindowMs) break;
			if (v < min) min = v;
		}
		return min;
	}
}
