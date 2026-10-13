using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

public sealed class TransformSmoother
{
	private sealed class Track
	{
		public readonly List<(long T, byte[] V)> Samples = new List<(long, byte[])>();

		public long LastArrivalMs;

		public bool Active = true;

		public bool FinalApplied;

		public bool WasExtrapolating;

		public long LastOutMs;

		public Vec3D LastRaw, LastShown, Velocity, Offset;

		public bool HasOut;
	}

	public const int TransformBytes = 40;

	private readonly object _sync = new object();

	private readonly Dictionary<uint, Track> _tracks = new Dictionary<uint, Track>();

	private readonly HashSet<uint> _raw = new HashSet<uint>();

	private long _hostHigh = -1L;

	private long _offset = long.MinValue;

	private long _offsetSetMs;

	private readonly WindowMin _offsetWindow = new();

	private double _playOffset = double.NaN;

	private double _curDelay = -1.0;

	private double _targetDelay = -1.0;

	private long _lastSampleMs = long.MinValue;

	private long _lastLateArrivalMs = long.MinValue;

	private long _lastTargetMs = long.MinValue;

	private readonly int[] _late = new int[512];

	private int _lateHead;

	private int _lateCount;

	public double MaxDelaySeenMs;

	public long Samples;

	public long Interpolated;

	public long Extrapolated;

	public long Held;

	public long Late;

	public long Dropped;

	public int DelayMs { get; set; } = 60;

	public bool Adaptive { get; set; } = true;

	public int MaxDelayMs { get; set; } = 250;

	public int MarginMs { get; set; } = 12;

	public double CurrentDelayMs
	{
		get
		{
			if (!(_curDelay < 0.0))
			{
				return _curDelay;
			}
			return DelayMs;
		}
	}

	public double TargetDelayMs
	{
		get
		{
			if (!(_targetDelay < 0.0))
			{
				return _targetDelay;
			}
			return DelayMs;
		}
	}

	// Covers late packets only. A longer gap is a server stall: the host's simulation paused and resumes where it
	// stopped, so coasting through it put bodies ahead and they were pulled back; holding matches what happened.
	public int MaxExtrapolateMs { get; set; } = 100;

	public int IdleMs { get; set; } = 1000;

	public bool Enabled => DelayMs > 0;

	public int ActiveCount
	{
		get
		{
			lock (_sync)
			{
				int num = 0;
				foreach (Track value in _tracks.Values)
				{
					num += (value.Active ? 1 : 0);
				}
				return num;
			}
		}
	}

	public void SetRaw(uint netId, bool raw)
	{
		lock (_sync)
		{
			if (raw)
			{
				_raw.Add(netId);
				_tracks.Remove(netId);
			}
			else
			{
				_raw.Remove(netId);
			}
		}
	}

	public double SpeedOf(uint netId)
	{
		lock (_sync)
		{
			if (!_tracks.TryGetValue(netId, out Track value) || !value.Active || value.Samples.Count < 2)
			{
				return 0.0;
			}
			List<(long T, byte[] V)> samples = value.Samples;
			(long T, byte[] V) tuple = samples[samples.Count - 1];
			long item = tuple.T;
			byte[] item2 = tuple.V;
			List<(long T, byte[] V)> samples2 = value.Samples;
			var (num, array) = samples2[samples2.Count - 2];
			if (item <= num || item - num > 1000)
			{
				return 0.0;
			}
			double num2 = 0.0;
			for (int i = 0; i < 3; i++)
			{
				double num3 = BinaryPrimitives.ReadDoubleLittleEndian(array.AsSpan(i * 8));
				double num4 = BinaryPrimitives.ReadDoubleLittleEndian(item2.AsSpan(i * 8));
				num2 += (num4 - num3) * (num4 - num3);
			}
			return Math.Sqrt(num2) * 1000.0 / (double)(item - num);
		}
	}

	public double TurnRateOf(uint netId)
	{
		lock (_sync)
		{
			if (!_tracks.TryGetValue(netId, out Track value) || !value.Active || value.Samples.Count < 2)
			{
				return 0.0;
			}
			var (t1, b) = value.Samples[value.Samples.Count - 1];
			var (t0, a) = value.Samples[value.Samples.Count - 2];
			if (t1 <= t0 || t1 - t0 > 1000)
			{
				return 0.0;
			}
			return (double)LookMath.AngleDeg(LookMath.ReadTransform(a).O, LookMath.ReadTransform(b).O) * 1000.0 / (double)(t1 - t0);
		}
	}

	public bool IsRaw(uint netId)
	{
		lock (_sync)
		{
			return _raw.Contains(netId);
		}
	}

	public bool Add(uint netId, uint hostMs, byte[] value, long localNowMs)
	{
		if (value.Length != 40)
		{
			return false;
		}
		lock (_sync)
		{
			if (_raw.Contains(netId))
			{
				return false;
			}
			long t = Unwrap(hostMs);
			long num = NoteClockLocked(t, localNowMs);
			if (localNowMs != _lastLateArrivalMs)
			{
				_lastLateArrivalMs = localNowMs;
				_late[_lateHead] = (int)Math.Clamp(num - _offset, 0L, 5000L);
				_lateHead = (_lateHead + 1) % _late.Length;
				if (_lateCount < _late.Length)
				{
					_lateCount++;
				}
			}
			Samples++;
			if (!_tracks.TryGetValue(netId, out Track value2))
			{
				value2 = (_tracks[netId] = new Track());
			}
			value2.LastArrivalMs = localNowMs;
			value2.Active = true;
			value2.FinalApplied = false;
			List<(long, byte[])> samples = value2.Samples;
			if (samples.Count > 0)
			{
				if (t <= samples[samples.Count - 1].Item1)
				{
					if (t == samples[samples.Count - 1].Item1)
					{
						samples[samples.Count - 1] = (t, value);
						return true;
					}
					if (t < samples[0].Item1)
					{
						Dropped++;
						return false;
					}
					Late++;
					int index = samples.FindIndex(((long T, byte[] V) s) => s.T >= t);
					if (samples[index].Item1 == t)
					{
						samples[index] = (t, value);
					}
					else
					{
						samples.Insert(index, (t, value));
					}
					return true;
				}
			}
			samples.Add((t, value));
			if (samples.Count > 16)
			{
				samples.RemoveRange(0, samples.Count - 16);
			}
			return true;
		}
	}

	/// <summary>Feeds the host clock estimate from a transform that is not smoothed (the own character).</summary>
	public void NoteClock(uint hostMs, long localNowMs)
	{
		lock (_sync)
		{
			NoteClockLocked(Unwrap(hostMs), localNowMs);
		}
	}

	/// <summary>The local time a host stamp maps to (its arrival with the least delay seen), or null before any stamp.</summary>
	public double? LocalOf(uint hostMs)
	{
		lock (_sync)
		{
			// The slewed playout offset where there is one: the raw estimate steps 2 ms at a time (speed x 2 ms jumps).
			return (_offset == long.MinValue) ? null : (Unwrap(hostMs) + (double.IsNaN(_playOffset) ? _offset : _playOffset));
		}
	}

	private long NoteClockLocked(long t, long localNowMs)
	{
		long num = localNowMs - t;
		_offset = _offsetWindow.Add(localNowMs, num);
		return num;
	}

	private long Unwrap(uint hostMs)
	{
		if (_hostHigh < 0)
		{
			_hostHigh = hostMs;
			return _hostHigh;
		}
		int num = (int)hostMs - (int)_hostHigh;
		long num2 = _hostHigh + num;
		if (num2 > _hostHigh)
		{
			_hostHigh = num2;
		}
		return num2;
	}

	public void Remove(uint netId)
	{
		lock (_sync)
		{
			_tracks.Remove(netId);
		}
	}

	public void Sample(long localNowMs, List<(uint NetId, byte[] Value)> output)
	{
		lock (_sync)
		{
			if (_offset == long.MinValue)
			{
				return;
			}
			UpdateDelay(localNowMs);
			// Fractional: rounding the playout time to whole milliseconds jittered fast bodies by speed x 0.5 ms.
			double num = localNowMs - (_playOffset + CurrentDelayMs);
			foreach (var (item, track2) in _tracks)
			{
				if (!track2.Active || track2.Samples.Count == 0)
				{
					continue;
				}
				List<(long, byte[])> samples = track2.Samples;
				(long, byte[]) tuple = samples[samples.Count - 1];
				if ((double)(num - tuple.Item1) > CurrentDelayMs + (double)MaxExtrapolateMs + (double)IdleMs)
				{
					if (!track2.FinalApplied)
					{
						output.Add((item, tuple.Item2));
						track2.FinalApplied = true;
					}
					track2.Active = false;
				}
				else if (num <= samples[0].Item1)
				{
					output.Add((item, samples[0].Item2));
					Held++;
				}
				else if (num >= tuple.Item1)
				{
					double num3 = num - tuple.Item1;
					if (samples.Count >= 2 && num3 <= MaxExtrapolateMs)
					{
						(long, byte[]) tuple2 = samples[samples.Count - 2];
						double num4 = tuple.Item1 - tuple2.Item1;
						if (num4 > 0.0 && num4 <= 250.0)
						{
							output.Add((item, Settle(track2, localNowMs, Lerp(tuple2.Item2, tuple.Item2, 1.0 + (double)num3 / num4, 1.0 + (double)MaxExtrapolateMs / num4), extrapolated: true)));
							Extrapolated++;
							continue;
						}
					}
					output.Add((item, Settle(track2, localNowMs, tuple.Item2, extrapolated: false)));
					Held++;
				}
				else
				{
					int i;
					for (i = 1; i < samples.Count && samples[i].Item1 < num; i++)
					{
					}
					(long, byte[]) tuple3 = samples[i - 1];
					(long, byte[]) tuple4 = samples[i];
					double t = ((tuple4.Item1 == tuple3.Item1) ? 1.0 : ((double)(num - tuple3.Item1) / (double)(tuple4.Item1 - tuple3.Item1)));
					output.Add((item, Settle(track2, localNowMs, Lerp(tuple3.Item2, tuple4.Item2, t), extrapolated: false)));
					Interpolated++;
					if (i - 1 > 0)
					{
						samples.RemoveRange(0, i - 1);
					}
				}
			}
		}
	}

	private void UpdateDelay(long now)
	{
		double num = ((_lastSampleMs == long.MinValue) ? 0 : Math.Clamp(now - _lastSampleMs, 0L, 100L));
		_lastSampleMs = now;
		// Offset estimate steps +-2 ms; stepping playout with it jerks every remote body by speed x step, so slew instead.
		double drift = _offset - _playOffset;
		_playOffset = (double.IsNaN(_playOffset) || Math.Abs(drift) > 250.0) ? _offset : (_playOffset + Math.Clamp(drift, -0.02 * num, 0.02 * num));
		if (!Adaptive)
		{
			_curDelay = (_targetDelay = DelayMs);
			return;
		}
		if (_curDelay < 0.0)
		{
			_curDelay = DelayMs;
		}
		if (_lastTargetMs == long.MinValue || now - _lastTargetMs >= 250)
		{
			_lastTargetMs = now;
			if (_lateCount >= 30)
			{
				int[] array = new int[_lateCount];
				Array.Copy(_late, array, _lateCount);
				Array.Sort(array);
				int num2 = array[Math.Min(array.Length - 1, (int)((double)array.Length * 0.97))];
				_targetDelay = Math.Clamp(num2 + MarginMs, DelayMs, Math.Max(DelayMs, MaxDelayMs));
			}
			else
			{
				_targetDelay = DelayMs;
			}
		}
		double num3 = _targetDelay - _curDelay;
		_curDelay += ((num3 > 0.0) ? Math.Min(num3, 0.06 * num) : Math.Max(num3, -0.03 * num));
		if (_curDelay > MaxDelaySeenMs)
		{
			MaxDelaySeenMs = _curDelay;
		}
	}

	/// <summary>
	/// Extrapolation guesses past the newest sample; when the next sample lands the true path is somewhere else and
	/// the body snapped there (ships rubberbanded, and whoever stood on or piloted them with it). The gap is carried
	/// as an offset that decays over about 100 ms instead. Gaps over 5 m are real teleports and are shown at once.
	/// </summary>
	private static byte[] Settle(Track track, long now, byte[] raw, bool extrapolated)
	{
		var position = new Vec3D(BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(0)), BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(8)), BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(16)));
		double dt = track.HasOut ? Math.Clamp(now - track.LastOutMs, 0L, 100L) / 1000.0 : 0.0;
		track.Offset *= Math.Exp(-dt * 10.0);
		if (track.HasOut && dt > 0.0)
		{
			if (track.WasExtrapolating && !extrapolated)
			{
				Vec3D jump = track.LastShown + track.Velocity * dt - position;
				track.Offset = jump.Length > 5.0 ? default : jump;
			}
			else
			{
				track.Velocity = (position - track.LastRaw) * (1.0 / dt);
			}
		}
		if (track.Offset.Length < 1e-4) track.Offset = default;
		track.WasExtrapolating = extrapolated;
		track.LastRaw = position;
		track.LastShown = position + track.Offset;
		track.LastOutMs = now;
		track.HasOut = true;
		if (track.Offset == default) return raw;
		byte[] shown = (byte[])raw.Clone();
		BinaryPrimitives.WriteDoubleLittleEndian(shown.AsSpan(0), track.LastShown.X);
		BinaryPrimitives.WriteDoubleLittleEndian(shown.AsSpan(8), track.LastShown.Y);
		BinaryPrimitives.WriteDoubleLittleEndian(shown.AsSpan(16), track.LastShown.Z);
		return shown;
	}

	public static byte[] Lerp(byte[] a, byte[] b, double t)
	{
		return Lerp(a, b, t, 2.0);
	}

	public static byte[] Lerp(byte[] a, byte[] b, double t, double maxT)
	{
		t = Math.Clamp(t, 0.0, Math.Max(1.0, maxT));
		byte[] array = new byte[40];
		for (int i = 0; i < 3; i++)
		{
			double num = BinaryPrimitives.ReadDoubleLittleEndian(a.AsSpan(i * 8));
			double num2 = BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(i * 8));
			BinaryPrimitives.WriteDoubleLittleEndian(array.AsSpan(i * 8), num + (num2 - num) * t);
		}
		Span<float> span = stackalloc float[4];
		Span<float> span2 = stackalloc float[4];
		Span<float> span3 = stackalloc float[4];
		float num3 = 0f;
		for (int j = 0; j < 4; j++)
		{
			span[j] = BinaryPrimitives.ReadSingleLittleEndian(a.AsSpan(24 + j * 4));
			span2[j] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(24 + j * 4));
			num3 += span[j] * span2[j];
		}
		float num4 = ((!(num3 < 0f)) ? 1 : (-1));
		float num5 = (float)Math.Min(t, 1.5);
		double num6 = 0.0;
		for (int k = 0; k < 4; k++)
		{
			span3[k] = span[k] + (num4 * span2[k] - span[k]) * num5;
			num6 += (double)span3[k] * (double)span3[k];
		}
		num6 = Math.Sqrt(num6);
		for (int l = 0; l < 4; l++)
		{
			BinaryPrimitives.WriteSingleLittleEndian(array.AsSpan(24 + l * 4), (num6 > 1E-09) ? ((float)((double)span3[l] / num6)) : span2[l]);
		}
		return array;
	}

	public string Describe()
	{
		return $"smoothing {((!Enabled) ? "off" : (Adaptive ? $"{CurrentDelayMs:0} ms (adaptive {DelayMs}-{MaxDelayMs}, target {TargetDelayMs:0}, max {MaxDelaySeenMs:0})" : $"{DelayMs} ms"))}: {ActiveCount} moving, samples {Samples}, interpolated {Interpolated}, extrapolated {Extrapolated}, held {Held}, late {Late}, too old (dropped) {Dropped}";
	}
}
