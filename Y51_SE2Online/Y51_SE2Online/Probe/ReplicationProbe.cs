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
		public readonly DirectionTotals Totals = new();

		public readonly Dictionary<int, Counter> Stubs = new();

		public readonly Dictionary<int, Counter> Data = new();

		public readonly Dictionary<int, Counter> DataRemove = new();

		public readonly Dictionary<MethodInfo, Counter> Invokes = new();

		public readonly Dictionary<Type, Counter> Stories = new();

		public readonly Dictionary<Type, Counter> StoryFinish = new();

		public readonly Dictionary<Type, Counter> ObjRefs = new();
	}

	private sealed class Window
	{
		public readonly DateTime StartUtc = DateTime.UtcNow;

		public readonly long StartMs = MpClock.NowMs();

		public long ServerTicks;

		public long ClientTicks;

		public readonly DirAcc[] Dir = [new DirAcc(), new DirAcc()];

		public readonly BundleTotals Bundles = new();
	}

	private const string PostInvokeSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostInvokeInternalSignal";

	private const string PostDataSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostDataSignal";

	private const string RemoveDataSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+RemoveDataSignal";

	private static IMpLog _log = GameLog.Instance;

	private static readonly object Sync = new();

	private static Window _window = new();

	private static readonly CodecSurface Surface = new();

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

	private static readonly ConditionalWeakTable<ReplicationCommandBuffer.Stub[], object> Instrumented = new();

	private static readonly ConcurrentDictionary<int, (string Name, int Size)> DataTypes = new();

	private static readonly ConcurrentDictionary<Type, FieldInfo?> StoryField = new();

	private static readonly ConcurrentDictionary<Type, string> StoryFieldsText = new();

	private static long _bundleNo;

	private static long _bundleSizingMs;

	private static long _totalServerTicks;

	private static long _measuredCommands;

	private static readonly long[] TotalBytes = new long[2];

	private static int _sizeFailures;

	public static IS2CTap? Tap
	{
		get => _tap;
		set => _tap = value;
	}

	public static bool Active => _commitActive || _bundleActive;

	public static bool MeterArmed => _meterAllowed && _commitActive;

	public unsafe static bool IsGuarded(object component)
	{
		try
		{
			if (!MeterArmed || _replayTableField == null)
			{
				return false;
			}
			ReplicationCommandBuffer.Stub[] stubs = ((ReplicationCommandBuffer.ReplayTable?)_replayTableField.GetValue(component))?.Stubs;
			if (stubs == null || stubs.Length == 0)
			{
				return false;
			}
			delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void> meter = &MeasuringReader;
			return stubs[0].Wrapper.Reader == meter;
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
		_sizeBundles = sizeBundles && report;
		_report = report;
		_classify = report;
		var results = new List<(string, bool, string)>();
		results.Add(TryInstall("probe:CommitFrame", () =>
		{
			MethodInfo original = EngineMembers.Method(HookTargets.CommitFrame);
			_isServer = EngineMembers.Getter<bool>(EngineMembers.Property(R("ipr-isserver")));
			_replayContext = EngineMembers.Getter<object>(EngineMembers.Property(R("ipr-replayctx")));
			_replayTableField = EngineMembers.Field(R("ipr-replaytable"));
			_committedBuffers = AccessTools.FieldRefAccess<CommandBufferBase, List<byte[]>>(EngineMembers.Field(R("cb-buffers")));
			_objects = AccessTools.FieldRefAccess<CommandBufferBase.WithObjects, List<ValueTuple<object>>>(EngineMembers.Field(R("cb-objects")));
			_readBuffer = (ReadBufferFn)Delegate.CreateDelegate(typeof(ReadBufferFn), EngineMembers.Method(R("cb-read")));
			// The command meter parses Replay's command header, so it only runs against the exact IL it was written for.
			byte[] replayIl = EngineMembers.Method(HookTargets.Replay).GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
			string hash = Convert.ToHexString(SHA256.HashData(replayIl)).ToLowerInvariant();
			_meterAllowed = hash == "6d1fd8c2bbcf3dcb7f2525c5260093504ed818b1d57706e8e358da1aa8feb064";
			harmony.Patch(original, new HarmonyMethod(typeof(ReplicationProbe), "CommitPrefix"), new HarmonyMethod(typeof(ReplicationProbe), "CommitPostfix"));
			_commitActive = true;
			_measure = _meterAllowed;
			return _meterAllowed
				? $"ok; command meter armed (Replay IL {replayIl.Length} B, sha256 matches build {HookTargets.GameBuild})"
				: "ok, but Replay IL sha256 " + hash + " != expected (game updated?): command meter OFF, only ticks/buffers/bundles";
		}));
		if (report)
		{
			results.Add(TryInstall("probe:BuildAndReset", () =>
			{
				MethodInfo original = EngineMembers.Method(HookTargets.BuildAndReset);
				_bundleCount = EngineMembers.Getter<int>(P("bundle-count"));
				_bundleSkipped = EngineMembers.Getter<object>(P("bundle-skipped"));
				_bundleStaged = EngineMembers.Getter<object>(P("bundle-staged"));
				_bundleServer = EngineMembers.Getter<object>(P("bundle-server"));
				harmony.Patch(original, null, new HarmonyMethod(typeof(ReplicationProbe), "BundlePostfix"));
				_bundleActive = true;
				return !sizeBundles ? "ok (bundle sizing off)" : "ok (bundles will be serialized for size)";
			}));
		}
		if (Active && report)
		{
			try
			{
				string dir = GameLog.LogDirectory();
				string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Environment.ProcessId;
				_csvPath = Path.Combine(dir, "y51online_probe_" + stamp + ".csv");
				_typesPath = Path.Combine(dir, "y51online_probe_" + stamp + "_types.txt");
				File.WriteAllText(_csvPath, ProbeWindow.CsvHeader + Environment.NewLine);
				_log.Info("probe: CSV " + _csvPath);
				_log.Info("probe: codec list " + _typesPath);
			}
			catch (Exception ex)
			{
				_log.Warn("probe: cannot create CSV (" + ex.Message + "); summaries go to the log only");
				_csvPath = _typesPath = null;
			}
			lock (Sync)
			{
				_window = new Window();
			}
			_timer = new Timer(_ => Flush(), null, 10000, 10000);
		}
		return results;

		static PropertyInfo P(string id) => EngineMembers.Property(HookTargets.Reflected.Single(m => m.Id == id));

		static EngineMember R(string id) => HookTargets.Reflected.Single(m => m.Id == id);
	}

	private static (string, bool, string) TryInstall(string name, Func<string> install)
	{
		try
		{
			return (name, true, install());
		}
		catch (Exception ex)
		{
			return (name, false, ex is MissingMemberException ? ex.Message : ex.ToString());
		}
	}

	private static void Disable(string where, Exception e)
	{
		_commitActive = _bundleActive = _measure = false;
		_log.Error($"probe: {where} failed, probe disabled (game unaffected): {e}");
	}

	private unsafe static void InstrumentTable(ReplicationCommandBuffer.Stub[] stubs)
	{
		if (!_meterAllowed || stubs.Length == 0 || Instrumented.TryGetValue(stubs, out _))
		{
			return;
		}
		lock (Sync)
		{
			if (Instrumented.TryGetValue(stubs, out _))
			{
				return;
			}
			nint[] readers = new nint[stubs.Length];
			for (int i = 0; i < stubs.Length; i++)
			{
				readers[i] = (nint)stubs[i].Wrapper.Reader;
			}
			delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void> meter = &MeasuringReader;
			if (_origReaders != null)
			{
				if (_origReaders.Length != readers.Length || !_origReaders.AsSpan().SequenceEqual(readers))
				{
					if (!readers.All(r => r == (nint)meter))
					{
						_log.Warn($"probe: another replay table with different readers ({readers.Length} entries); not metering it");
					}
					Instrumented.AddOrUpdate(stubs, new object());
					return;
				}
			}
			else
			{
				_stubs = BuildStubInfo(readers);
				Volatile.Write(ref _origReaders, readers);
			}
			for (int i = 0; i < stubs.Length; i++)
			{
				stubs[i].Wrapper = new ReaderSignature(meter);
			}
			Instrumented.AddOrUpdate(stubs, new object());
			_log.Info($"probe: command meter live on {stubs.Length} replicated command types ({_stubs.Count(s => s.Kind != StubKind.Signal)} generic InProcess commands)");
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
		long start = Stopwatch.GetTimestamp();
		MeasuringReaderCore(context, ref st, callback, cb);
		SlowTicks.Record(stub, Stopwatch.GetTimestamp() - start);
	}

	private unsafe static void MeasuringReaderCore(object context, ref CommandBufferState st, nint callback, ReplicationCommandBuffer cb)
	{
		byte* dataPtr = st.DataPtr;
		nint dataOffset = st.DataOffset;
		int stub = Unsafe.ReadUnaligned<int>(dataPtr + dataOffset - 4);
		var original = (delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void>)_origReaders[stub];
		if (dataOffset >= 8 && Unsafe.ReadUnaligned<int>(dataPtr + dataOffset - 8) == 1580056798)
		{
			if (AudioGate.Enabled && AudioGate.Mute(stub, cb, dataPtr + dataOffset, st.ActiveBytes - dataOffset, injected: true, s2c: true))
			{
				AudioGate.Enter();
				try
				{
					ReplayGuard.Invoke(original, context, ref st, callback, cb, stub);
					return;
				}
				finally
				{
					AudioGate.Exit();
				}
			}
			ReplayGuard.Invoke(original, context, ref st, callback, cb, stub);
			return;
		}
		if ((ControlGuard.Enabled || ControlGuard.HostFlightGate) && _serverCtx != null && _serverCtx.TryGetTarget(out object server) && server == context)
		{
			int skip = -1;
			try
			{
				StubInfo[] stubs = _stubs;
				bool flightCommand = ControlGuard.HostFlightGate && stub >= 0 && stub < stubs.Length;
				if (flightCommand)
				{
					flightCommand = stubs[stub].Kind is StubKind.PostData or StubKind.RemoveData;
				}
				if (flightCommand)
				{
					skip = ControlGuard.HostFlightSkip(stubs[stub].Kind == StubKind.RemoveData, dataPtr + dataOffset, st.ActiveBytes - dataOffset, cb);
				}
				else if (ControlGuard.Enabled)
				{
					skip = ControlGuard.HostClientSkip(stub, dataPtr + dataOffset, st.ActiveBytes - dataOffset, cb);
				}
			}
			catch (Exception ex)
			{
				ControlGuard.Enabled = false;
				ControlGuard.HostFlightGate = false;
				_log.Error($"control guard failed and is switched off (the game is unaffected): {ex}");
			}
			if (skip >= 0)
			{
				st.DataOffset = dataOffset + skip;
				return;
			}
		}
		if (!_measure)
		{
			original(context, ref st, callback, cb);
			return;
		}
		bool known = false;
		Direction dir = Direction.S2C;
		StubKind kind = StubKind.Signal;
		object? key = null;
		int intKey = stub;
		int payloadBytes = 0;
		IS2CTap tap = _tap;
		try
		{
			known = TryDirection(context, out dir);
			bool tapS2C = tap != null && known && dir == Direction.S2C;
			if (known && (_classify || tapS2C))
			{
				Classify(cb, stub, dataPtr + dataOffset, st.ActiveBytes - dataOffset, out kind, out key, out intKey, out payloadBytes);
			}
			if (tapS2C && kind != StubKind.Signal)
			{
				TapCommand(tap, cb, kind, dataPtr + dataOffset, st.ActiveBytes - dataOffset, key, intKey, payloadBytes);
			}
			else if (tapS2C)
			{
				tap.OnSignal(cb, stub, new ReadOnlySpan<byte>(dataPtr + dataOffset, (int)Math.Min(int.MaxValue, st.ActiveBytes - dataOffset)));
			}
		}
		catch (Exception ex)
		{
			if (tap != null && _tap == tap)
			{
				_tap = null;
				_log.Error($"host tap failed, world sync stops forwarding state (game unaffected): {ex}");
			}
			else
			{
				_classify = false;
				_log.Error($"probe: command classification failed, per-signal totals only from now on: {ex}");
			}
		}
		if (tap != null && AudioGate.Enabled && known && dir == Direction.S2C && AudioGate.Mute(stub, cb, dataPtr + dataOffset, st.ActiveBytes - dataOffset, injected: false, s2c: true))
		{
			AudioGate.Enter();
			try
			{
				original(context, ref st, callback, cb);
			}
			finally
			{
				AudioGate.Exit();
			}
		}
		else
		{
			original(context, ref st, callback, cb);
		}
		if (known && _report)
		{
			Record(dir, kind, stub, intKey, key, (long)(st.DataOffset - dataOffset) + 8L, payloadBytes);
		}
	}

	private unsafe static void TapCommand(IS2CTap tap, ReplicationCommandBuffer cb, StubKind kind, byte* payload, nint available, object? key, int typeId, int length)
	{
		switch (kind)
		{
		case StubKind.PostData:
		{
			int sourceRef = ReadInt(payload, available, 4);
			if (typeId >= 0 && length >= 0 && (nint)16 + (nint)length <= available)
			{
				Entity? source = sourceRef >= 0 ? cb.ReadObject(sourceRef) as Entity : null;
				tap.OnPostData(source, typeId, new ReadOnlySpan<byte>(payload + 16, length), cb);
			}
			break;
		}
		case StubKind.RemoveData:
		{
			int sourceRef = ReadInt(payload, available, 4);
			if (typeId >= 0)
			{
				tap.OnRemoveData(sourceRef >= 0 ? cb.ReadObject(sourceRef) as Entity : null, typeId);
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
		if (_serverCtx != null && _serverCtx.TryGetTarget(out object server) && server == context)
		{
			dir = Direction.C2S;
			return true;
		}
		if (_clientCtx != null && _clientCtx.TryGetTarget(out object client) && client == context)
		{
			dir = Direction.S2C;
			return true;
		}
		dir = Direction.S2C;
		return false;
	}

	private unsafe static StubInfo[] BuildStubInfo(nint[] readers)
	{
		ReplicationCommandBufferIndexer.CommandBufferCommand[] table;
		try
		{
			table = MetadataIndexerBase<ReplicationCommandBufferIndexer>.Instance.Table;
		}
		catch (Exception ex)
		{
			_log.Warn($"probe: command indexer unavailable ({ex.GetType().Name}: {ex.Message}); commands reported by stub index only");
			table = Array.Empty<ReplicationCommandBufferIndexer.CommandBufferCommand>();
		}
		var infos = new StubInfo[readers.Length];
		bool aligned = table.Length == readers.Length;
		int mismatches = 0;
		for (int i = 0; i < readers.Length; i++)
		{
			if (aligned && table[i].Reader.Reader != (delegate*<object, ref CommandBufferState, IntPtr, ReplicationCommandBuffer, void>)readers[i])
			{
				mismatches++;
			}
			Type? signalType = aligned ? table[i].SignalType : null;
			string fullName = signalType?.FullName ?? $"stub#{i}";
			infos[i] = new StubInfo
			{
				Name = signalType != null ? Names.Short(fullName) : fullName,
				Detail = aligned ? WriterParams(table[i].WriterInfo) : "",
				Kind = fullName switch
				{
					PostInvokeSignal => StubKind.PostInvoke,
					PostDataSignal => StubKind.PostData,
					RemoveDataSignal => StubKind.RemoveData,
					_ => StubKind.Signal,
				}
			};
		}
		if (!aligned || mismatches > 0)
		{
			_log.Warn($"probe: replay table ({readers.Length}) and command indexer ({table.Length}) disagree ({mismatches} reader mismatches); signal names may be wrong");
		}
		return infos;
	}

	private static string WriterParams(MethodInfo? writer)
	{
		if (writer == null)
		{
			return "";
		}
		IEnumerable<string> parameters = writer.GetParameters().Skip(1)
			.Select(p => Names.Short(p.ParameterType.FullName ?? p.ParameterType.Name) + " " + p.Name);
		return "(" + string.Join(", ", parameters) + ")";
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
			int delegateRef = ReadInt(payload, available, 4);
			key = delegateRef >= 0 ? cb.ReadObject(delegateRef) : null;
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
		if (!d.TryGetValue(k, out Counter counter))
		{
			counter = d[k] = new Counter();
		}
		return counter;
	}

	private static void Record(Direction dir, StubKind kind, int num, int intKey, object? key, long bytes, int payload)
	{
		try
		{
			lock (Sync)
			{
				DirAcc acc = _window.Dir[(int)dir];
				acc.Totals.Commands++;
				_measuredCommands++;
				Counter stub = Get(acc.Stubs, num);
				stub.Count++;
				stub.Bytes += bytes;
				switch (kind)
				{
				case StubKind.PostData:
				{
					Counter data = Get(acc.Data, intKey);
					data.Count++;
					data.Bytes += bytes;
					data.Payload += Math.Max(0, payload);
					acc.Totals.DataPayloadBytes += Math.Max(0, payload);
					break;
				}
				case StubKind.RemoveData:
				{
					Counter removal = Get(acc.DataRemove, intKey);
					removal.Count++;
					removal.Bytes += bytes;
					break;
				}
				case StubKind.PostInvoke:
					if (key is Delegate d)
					{
						Counter invoke = ClassifyInvoke(acc, d);
						invoke.Count++;
						invoke.Bytes += bytes;
					}
					break;
				}
			}
		}
		catch (Exception ex)
		{
			if (_classify)
			{
				_classify = false;
				_log.Error($"probe: recording failed, classification off: {ex}");
			}
		}
	}

	private static Counter ClassifyInvoke(DirAcc acc, Delegate d)
	{
		MethodInfo method = d.Method;
		if (method.Name.Contains("MoveNextRemotely", StringComparison.Ordinal) && d.Target != null)
		{
			// A network story's MoveNextRemotely delegate targets the builder box; the state machine is one of its fields.
			Type stateMachine = (StoryField.GetOrAdd(d.Target.GetType(), (Type t) => t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(f => typeof(IAsyncStateMachine).IsAssignableFrom(f.FieldType)))
				?.GetValue(d.Target))?.GetType() ?? typeof(IAsyncStateMachine);
			return Get(acc.Stories, stateMachine);
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
			bool isServer = __instance == _serverComp || (__instance != _clientComp && Attach(__instance));
			lock (Sync)
			{
				if (isServer)
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
				MeasureBuffer(__state, !isServer ? Direction.C2S : Direction.S2C);
			}
		}
		catch (Exception e)
		{
			Disable("CommitFrame postfix", e);
		}
	}

	private static bool Attach(object component)
	{
		bool isServer = _isServer(component);
		object replayContext = _replayContext(component);
		if (isServer)
		{
			_serverComp = component;
			_serverCtx = new WeakReference<object>(replayContext);
		}
		else
		{
			_clientComp = component;
			_clientCtx = new WeakReference<object>(replayContext);
		}
		var table = (ReplicationCommandBuffer.ReplayTable)_replayTableField.GetValue(component);
		_log.Info($"{(_report ? "probe" : "tap")}: {(isServer ? "server" : "client")} scene replication component attached (replay table {table.Stubs?.Length ?? 0} entries)");
		if (table.Stubs != null)
		{
			InstrumentTable(table.Stubs);
		}
		return isServer;
	}

	private static void MeasureBuffer(ReplicationCommandBuffer cb, Direction dir)
	{
		List<byte[]> buffers = _committedBuffers(cb);
		long inlineBytes = 0L;
		for (int i = 0; i < buffers.Count; i++)
		{
			_readBuffer(cb, i, out var offset, out var activeBytes);
			inlineBytes += activeBytes - offset;
		}
		List<ValueTuple<object>> objects = _objects(cb);
		int objectCount = objects.Count;
		lock (Sync)
		{
			DirAcc acc = _window.Dir[(int)dir];
			DirectionTotals totals = acc.Totals;
			totals.Buffers++;
			totals.InlineBytes += inlineBytes;
			totals.ObjectRefs += objectCount;
			totals.MaxTickBytes = Math.Max(totals.MaxTickBytes, inlineBytes);
			totals.TickBytes.Add((int)Math.Min(int.MaxValue, inlineBytes));
			TotalBytes[(int)dir] += inlineBytes;
			for (int j = 0; j < objectCount; j++)
			{
				Get(acc.ObjRefs, objects[j].Item1?.GetType() ?? typeof(void)).Count++;
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
			int entities = _bundleCount(__result);
			int skipped = (_bundleSkipped(__result) as IReadOnlyCollection<Entity>)?.Count ?? 0;
			int staged = (_bundleStaged(__result) as IReadOnlyCollection<Entity>)?.Count ?? 0;
			long bytes = -1L;
			double ms = 0.0;
			if (_sizeBundles && entities > 0 && _bundleServer(__result) is EntityBundle bundle)
			{
				bytes = SizeOf(bundle, out ms);
			}
			long bundleNo = Interlocked.Increment(ref _bundleNo);
			lock (Sync)
			{
				BundleTotals bundles = _window.Bundles;
				bundles.Bundles++;
				bundles.Entities += entities;
				bundles.Skipped += skipped;
				bundles.Staged += staged;
				bundles.MaxBundleEntities = Math.Max(bundles.MaxBundleEntities, entities);
				bundles.SerializeMs += ms;
				if (bytes >= 0)
				{
					bundles.SizedBundles++;
					bundles.SerializedBytes += bytes;
					bundles.MaxBundleBytes = Math.Max(bundles.MaxBundleBytes, bytes);
				}
				else if (_sizeBundles && entities > 0)
				{
					bundles.SizeFailures++;
				}
				Surface.Add("bundle", "entity spawn bundle", "", 1L, Math.Max(0L, bytes));
			}
			if (bundleNo == 1 || entities >= 50)
			{
				_log.Info($"probe: entity bundle #{bundleNo}: {entities} entities (+{skipped} skipped, {staged} staged)"
					+ (bytes >= 0 ? $", {MpLogExtensions.Bytes(bytes)} serialized in {ms:0} ms" : "")
					+ (bundleNo == 1 ? "  <- first bundle after load = join-time world sync estimate" : ""));
			}
		}
		catch (Exception e)
		{
			Disable("BuildAndReset postfix", e);
		}
	}

	private static long SizeOf(EntityBundle bundle, out double ms)
	{
		var stopwatch = Stopwatch.StartNew();
		try
		{
			using var stream = new MemoryStream();
			using (var context = new SerializationContext(stream, "y51online-probe.vrb", new EntityProxySerializationContext(), new DefinitionManagerDefinitionSerializationContext(Singleton<DefinitionManager>.Instance)))
			{
				SerializationHelper.Serialize(context, in bundle, SerializerFormat.Binary);
			}
			return stream.ToArray().LongLength;
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

	internal static void InstrumentForTest(ReplicationCommandBuffer.Stub[] stubs) => InstrumentTable(stubs);

	internal static void MeasureBufferForTest(ReplicationCommandBuffer cb, bool server) => MeasureBuffer(cb, !server ? Direction.C2S : Direction.S2C);

	internal static void FlushForTest() => Flush();

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
			ProbeWindow pw = Resolve(window);
			if (pw.ServerTicks == 0L && pw.ClientTicks == 0L && pw.Bundles.Bundles == 0L && pw.Dir.All(d => d.Buffers == 0L && d.Commands == 0))
			{
				return;
			}
			GameLog.Instance.Info(pw.Summary());
			if (_csvPath != null)
			{
				File.AppendAllLines(_csvPath, pw.ToCsvRows());
			}
			if (_typesPath != null)
			{
				string header;
				lock (Sync)
				{
					header = $"Y51_SE2Online replication probe, game {HookTargets.GameBuild}, written {DateTime.Now:yyyy-MM-dd HH:mm:ss}; server ticks so far {_totalServerTicks}, S2C {TotalBytes[0] / 1024} KiB, C2S {TotalBytes[1] / 1024} KiB inline.\n"
						+ "Columns: count x inline-bytes  key  :: detail (signal args / data size / story fields). Every key below needs a wire codec.";
				}
				string contents;
				lock (Sync)
				{
					contents = Surface.Render(header);
				}
				File.WriteAllText(_typesPath, contents);
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("probe-flush", $"probe: flush failed (will keep trying): {ex}");
		}
	}

	private static ProbeWindow Resolve(Window w)
	{
		var pw = new ProbeWindow
		{
			StartUtc = w.StartUtc,
			Seconds = (MpClock.NowMs() - w.StartMs) / 1000.0,
			ServerTicks = w.ServerTicks,
			ClientTicks = w.ClientTicks
		};
		BundleTotals src = w.Bundles;
		BundleTotals dst = pw.Bundles;
		dst.Bundles = src.Bundles;
		dst.Entities = src.Entities;
		dst.Skipped = src.Skipped;
		dst.Staged = src.Staged;
		dst.SerializedBytes = src.SerializedBytes;
		dst.MaxBundleBytes = src.MaxBundleBytes;
		dst.MaxBundleEntities = src.MaxBundleEntities;
		dst.SizedBundles = src.SizedBundles;
		dst.SizeFailures = src.SizeFailures;
		dst.SerializeMs = src.SerializeMs;
		StubInfo[] stubs = _stubs;
		lock (Sync)
		{
			for (int i = 0; i < 2; i++)
			{
				DirAcc acc = w.Dir[i];
				Direction dir = (Direction)i;
				DirectionTotals totals = pw.Dir[i];
				totals.Buffers = acc.Totals.Buffers;
				totals.InlineBytes = acc.Totals.InlineBytes;
				totals.Commands = acc.Totals.Commands;
				totals.ObjectRefs = acc.Totals.ObjectRefs;
				totals.DataPayloadBytes = acc.Totals.DataPayloadBytes;
				totals.MaxTickBytes = acc.Totals.MaxTickBytes;
				totals.TickBytes.AddRange(acc.Totals.TickBytes);
				foreach (var stub in acc.Stubs)
				{
					StubInfo? info = (uint)stub.Key < (uint)stubs.Length ? stubs[stub.Key] : null;
					if (info == null || info.Kind == StubKind.Signal)
					{
						string key = info?.Name ?? $"stub#{stub.Key}";
						Add(pw, dir, "signal", key, info?.Detail ?? "", stub.Value);
					}
				}
				foreach (var data in acc.Data)
				{
					string name;
					int size;
					if (!DataTypes.TryGetValue(data.Key, out var dataType))
					{
						(name, size) = ($"typeId#{data.Key}", 0);
					}
					else
					{
						(name, size) = dataType;
					}
					Add(pw, dir, "data", name, $"{size} B unmanaged, TypeId {data.Key} (process-local, remap by name)", data.Value);
				}
				foreach (var removal in acc.DataRemove)
				{
					string name;
					if (!DataTypes.TryGetValue(removal.Key, out var dataType))
					{
						name = $"typeId#{removal.Key}";
					}
					else
					{
						(name, _) = dataType;
					}
					Add(pw, dir, "data-remove", name, "", removal.Value);
				}
				foreach (var invoke in acc.Invokes)
				{
					Add(pw, dir, "invoke", InvokeName(invoke.Key), "closure fields: " + ClosureFields(invoke.Key.DeclaringType), invoke.Value);
				}
				foreach (var story in acc.Stories)
				{
					Add(pw, dir, "story", Names.GeneratedType(story.Key.FullName), StoryFields(story.Key), story.Value);
				}
				foreach (var finish in acc.StoryFinish)
				{
					Type[] typeArgs = finish.Key.IsGenericType ? finish.Key.GetGenericArguments() : Type.EmptyTypes;
					string result = typeArgs.Length != 0 ? Names.Short(typeArgs[^1].FullName ?? typeArgs[^1].Name) : "?";
					Add(pw, dir, "story-finish", "result " + result, "", finish.Value);
				}
				foreach (var objRef in acc.ObjRefs)
				{
					Add(pw, dir, "objref", objRef.Key == typeof(void) ? "null" : Names.Short(objRef.Key.FullName ?? objRef.Key.Name), "", objRef.Value);
				}
			}
			return pw;
		}
	}

	private static void Add(ProbeWindow pw, Direction dir, string category, string key, string detail, Counter c)
	{
		pw.Rows.Add(new ProbeRow(dir, category, key, c.Count, c.Bytes));
		Surface.Add(category, $"{dir} {key}", detail, c.Count, c.Bytes);
	}

	private static string InvokeName(MethodInfo m) => Names.GeneratedType(m.DeclaringType?.FullName) + "." + Names.Member(m.Name);

	private static string ClosureFields(Type? t)
	{
		if (t == null)
		{
			return "";
		}
		IEnumerable<string> fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			.Select(f => Names.Short(f.FieldType.FullName ?? f.FieldType.Name) + " " + Names.Member(f.Name));
		return string.Join(", ", fields);
	}

	private static string StoryFields(Type sm)
	{
		return StoryFieldsText.GetOrAdd(sm, t =>
		{
			IEnumerable<string> fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				.Where(f => !f.Name.StartsWith("<>", StringComparison.Ordinal))
				.Select(f => Names.Short(f.FieldType.FullName ?? f.FieldType.Name) + " " + Names.Member(f.Name));
			return "state machine fields: " + string.Join(", ", fields);
		});
	}

	public static void Stop()
	{
		_tap = null;
		_commitActive = _bundleActive = _measure = false;
		Timer? timer = Interlocked.Exchange(ref _timer, null);
		if (timer != null)
		{
			timer.Dispose();
			Flush();
			long ticks;
			long s2c;
			long c2s;
			lock (Sync)
			{
				ticks = _totalServerTicks;
				s2c = TotalBytes[0];
				c2s = TotalBytes[1];
			}
			_log.Info($"probe: stopped after {ticks} server ticks; S2C {MpLogExtensions.Bytes(s2c)}, C2S {MpLogExtensions.Bytes(c2s)} inline command bytes; {_measuredCommands} commands metered; {_bundleNo} entity bundles");
		}
	}
}
