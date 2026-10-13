using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Internal;
using Keen.VRage.DCS.Serialization;
using Keen.VRage.DCS.Utils;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Probe;

internal static class ReplicationProbe
{
	private delegate ref byte ReadBufferFn(CommandBufferBase cb, int index, out nint offset, out nint activeBytes);

	private enum StubKind : byte
	{
		Signal,
		PostInvoke,
		PostData,
		RemoveData
	}

	private sealed class StubInfo
	{
		public string Name;

		public string Detail;

		public StubKind Kind;
	}

	private sealed class Counter
	{
		public long Count;

		public long Bytes;

		public long Payload;
	}

	private sealed class DirAcc
	{
		public readonly DirectionTotals Totals = new DirectionTotals();

		public readonly Dictionary<int, Counter> Stubs = new Dictionary<int, Counter>();

		public readonly Dictionary<int, Counter> Data = new Dictionary<int, Counter>();

		public readonly Dictionary<int, Counter> DataRemove = new Dictionary<int, Counter>();

		public readonly Dictionary<MethodInfo, Counter> Invokes = new Dictionary<MethodInfo, Counter>();

		public readonly Dictionary<Type, Counter> Stories = new Dictionary<Type, Counter>();

		public readonly Dictionary<Type, Counter> StoryFinish = new Dictionary<Type, Counter>();

		public readonly Dictionary<Type, Counter> ObjRefs = new Dictionary<Type, Counter>();
	}

	private sealed class Window
	{
		public readonly DateTime StartUtc = DateTime.UtcNow;

		public readonly long StartMs = MpClock.NowMs();

		public long ServerTicks;

		public long ClientTicks;

		public readonly DirAcc[] Dir = new DirAcc[2]
		{
			new DirAcc(),
			new DirAcc()
		};

		public readonly BundleTotals Bundles = new BundleTotals();
	}

	private const string PostInvokeSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostInvokeInternalSignal";

	private const string PostDataSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostDataSignal";

	private const string RemoveDataSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+RemoveDataSignal";

	private static IMpLog _log = GameLog.Instance;

	private static readonly object Sync = new object();

	private static Window _window = new Window();

	private static readonly CodecSurface Surface = new CodecSurface();

	private static Timer? _timer;

	private static string? _csvPath;

	private static string? _typesPath;

	private static bool _sizeBundles;

	private static volatile bool _commitActive;

	private static volatile bool _bundleActive;

	private static volatile bool _measure;

	private static volatile bool _classify = true;

	private static volatile bool _report = true;

	private static volatile IS2CTap? _tap;

	private static bool _meterAllowed;

	private static AccessTools.FieldRef<CommandBufferBase, List<byte[]>>? _committedBuffers;

	private static AccessTools.FieldRef<CommandBufferBase.WithObjects, List<ValueTuple<object?>>>? _objects;

	private static ReadBufferFn? _readBuffer;

	private static Func<object, bool>? _isServer;

	private static Func<object, object>? _replayContext;

	private static FieldInfo? _replayTableField;

	private static Func<object, int>? _bundleCount;

	private static Func<object, object>? _bundleSkipped;

	private static Func<object, object>? _bundleStaged;

	private static Func<object, object>? _bundleServer;

	private static object? _serverComp;

	private static object? _clientComp;

	private static WeakReference<object>? _serverCtx;

	private static WeakReference<object>? _clientCtx;

	private static nint[]? _origReaders;

	private static StubInfo[] _stubs = Array.Empty<StubInfo>();

	private static readonly ConditionalWeakTable<ReplicationCommandBuffer.Stub[], object> Instrumented = new ConditionalWeakTable<ReplicationCommandBuffer.Stub[], object>();

	private static readonly ConcurrentDictionary<int, (string Name, int Size)> DataTypes = new ConcurrentDictionary<int, (string, int)>();

	private static readonly ConcurrentDictionary<Type, FieldInfo?> StoryField = new ConcurrentDictionary<Type, FieldInfo>();

	private static readonly ConcurrentDictionary<Type, string> StoryFieldsText = new ConcurrentDictionary<Type, string>();

	private static long _bundleNo;

	private static long _bundleSizingMs;

	private static long _totalServerTicks;

	private static long _measuredCommands;

	private static readonly long[] TotalBytes = new long[2];

	private static int _sizeFailures;

	public static IS2CTap? Tap
	{
		get
		{
			return _tap;
		}
		set
		{
			_tap = value;
		}
	}

	public static bool Active
	{
		get
		{
			if (!_commitActive)
			{
				return _bundleActive;
			}
			return true;
		}
	}

	public static bool MeterArmed
	{
		get
		{
			if (_meterAllowed)
			{
				return _commitActive;
			}
			return false;
		}
	}

	public unsafe static bool IsGuarded(object component)
	{
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			if (!MeterArmed || _replayTableField == null)
			{
				return false;
			}
			ReplicationCommandBuffer.Stub[] array = ((Keen.VRage.Multiplayer.Sessions.ReplicationCommandBuffer.ReplayTable?)_replayTableField.GetValue(component))?.Stubs;
			if (array == null || array.Length == 0)
			{
				return false;
			}
			delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void> obj = &MeasuringReader;
			return array[0].Wrapper.Reader == (delegate*<object, ref CommandBufferState, IntPtr, ReplicationCommandBuffer, void>)obj;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static List<(string Hook, bool Ok, string Detail)> Install(Harmony harmony, bool sizeBundles, IMpLog log, bool report = true)
	{
		_log = log;
		ReplayGuard.Log = log;
		_sizeBundles = sizeBundles & report;
		_report = report;
		_classify = report;
		List<(string, bool, string)> list = new List<(string, bool, string)>();
		list.Add(TryInstall("probe:CommitFrame", () =>
		{
			MethodInfo original = EngineMembers.Method(HookTargets.CommitFrame);
			_isServer = EngineMembers.Getter<bool>(EngineMembers.Property(R("ipr-isserver")));
			_replayContext = EngineMembers.Getter<object>(EngineMembers.Property(R("ipr-replayctx")));
			_replayTableField = EngineMembers.Field(R("ipr-replaytable"));
			_committedBuffers = AccessTools.FieldRefAccess<CommandBufferBase, List<byte[]>>(EngineMembers.Field(R("cb-buffers")));
			_objects = AccessTools.FieldRefAccess<CommandBufferBase.WithObjects, List<ValueTuple<object>>>(EngineMembers.Field(R("cb-objects")));
			_readBuffer = (ReadBufferFn)Delegate.CreateDelegate(typeof(ReadBufferFn), EngineMembers.Method(R("cb-read")));
			byte[] array = EngineMembers.Method(HookTargets.Replay).GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
			string text2 = Convert.ToHexString(SHA256.HashData(array)).ToLowerInvariant();
			_meterAllowed = text2 == "6d1fd8c2bbcf3dcb7f2525c5260093504ed818b1d57706e8e358da1aa8feb064";
			harmony.Patch(original, new HarmonyMethod(typeof(ReplicationProbe), "CommitPrefix"), new HarmonyMethod(typeof(ReplicationProbe), "CommitPostfix"));
			_commitActive = true;
			_measure = _meterAllowed;
			return _meterAllowed ? $"ok; command meter armed (Replay IL {array.Length} B, sha256 matches build {"2.4.0.95"})" : ("ok, but Replay IL sha256 " + text2 + " != expected (game updated?): command meter OFF, only ticks/buffers/bundles");
		}));
		if (report)
		{
			list.Add(TryInstall("probe:BuildAndReset", () =>
			{
				MethodInfo original = EngineMembers.Method(HookTargets.BuildAndReset);
				_bundleCount = EngineMembers.Getter<int>(P("bundle-count"));
				_bundleSkipped = EngineMembers.Getter<object>(P("bundle-skipped"));
				_bundleStaged = EngineMembers.Getter<object>(P("bundle-staged"));
				_bundleServer = EngineMembers.Getter<object>(P("bundle-server"));
				harmony.Patch(original, null, new HarmonyMethod(typeof(ReplicationProbe), "BundlePostfix"));
				_bundleActive = true;
				return (!sizeBundles) ? "ok (bundle sizing off)" : "ok (bundles will be serialized for size)";
			}));
		}
		if (Active & report)
		{
			try
			{
				string path = GameLog.LogDirectory();
				string text = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Environment.ProcessId;
				_csvPath = Path.Combine(path, "y51online_probe_" + text + ".csv");
				_typesPath = Path.Combine(path, "y51online_probe_" + text + "_types.txt");
				File.WriteAllText(_csvPath, "utc,window_s,server_ticks,direction,category,key,count,bytes,count_per_tick,bytes_per_tick" + Environment.NewLine);
				_log.Info("probe: CSV " + _csvPath);
				_log.Info("probe: codec list " + _typesPath);
			}
			catch (Exception ex)
			{
				_log.Warn("probe: cannot create CSV (" + ex.Message + "); summaries go to the log only");
				_csvPath = (_typesPath = null);
			}
			lock (Sync)
			{
				_window = new Window();
			}
			_timer = new Timer((object? _) =>
			{
				Flush();
			}, null, 10000, 10000);
		}
		return list;
		static PropertyInfo P(string id)
		{
			return EngineMembers.Property(HookTargets.Reflected.Single((EngineMember m) => m.Id == id));
		}
		static EngineMember R(string id)
		{
			return HookTargets.Reflected.Single((EngineMember m) => m.Id == id);
		}
	}

	private static (string, bool, string) TryInstall(string name, Func<string> install)
	{
		try
		{
			return (name, true, install());
		}
		catch (Exception ex)
		{
			return (name, false, (ex is MissingMemberException) ? ex.Message : ex.ToString());
		}
	}

	private static void Disable(string where, Exception e)
	{
		_commitActive = (_bundleActive = (_measure = false));
		_log.Error($"probe: {where} failed, probe disabled (game unaffected): {e}");
	}

	private unsafe static void InstrumentTable(ReplicationCommandBuffer.Stub[] stubs)
	{
		if (!_meterAllowed || stubs.Length == 0 || Instrumented.TryGetValue(stubs, out object _))
		{
			return;
		}
		lock (Sync)
		{
			if (Instrumented.TryGetValue(stubs, out object _))
			{
				return;
			}
			nint[] array = new nint[stubs.Length];
			for (int i = 0; i < stubs.Length; i++)
			{
				array[i] = (nint)stubs[i].Wrapper.Reader;
			}
			delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void> meter = &MeasuringReader;
			if (_origReaders != null)
			{
				if (_origReaders.Length != array.Length || !_origReaders.AsSpan().SequenceEqual<nint>((ReadOnlySpan<nint>)array))
				{
					if (!array.All((nint r) => r == (nint)meter))
					{
						_log.Warn($"probe: another replay table with different readers ({array.Length} entries); not metering it");
					}
					Instrumented.AddOrUpdate(stubs, new object());
					return;
				}
			}
			else
			{
				_stubs = BuildStubInfo(array);
				Volatile.Write(ref _origReaders, array);
			}
			for (int num = 0; num < stubs.Length; num++)
			{
				stubs[num].Wrapper = new ReaderSignature((delegate*<object, ref CommandBufferState, IntPtr, ReplicationCommandBuffer, void>)meter);
			}
			Instrumented.AddOrUpdate(stubs, new object());
			_log.Info($"probe: command meter live on {stubs.Length} replicated command types ({_stubs.Count((StubInfo s) => s.Kind != StubKind.Signal)} generic InProcess commands)");
		}
	}

	private unsafe static void MeasuringReader(object context, ref CommandBufferState st, nint callback, ReplicationCommandBuffer cb)
	{
		if (!SlowTicks.Enabled)
		{
			MeasuringReaderCore(context, ref st, callback, cb);
			return;
		}
		int stub = Unsafe.ReadUnaligned<int>(st.DataPtr + st.DataOffset - 4);
		long timestamp = Stopwatch.GetTimestamp();
		MeasuringReaderCore(context, ref st, callback, cb);
		SlowTicks.Record(stub, Stopwatch.GetTimestamp() - timestamp);
	}

	private unsafe static void MeasuringReaderCore(object context, ref CommandBufferState st, nint callback, ReplicationCommandBuffer cb)
	{
		byte* dataPtr = st.DataPtr;
		nint dataOffset = st.DataOffset;
		int num = Unsafe.ReadUnaligned<int>(dataPtr + dataOffset - 4);
		delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void> obj = (delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void>)_origReaders[num];
		if (dataOffset >= 8 && Unsafe.ReadUnaligned<int>(dataPtr + dataOffset - 8) == 1580056798)
		{
			if (AudioGate.Enabled && AudioGate.Mute(num, cb, dataPtr + dataOffset, st.ActiveBytes - dataOffset, injected: true, s2c: true))
			{
				AudioGate.Enter();
				try
				{
					ReplayGuard.Invoke(obj, context, ref st, callback, cb, num);
					return;
				}
				finally
				{
					AudioGate.Exit();
				}
			}
			ReplayGuard.Invoke(obj, context, ref st, callback, cb, num);
			return;
		}
		if ((ControlGuard.Enabled || ControlGuard.HostFlightGate) && _serverCtx != null && _serverCtx.TryGetTarget(out object target) && target == context)
		{
			int num2 = -1;
			try
			{
				StubInfo[] stubs = _stubs;
				bool flag = ControlGuard.HostFlightGate && num >= 0 && num < stubs.Length;
				if (flag)
				{
					StubKind kind = stubs[num].Kind;
					bool flag2 = kind - 2 <= StubKind.PostInvoke;
					flag = flag2;
				}
				if (flag)
				{
					num2 = ControlGuard.HostFlightSkip(stubs[num].Kind == StubKind.RemoveData, dataPtr + dataOffset, st.ActiveBytes - dataOffset, cb);
				}
				else if (ControlGuard.Enabled)
				{
					num2 = ControlGuard.HostClientSkip(num, dataPtr + dataOffset, st.ActiveBytes - dataOffset, cb);
				}
			}
			catch (Exception value)
			{
				ControlGuard.Enabled = false;
				ControlGuard.HostFlightGate = false;
				_log.Error($"control guard failed and is switched off (the game is unaffected): {value}");
			}
			if (num2 >= 0)
			{
				st.DataOffset = dataOffset + num2;
				return;
			}
		}
		if (!_measure)
		{
			obj(context, ref st, callback, cb);
			return;
		}
		bool flag3 = false;
		Direction dir = Direction.S2C;
		StubKind kind2 = StubKind.Signal;
		object key = null;
		int intKey = num;
		int payloadBytes = 0;
		IS2CTap tap = _tap;
		try
		{
			flag3 = TryDirection(context, out dir);
			bool flag4 = ((tap != null) & flag3) && dir == Direction.S2C;
			if (flag3 && (_classify | flag4))
			{
				Classify(cb, num, dataPtr + dataOffset, st.ActiveBytes - dataOffset, out kind2, out key, out intKey, out payloadBytes);
			}
			if (flag4 && kind2 != StubKind.Signal)
			{
				TapCommand(tap, cb, kind2, dataPtr + dataOffset, st.ActiveBytes - dataOffset, key, intKey, payloadBytes);
			}
			else if (flag4)
			{
				tap.OnSignal(cb, num, new ReadOnlySpan<byte>(dataPtr + dataOffset, (int)Math.Min(int.MaxValue, st.ActiveBytes - dataOffset)));
			}
		}
		catch (Exception value2)
		{
			if (tap != null && _tap == tap)
			{
				_tap = null;
				_log.Error($"host tap failed, world sync stops forwarding state (game unaffected): {value2}");
			}
			else
			{
				_classify = false;
				_log.Error($"probe: command classification failed, per-signal totals only from now on: {value2}");
			}
		}
		if (((tap != null && AudioGate.Enabled) & flag3) && dir == Direction.S2C && AudioGate.Mute(num, cb, dataPtr + dataOffset, st.ActiveBytes - dataOffset, injected: false, s2c: true))
		{
			AudioGate.Enter();
			try
			{
				obj(context, ref st, callback, cb);
			}
			finally
			{
				AudioGate.Exit();
			}
		}
		else
		{
			obj(context, ref st, callback, cb);
		}
		if (flag3 && _report)
		{
			Record(dir, kind2, num, intKey, key, (long)(st.DataOffset - dataOffset) + 8L, payloadBytes);
		}
	}

	private unsafe static void TapCommand(IS2CTap tap, ReplicationCommandBuffer cb, StubKind kind, byte* payload, nint available, object? key, int typeId, int length)
	{
		switch (kind)
		{
		case StubKind.PostData:
		{
			int num = ReadInt(payload, available, 4);
			if (typeId >= 0 && length >= 0 && (nint)16 + (nint)length <= available)
			{
				Entity source = ((num >= 0) ? (cb.ReadObject(num) as Entity) : null);
				tap.OnPostData(source, typeId, new ReadOnlySpan<byte>(payload + 16, length), cb);
			}
			break;
		}
		case StubKind.RemoveData:
		{
			int num2 = ReadInt(payload, available, 4);
			if (typeId >= 0)
			{
				tap.OnRemoveData((num2 >= 0) ? (cb.ReadObject(num2) as Entity) : null, typeId);
			}
			break;
		}
		case StubKind.PostInvoke:
			if (key is Delegate callback)
			{
				tap.OnInvoke(callback);
			}
			break;
		}
	}

	private static bool TryDirection(object context, out Direction dir)
	{
		if (_serverCtx != null && _serverCtx.TryGetTarget(out object target) && target == context)
		{
			dir = Direction.C2S;
			return true;
		}
		if (_clientCtx != null && _clientCtx.TryGetTarget(out object target2) && target2 == context)
		{
			dir = Direction.S2C;
			return true;
		}
		dir = Direction.S2C;
		return false;
	}

	private unsafe static StubInfo[] BuildStubInfo(nint[] readers)
	{
		ReplicationCommandBufferIndexer.CommandBufferCommand[] array;
		try
		{
			array = MetadataIndexerBase<ReplicationCommandBufferIndexer>.Instance.Table;
		}
		catch (Exception ex)
		{
			_log.Warn($"probe: command indexer unavailable ({ex.GetType().Name}: {ex.Message}); commands reported by stub index only");
			array = Array.Empty<ReplicationCommandBufferIndexer.CommandBufferCommand>();
		}
		StubInfo[] array2 = new StubInfo[readers.Length];
		bool flag = array.Length == readers.Length;
		int num = 0;
		for (int i = 0; i < readers.Length; i++)
		{
			if (flag && array[i].Reader.Reader != (delegate*<object, ref CommandBufferState, IntPtr, ReplicationCommandBuffer, void>)readers[i])
			{
				num++;
			}
			Type type = (flag ? array[i].SignalType : null);
			string text = type?.FullName ?? $"stub#{i}";
			StubInfo[] array3 = array2;
			int num2 = i;
			StubInfo stubInfo = new StubInfo();
			stubInfo.Name = ((type != null) ? Names.Short(text) : text);
			stubInfo.Detail = (flag ? WriterParams(array[i].WriterInfo) : "");
			StubInfo stubInfo2 = stubInfo;
			stubInfo2.Kind = text switch
			{
				"Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostInvokeInternalSignal" => StubKind.PostInvoke, 
				"Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostDataSignal" => StubKind.PostData, 
				"Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+RemoveDataSignal" => StubKind.RemoveData, 
				_ => StubKind.Signal, 
			};
			array3[num2] = stubInfo;
		}
		if (!flag || num > 0)
		{
			_log.Warn($"probe: replay table ({readers.Length}) and command indexer ({array.Length}) disagree ({num} reader mismatches); signal names may be wrong");
		}
		return array2;
	}

	private static string WriterParams(MethodInfo? writer)
	{
		if (writer == null)
		{
			return "";
		}
		IEnumerable<string> values = from p in writer.GetParameters().Skip(1)
			select Names.Short(p.ParameterType.FullName ?? p.ParameterType.Name) + " " + p.Name;
		return "(" + string.Join(", ", values) + ")";
	}

	private unsafe static int ReadInt(byte* payload, nint available, int offset)
	{
		if (offset + 4 > available)
		{
			return -1;
		}
		return Unsafe.ReadUnaligned<int>(payload + offset);
	}

	private unsafe static void Classify(ReplicationCommandBuffer cb, int num, byte* payload, nint available, out StubKind kind, out object? key, out int intKey, out int payloadBytes)
	{
		kind = StubKind.Signal;
		key = null;
		intKey = num;
		payloadBytes = 0;
		StubInfo[] stubs = _stubs;
		if ((uint)num >= (uint)stubs.Length)
		{
			return;
		}
		kind = stubs[num].Kind;
		switch (kind)
		{
		case StubKind.PostInvoke:
		{
			int num2 = ReadInt(payload, available, 4);
			key = ((num2 >= 0) ? cb.ReadObject(num2) : null);
			break;
		}
		case StubKind.PostData:
			intKey = ReadInt(payload, available, 8);
			payloadBytes = ReadInt(payload, available, 12);
			if (intKey >= 0 && !DataTypes.ContainsKey(intKey))
			{
				ResolveDataType(intKey);
			}
			break;
		case StubKind.RemoveData:
			intKey = ReadInt(payload, available, 8);
			if (intKey >= 0 && !DataTypes.ContainsKey(intKey))
			{
				ResolveDataType(intKey);
			}
			break;
		}
	}

	private static void ResolveDataType(int typeId)
	{
		try
		{
			RuntimeDataInfo.Info info = RuntimeDataInfo.Of(typeId);
			DataTypes[typeId] = (Names.Short(info.RuntimeType?.FullName), info.Size);
		}
		catch (Exception)
		{
			DataTypes[typeId] = ($"typeId#{typeId}", 0);
		}
	}

	private static Counter Get<TKey>(Dictionary<TKey, Counter> d, TKey k) where TKey : notnull
	{
		if (!d.TryGetValue(k, out Counter value))
		{
			value = (d[k] = new Counter());
		}
		return value;
	}

	private static void Record(Direction dir, StubKind kind, int num, int intKey, object? key, long bytes, int payload)
	{
		try
		{
			lock (Sync)
			{
				DirAcc dirAcc = _window.Dir[(int)dir];
				dirAcc.Totals.Commands++;
				_measuredCommands++;
				Counter counter = Get(dirAcc.Stubs, num);
				counter.Count++;
				counter.Bytes += bytes;
				switch (kind)
				{
				case StubKind.PostData:
				{
					Counter counter3 = Get(dirAcc.Data, intKey);
					counter3.Count++;
					counter3.Bytes += bytes;
					counter3.Payload += Math.Max(0, payload);
					dirAcc.Totals.DataPayloadBytes += Math.Max(0, payload);
					break;
				}
				case StubKind.RemoveData:
				{
					Counter counter4 = Get(dirAcc.DataRemove, intKey);
					counter4.Count++;
					counter4.Bytes += bytes;
					break;
				}
				case StubKind.PostInvoke:
					if (key is Delegate d)
					{
						Counter counter2 = ClassifyInvoke(dirAcc, d);
						counter2.Count++;
						counter2.Bytes += bytes;
					}
					break;
				}
			}
		}
		catch (Exception value)
		{
			if (_classify)
			{
				_classify = false;
				_log.Error($"probe: recording failed, classification off: {value}");
			}
		}
	}

	private static Counter ClassifyInvoke(DirAcc acc, Delegate d)
	{
		MethodInfo method = d.Method;
		if (method.Name.Contains("MoveNextRemotely", StringComparison.Ordinal) && d.Target != null)
		{
			return Get(k: (StoryField.GetOrAdd(d.Target.GetType(), (Type t) => t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => typeof(IAsyncStateMachine).IsAssignableFrom(f.FieldType)))?.GetValue(d.Target))?.GetType() ?? typeof(IAsyncStateMachine), d: acc.Stories);
		}
		if (method.Name.Contains("FinishNetworkStory", StringComparison.Ordinal))
		{
			return Get(acc.StoryFinish, method.DeclaringType ?? typeof(object));
		}
		return Get(acc.Invokes, method);
	}

	private static void CommitPrefix(object __instance, out ReplicationCommandBuffer? __state)
	{
		__state = null;
		if (!_commitActive)
		{
			return;
		}
		try
		{
			__state = ((ReplicationCommandBufferSessionComponent)__instance).Buffer;
		}
		catch (Exception)
		{
		}
	}

	private static void CommitPostfix(object __instance, ReplicationCommandBuffer? __state)
	{
		if (!_commitActive)
		{
			return;
		}
		try
		{
			bool flag = __instance == _serverComp || (__instance != _clientComp && Attach(__instance));
			lock (Sync)
			{
				if (flag)
				{
					_window.ServerTicks++;
					_totalServerTicks++;
				}
				else
				{
					_window.ClientTicks++;
				}
			}
			if (__state != null && _report)
			{
				MeasureBuffer(__state, (!flag) ? Direction.C2S : Direction.S2C);
			}
		}
		catch (Exception e)
		{
			Disable("CommitFrame postfix", e);
		}
	}

	private static bool Attach(object component)
	{
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00df: Unknown result type (might be due to invalid IL or missing references)
		bool flag = _isServer(component);
		object target = _replayContext(component);
		if (flag)
		{
			_serverComp = component;
			_serverCtx = new WeakReference<object>(target);
		}
		else
		{
			_clientComp = component;
			_clientCtx = new WeakReference<object>(target);
		}
		Keen.VRage.Multiplayer.Sessions.ReplicationCommandBuffer.ReplayTable val = (Keen.VRage.Multiplayer.Sessions.ReplicationCommandBuffer.ReplayTable)_replayTableField.GetValue(component);
		_log.Info($"{(_report ? "probe" : "tap")}: {(flag ? "server" : "client")} scene replication component attached (replay table {val.Stubs?.Length ?? 0} entries)");
		if (val.Stubs != null)
		{
			InstrumentTable(val.Stubs);
		}
		return flag;
	}

	private static void MeasureBuffer(ReplicationCommandBuffer cb, Direction dir)
	{
		List<byte[]> list = _committedBuffers(cb);
		long num = 0L;
		for (int i = 0; i < list.Count; i++)
		{
			_readBuffer(cb, i, out var offset, out var activeBytes);
			num += activeBytes - offset;
		}
		List<ValueTuple<object>> list2 = _objects(cb);
		int count = list2.Count;
		lock (Sync)
		{
			DirAcc dirAcc = _window.Dir[(int)dir];
			DirectionTotals totals = dirAcc.Totals;
			totals.Buffers++;
			totals.InlineBytes += num;
			totals.ObjectRefs += count;
			totals.MaxTickBytes = Math.Max(totals.MaxTickBytes, num);
			totals.TickBytes.Add((int)Math.Min(2147483647L, num));
			TotalBytes[(int)dir] += num;
			for (int j = 0; j < count; j++)
			{
				Get(dirAcc.ObjRefs, list2[j].Item1?.GetType() ?? typeof(void)).Count++;
			}
		}
	}

	private static void BundlePostfix(object __result)
	{
		if (!_bundleActive || __result == null)
		{
			return;
		}
		try
		{
			int num = _bundleCount(__result);
			int num2 = (_bundleSkipped(__result) as IReadOnlyCollection<Entity>)?.Count ?? 0;
			int num3 = (_bundleStaged(__result) as IReadOnlyCollection<Entity>)?.Count ?? 0;
			long num4 = -1L;
			double ms = 0.0;
			if (_sizeBundles && num > 0 && _bundleServer(__result) is EntityBundle bundle)
			{
				num4 = SizeOf(bundle, out ms);
			}
			long num5 = Interlocked.Increment(ref _bundleNo);
			lock (Sync)
			{
				BundleTotals bundles = _window.Bundles;
				bundles.Bundles++;
				bundles.Entities += num;
				bundles.Skipped += num2;
				bundles.Staged += num3;
				bundles.MaxBundleEntities = Math.Max(bundles.MaxBundleEntities, num);
				bundles.SerializeMs += ms;
				if (num4 >= 0)
				{
					bundles.SizedBundles++;
					bundles.SerializedBytes += num4;
					bundles.MaxBundleBytes = Math.Max(bundles.MaxBundleBytes, num4);
				}
				else if (_sizeBundles && num > 0)
				{
					bundles.SizeFailures++;
				}
				Surface.Add("bundle", "entity spawn bundle", "", 1L, Math.Max(0L, num4));
			}
			if (num5 == 1 || num >= 50)
			{
				_log.Info($"probe: entity bundle #{num5}: {num} entities (+{num2} skipped, {num3} staged)" + ((num4 >= 0) ? $", {MpLogExtensions.Bytes(num4)} serialized in {ms:0} ms" : "") + ((num5 == 1) ? "  <- first bundle after load = join-time world sync estimate" : ""));
			}
		}
		catch (Exception e)
		{
			Disable("BuildAndReset postfix", e);
		}
	}

	private static long SizeOf(EntityBundle bundle, out double ms)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			using MemoryStream memoryStream = new MemoryStream();
			using (SerializationContext context = new SerializationContext(memoryStream, "y51online-probe.vrb", new EntityProxySerializationContext(), new DefinitionManagerDefinitionSerializationContext(Singleton<DefinitionManager>.Instance)))
			{
				SerializationHelper.Serialize(context, in bundle, SerializerFormat.Binary);
			}
			return memoryStream.ToArray().LongLength;
		}
		catch (Exception ex)
		{
			if (Interlocked.Increment(ref _sizeFailures) <= 3)
			{
				_log.Warn($"probe: bundle serialization failed ({_sizeFailures}/3 before sizing is switched off): {ex.GetType().Name}: {ex.Message}");
			}
			if (_sizeFailures >= 3)
			{
				_sizeBundles = false;
			}
			return -1L;
		}
		finally
		{
			ms = stopwatch.Elapsed.TotalMilliseconds;
			if (Interlocked.Add(ref _bundleSizingMs, (long)ms) > 60000 && _sizeBundles)
			{
				_sizeBundles = false;
				_log.Warn("probe: more than 60 s spent serializing bundles, sizing switched off");
			}
		}
	}

	internal static void RegisterSideForTest(object replayContext, bool server)
	{
		if (server)
		{
			_serverCtx = new WeakReference<object>(replayContext);
		}
		else
		{
			_clientCtx = new WeakReference<object>(replayContext);
		}
	}

	internal static void InstrumentForTest(ReplicationCommandBuffer.Stub[] stubs)
	{
		InstrumentTable(stubs);
	}

	internal static void MeasureBufferForTest(ReplicationCommandBuffer cb, bool server)
	{
		MeasureBuffer(cb, (!server) ? Direction.C2S : Direction.S2C);
	}

	internal static void FlushForTest()
	{
		Flush();
	}

	private static void Flush()
	{
		if (!_report)
		{
			return;
		}
		try
		{
			Window window;
			lock (Sync)
			{
				window = _window;
				_window = new Window();
			}
			ProbeWindow probeWindow = Resolve(window);
			if (probeWindow.ServerTicks == 0L && probeWindow.ClientTicks == 0L && probeWindow.Bundles.Bundles == 0L && probeWindow.Dir.All((DirectionTotals d) => d.Buffers == 0L && d.Commands == 0))
			{
				return;
			}
			GameLog.Instance.Info(probeWindow.Summary());
			if (_csvPath != null)
			{
				File.AppendAllLines(_csvPath, probeWindow.ToCsvRows());
			}
			if (_typesPath != null)
			{
				string header;
				lock (Sync)
				{
					header = $"Y51_SE2Online replication probe, game {"2.4.0.95"}, written {DateTime.Now:yyyy-MM-dd HH:mm:ss}; server ticks so far {_totalServerTicks}, S2C {TotalBytes[0] / 1024} KiB, C2S {TotalBytes[1] / 1024} KiB inline.\n" + "Columns: count x inline-bytes  key  :: detail (signal args / data size / story fields). Every key below needs a wire codec.";
				}
				string contents;
				lock (Sync)
				{
					contents = Surface.Render(header);
				}
				File.WriteAllText(_typesPath, contents);
			}
		}
		catch (Exception value)
		{
			_log.WarnOnce("probe-flush", $"probe: flush failed (will keep trying): {value}");
		}
	}

	private static ProbeWindow Resolve(Window w)
	{
		ProbeWindow probeWindow = new ProbeWindow
		{
			StartUtc = w.StartUtc,
			Seconds = (double)(MpClock.NowMs() - w.StartMs) / 1000.0,
			ServerTicks = w.ServerTicks,
			ClientTicks = w.ClientTicks
		};
		BundleTotals bundles = w.Bundles;
		BundleTotals bundles2 = probeWindow.Bundles;
		bundles2.Bundles = bundles.Bundles;
		bundles2.Entities = bundles.Entities;
		bundles2.Skipped = bundles.Skipped;
		bundles2.Staged = bundles.Staged;
		bundles2.SerializedBytes = bundles.SerializedBytes;
		bundles2.MaxBundleBytes = bundles.MaxBundleBytes;
		bundles2.MaxBundleEntities = bundles.MaxBundleEntities;
		bundles2.SizedBundles = bundles.SizedBundles;
		bundles2.SizeFailures = bundles.SizeFailures;
		bundles2.SerializeMs = bundles.SerializeMs;
		StubInfo[] stubs = _stubs;
		lock (Sync)
		{
			for (int i = 0; i < 2; i++)
			{
				DirAcc dirAcc = w.Dir[i];
				Direction dir = (Direction)i;
				DirectionTotals obj = probeWindow.Dir[i];
				obj.Buffers = dirAcc.Totals.Buffers;
				obj.InlineBytes = dirAcc.Totals.InlineBytes;
				obj.Commands = dirAcc.Totals.Commands;
				obj.ObjectRefs = dirAcc.Totals.ObjectRefs;
				obj.DataPayloadBytes = dirAcc.Totals.DataPayloadBytes;
				obj.MaxTickBytes = dirAcc.Totals.MaxTickBytes;
				obj.TickBytes.AddRange(dirAcc.Totals.TickBytes);
				foreach (KeyValuePair<int, Counter> stub in dirAcc.Stubs)
				{
					StubInfo stubInfo = (((uint)stub.Key < (uint)stubs.Length) ? stubs[stub.Key] : null);
					if (stubInfo == null || stubInfo.Kind == StubKind.Signal)
					{
						string key = stubInfo?.Name ?? $"stub#{stub.Key}";
						Add(probeWindow, dir, "signal", key, stubInfo?.Detail ?? "", stub.Value);
					}
				}
				foreach (KeyValuePair<int, Counter> datum in dirAcc.Data)
				{
					int value2;
					string key2;
					if (!DataTypes.TryGetValue(datum.Key, out (string, int) value))
					{
						string text = $"typeId#{datum.Key}";
						value2 = 0;
						key2 = text;
					}
					else
					{
						(key2, value2) = value;
					}
					Add(probeWindow, dir, "data", key2, $"{value2} B unmanaged, TypeId {datum.Key} (process-local, remap by name)", datum.Value);
				}
				foreach (KeyValuePair<int, Counter> item in dirAcc.DataRemove)
				{
					string key3;
					if (!DataTypes.TryGetValue(item.Key, out (string, int) value3))
					{
						key3 = $"typeId#{item.Key}";
					}
					else
					{
						(key3, _) = value3;
					}
					Add(probeWindow, dir, "data-remove", key3, "", item.Value);
				}
				foreach (KeyValuePair<MethodInfo, Counter> invoke in dirAcc.Invokes)
				{
					Add(probeWindow, dir, "invoke", InvokeName(invoke.Key), "closure fields: " + ClosureFields(invoke.Key.DeclaringType), invoke.Value);
				}
				foreach (KeyValuePair<Type, Counter> story in dirAcc.Stories)
				{
					Add(probeWindow, dir, "story", Names.GeneratedType(story.Key.FullName), StoryFields(story.Key), story.Value);
				}
				foreach (KeyValuePair<Type, Counter> item2 in dirAcc.StoryFinish)
				{
					Type[] array = (item2.Key.IsGenericType ? item2.Key.GetGenericArguments() : Type.EmptyTypes);
					string text2 = ((array.Length != 0) ? Names.Short(array[^1].FullName ?? array[^1].Name) : "?");
					Add(probeWindow, dir, "story-finish", "result " + text2, "", item2.Value);
				}
				foreach (KeyValuePair<Type, Counter> objRef in dirAcc.ObjRefs)
				{
					Add(probeWindow, dir, "objref", (objRef.Key == typeof(void)) ? "null" : Names.Short(objRef.Key.FullName ?? objRef.Key.Name), "", objRef.Value);
				}
			}
			return probeWindow;
		}
	}

	private static void Add(ProbeWindow pw, Direction dir, string category, string key, string detail, Counter c)
	{
		pw.Rows.Add(new ProbeRow(dir, category, key, c.Count, c.Bytes));
		Surface.Add(category, $"{dir} {key}", detail, c.Count, c.Bytes);
	}

	private static string InvokeName(MethodInfo m)
	{
		return Names.GeneratedType(m.DeclaringType?.FullName) + "." + Names.Member(m.Name);
	}

	private static string ClosureFields(Type? t)
	{
		if (t == null)
		{
			return "";
		}
		IEnumerable<string> values = from f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			select Names.Short(f.FieldType.FullName ?? f.FieldType.Name) + " " + Names.Member(f.Name);
		return string.Join(", ", values);
	}

	private static string StoryFields(Type sm)
	{
		return StoryFieldsText.GetOrAdd(sm, (Type t) =>
		{
			IEnumerable<string> values = from f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				where !f.Name.StartsWith("<>", StringComparison.Ordinal)
				select Names.Short(f.FieldType.FullName ?? f.FieldType.Name) + " " + Names.Member(f.Name);
			return "state machine fields: " + string.Join(", ", values);
		});
	}

	public static void Stop()
	{
		_tap = null;
		_commitActive = (_bundleActive = (_measure = false));
		Timer timer = Interlocked.Exchange(ref _timer, null);
		if (timer != null)
		{
			timer.Dispose();
			Flush();
			long totalServerTicks;
			long b;
			long b2;
			lock (Sync)
			{
				totalServerTicks = _totalServerTicks;
				b = TotalBytes[0];
				b2 = TotalBytes[1];
			}
			_log.Info($"probe: stopped after {totalServerTicks} server ticks; S2C {MpLogExtensions.Bytes(b)}, C2S {MpLogExtensions.Bytes(b2)} inline command bytes; {_measuredCommands} commands metered; {_bundleNo} entity bundles");
		}
	}
}
