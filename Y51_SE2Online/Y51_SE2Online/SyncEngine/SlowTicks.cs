using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

internal static class SlowTicks
{
	public static volatile bool Enabled;

	public const double ThresholdMs = 50.0;

	internal static IMpLog? Log;

	public static long Ticks;

	public static long Slow;

	public static long Logged;

	public static double WorstMs;

	private static long[] _time = new long[1024];

	private static int[] _count = new int[1024];

	private static readonly List<int> Touched = new();

	private static long _start;

	private static long _lastLogMs;

	private static bool _inTick;

	private static int _commands;

	public static string? Note;

	public static long LoadingTicks;

	public static void Begin()
	{
		if (!Enabled)
		{
			return;
		}
		foreach (int stub in Touched)
		{
			_time[stub] = 0L;
			_count[stub] = 0;
		}
		Touched.Clear();
		_commands = 0;
		Note = null;
		_inTick = true;
		_start = Stopwatch.GetTimestamp();
	}

	public static void Record(int stub, long elapsed)
	{
		if (_inTick && stub >= 0)
		{
			if (stub >= _time.Length)
			{
				Array.Resize(ref _time, stub + 256);
				Array.Resize(ref _count, stub + 256);
			}
			if (_count[stub] == 0)
			{
				Touched.Add(stub);
			}
			_time[stub] += elapsed;
			_count[stub]++;
			_commands++;
		}
	}

	public static void End()
	{
		if (!_inTick)
		{
			return;
		}
		_inTick = false;
		Ticks++;
		double tickMs = Ms(Stopwatch.GetTimestamp() - _start);
		if (tickMs < ThresholdMs)
		{
			return;
		}
		if (Note != null)
		{
			LoadingTicks++;
		}
		else
		{
			Slow++;
			WorstMs = Math.Max(WorstMs, tickMs);
		}
		long nowMs = MpClock.NowMs();
		if (Logged < 20 || nowMs - _lastLogMs >= 1000)
		{
			_lastLogMs = nowMs;
			Logged++;
			Log?.Info(Describe(tickMs));
		}
	}

	internal static string Describe(double tickMs)
	{
		long replayTicks = 0L;
		foreach (int stub in Touched)
		{
			replayTicks += _time[stub];
		}
		IEnumerable<string> slowest = from s in Touched.OrderByDescending(s => _time[s]).Take(5)
			select $"{Name(s)} x{_count[s]} {Ms(_time[s]):0.0} ms";
		if (Note != null)
		{
			return $"client: long tick {tickMs:0} ms: {Note} (before the player has control; replay {Ms(replayTicks):0} ms in {_commands} commands)";
		}
		return $"client: SLOW TICK {tickMs:0} ms (replay {Ms(replayTicks):0} ms in {_commands} commands; the rest is the game's own tick): {string.Join(", ", slowest)}";
	}

	private static string Name(int stub)
	{
		CommandLayout layout = CommandLayouts.ByStub(stub);
		if (layout == null)
		{
			return $"stub {stub}";
		}
		return ClientUpstream.Short(layout.Name);
	}

	private static double Ms(long ticks) => (double)ticks * 1000.0 / Stopwatch.Frequency;

	public static string Describe() => $"slow client ticks (>{ThresholdMs:0} ms) {Slow} of {Ticks}, worst {WorstMs:0} ms (+{LoadingTicks} long world-part builds while joining)";
}
