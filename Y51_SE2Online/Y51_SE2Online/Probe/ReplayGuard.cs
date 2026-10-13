using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Probe;

internal static class ReplayGuard
{
	public const int Marker = 1580056798;

	public static IMpLog? Log;

	public static long Guarded;

	public static long Caught;

	public static readonly ConcurrentDictionary<string, long> CaughtBy = new ConcurrentDictionary<string, long>();

	[ThreadStatic]
	private static List<(int Offset, Segment Seg)>? _scratch;

	public unsafe static void Invoke(delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void> orig, object context, ref CommandBufferState st, nint callback, ReplicationCommandBuffer cb, int stub)
	{
		nint dataOffset = st.DataOffset;
		Interlocked.Increment(ref Guarded);
		try
		{
			orig(context, ref st, callback, cb);
		}
		catch (Exception e)
		{
			Recover(ref st, dataOffset, stub, e);
		}
	}

	private unsafe static void Recover(ref CommandBufferState st, nint start, int stub, Exception e)
	{
		Interlocked.Increment(ref Caught);
		string full = $"stub {stub}";
		int num = -1;
		try
		{
			CommandLayout commandLayout = CommandLayouts.ByStub(stub);
			if (commandLayout != null)
			{
				full = commandLayout.Name;
				num = commandLayout.Frame(new ReadOnlySpan<byte>(st.DataPtr + start, (int)Math.Min(int.MaxValue, st.ActiveBytes - start)), _scratch ?? (_scratch = new List<(int, Segment)>()));
			}
		}
		catch (Exception)
		{
			num = -1;
		}
		st.DataOffset = ((num >= 0 && start + num <= st.ActiveBytes) ? (start + num) : st.ActiveBytes);
		string text = ClientUpstream.Short(full);
		CaughtBy.AddOrUpdate(text, 1L, (string _, long c) => c + 1);
		Log?.WarnOnce("replay-guard-" + text, $"world sync: the game's handler for {text} threw {e.GetType().Name}: {e.Message} -- that command was skipped " + ((num >= 0) ? "(the replay continues)" : "(with the rest of its buffer)") + "; game unaffected. Further ones are counted in the status line.");
	}

	public static string Describe()
	{
		return $"replay guard: {Guarded} injected signals replayed, handler exceptions caught {Caught}" + (CaughtBy.IsEmpty ? "" : (" (" + string.Join(", ", from kv in CaughtBy.OrderByDescending((KeyValuePair<string, long> kv) => kv.Value).Take(6)
			select $"{kv.Key} x{kv.Value}") + ")"));
	}
}
