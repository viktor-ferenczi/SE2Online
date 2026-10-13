using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class HostStories
{
	internal sealed class Entry
	{
		public PeerId Peer;

		public uint Id;

		public string Name;

		public NetworkStoryContext Context;

		public long LastMs;

		public int Moves;
	}

	private sealed class HostStoryNarrator : INetworkStoryNarrator
	{
		private readonly HostStories _owner;

		private readonly Entry _entry;

		private readonly IStorySink _host;

		private readonly ObjectSpace _space;

		public HostStoryNarrator(HostStories owner, Entry entry, IStorySink host, ObjectSpace space)
		{
			_owner = owner;
			_entry = entry;
			_host = host;
			_space = space;
		}

		public void Start<TResult>(NetworkStoryContext context, IAsyncStateMachine stateMachine)
		{
			stateMachine.MoveNext();
		}

		public unsafe void MoveToServer(NetworkStoryContext context, IAsyncStateMachine stateMachine, ServerRequest server)
		{
			//IL_002a: Expected native int or pointer, but got VT
			server.Result = new ReplicationEventContext
			{
				IsLocal = true,
				Session = _space.Session,
				SourceId = new ClientId(0)
			};
			stateMachine.MoveNext();
		}

		public void MoveToClient(NetworkStoryContext context, IAsyncStateMachine stateMachine, ClientRequest client)
		{
			_space.ClearLocalIds();
			_space.Observed.Clear();
			List<(string, WireValue)> list = StoryState.Encode(stateMachine, _space, out string fail);
			if (list != null && _space.LocalIds.Count > 0)
			{
				list = null;
				fail = "refers to an entity the joiner does not have";
			}
			if (list == null)
			{
				_owner.Done(_entry);
				_owner.Fail(_host, _entry.Peer, _entry.Id, _entry.Name, "cannot travel back: " + fail);
				return;
			}
			_owner.MovedToClient++;
			_owner.Roots(_entry.Peer, _space);
			_entry.LastMs = MpClock.NowMs();
			_host.SendStoryMove(_entry.Peer, new StoryMove
			{
				StoryId = _entry.Id,
				StateMachineType = ObjectCodec.TypeName(stateMachine.GetType()),
				Fields = list
			});
		}

		public void Finish<TResult>(NetworkStoryContext context, TResult result, Exception? exception)
		{
			_owner.Done(_entry);
			_owner.Finished++;
			string text = ((exception == null) ? ("ok" + StoryState.Describe(result)) : exception.GetType().Name);
			if (_owner.FirstOf("finished " + _entry.Name + ((exception == null) ? "" : (" " + text))) | (_owner.Finished <= 20))
			{
				_owner._log.Info($"host world sync: STORY {_entry.Name} #{_entry.Id} finished on the server ({text}), result to {_entry.Peer}");
			}
			StoryFinish storyFinish = new StoryFinish
			{
				StoryId = _entry.Id
			};
			if (exception != null)
			{
				storyFinish.Status = (byte)((!(exception is Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException)) ? 1 : 2);
				storyFinish.Text = exception.GetType().Name + ": " + exception.Message;
			}
			else if (result != null && !(result is DBNull))
			{
				string fail = null;
				_space.ClearLocalIds();
				_space.Observed.Clear();
				storyFinish.Result = ObjectCodec.Encode(result, typeof(TResult), _space, ref fail);
				if (fail == null && _space.LocalIds.Count > 0)
				{
					_owner.Defer(_entry, result, typeof(TResult));
					return;
				}
				if (fail != null)
				{
					storyFinish.Status = 2;
					storyFinish.Text = "result cannot travel: " + fail;
				}
				else
				{
					_owner.Roots(_entry.Peer, _space);
				}
			}
			_host.SendStoryFinish(_entry.Peer, storyFinish);
		}
	}

	private readonly IMpLog _log;

	private readonly ConcurrentQueue<(PeerId Peer, ushort ClientId, StoryMove Move)> _in = new ConcurrentQueue<(PeerId, ushort, StoryMove)>();

	private readonly ConcurrentQueue<PeerId> _forget = new ConcurrentQueue<PeerId>();

	private readonly ConcurrentQueue<(Entry Entry, object? Result, Type Type, long SinceMs)> _deferred = new ConcurrentQueue<(Entry, object, Type, long)>();

	public const int ResultWaitMs = 3000;

	private readonly ConcurrentDictionary<(PeerId, uint), Entry> _entries = new ConcurrentDictionary<(PeerId, uint), Entry>();

	private readonly ConcurrentDictionary<string, long> _counts = new ConcurrentDictionary<string, long>();

	public long Received;

	public long Started;

	public long Resumed;

	public long MovedToClient;

	public long Finished;

	public long Failed;

	public long ReleasedNoOps;

	private readonly ConcurrentDictionary<string, byte> _logged = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

	public Func<ushort, ushort>? SourceFor;

	public Action<PeerId, IReadOnlyList<uint>>? RootsFor;

	public Func<ushort, object, Session?, string?>? Gate;

	public long Refused;

	public const int IdleForgetMs = 300000;

	private long _lastPurgeMs;

	public bool Pending
	{
		get
		{
			if (_in.IsEmpty && _forget.IsEmpty)
			{
				return !_deferred.IsEmpty;
			}
			return true;
		}
	}

	internal bool FirstOf(string key)
	{
		return _logged.TryAdd(key, 0);
	}

	public HostStories(IMpLog log)
	{
		_log = log;
	}

	internal void Roots(PeerId peer, ObjectSpace space)
	{
		if (space.Observed.Count > 0)
		{
			RootsFor?.Invoke(peer, space.Observed.ToList());
		}
	}

	public void Count(string key)
	{
		_counts.AddOrUpdate(key, 1L, (string _, long c) => c + 1);
	}

	public void Enqueue(PeerId peer, ushort clientId, StoryMove move)
	{
		Received++;
		_in.Enqueue((peer, clientId, move));
	}

	public void Forget(PeerId peer)
	{
		_forget.Enqueue(peer);
	}

	internal void Defer(Entry e, object? result, Type type)
	{
		_deferred.Enqueue((e, result, type, MpClock.NowMs()));
	}

	internal void RetryDeferred(IStorySink host, ObjectSpace space)
	{
		int count = _deferred.Count;
		long num = MpClock.NowMs();
		(Entry, object, Type, long) result;
		for (int i = 0; i < count && _deferred.TryDequeue(out result); i++)
		{
			string fail = null;
			space.ClearLocalIds();
			space.Observed.Clear();
			WireValue result2 = ObjectCodec.Encode(result.Item2, result.Item3, space, ref fail);
			if (fail == null && space.LocalIds.Count > 0)
			{
				if (num - result.Item4 < 3000)
				{
					_deferred.Enqueue(result);
					continue;
				}
				fail = "refers to entities the joiner does not have";
			}
			if (fail == null)
			{
				Roots(result.Item1.Peer, space);
			}
			host.SendStoryFinish(result.Item1.Peer, (fail == null) ? new StoryFinish
			{
				StoryId = result.Item1.Id,
				Result = result2
			} : new StoryFinish
			{
				StoryId = result.Item1.Id,
				Status = 2,
				Text = "result cannot travel: " + fail
			});
		}
	}

	public void Drain(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent hostClient, Func<IStorySink?> host, Func<ObjectSpace> serverSpace, Session serverSession, bool serverOnly = false)
	{
		Entry value;
		while (true)
		{
			if (!_forget.TryDequeue(out var gone))
			{
				break;
			}
			foreach (var item in _entries.Keys.Where(((PeerId, uint) k) => k.Item1 == gone).ToList())
			{
				_entries.TryRemove(item, out value);
			}
		}
		long now = MpClock.NowMs();
		if (now - _lastPurgeMs > 1000)
		{
			_lastPurgeMs = now;
			foreach (var item2 in (from kv in _entries
				where now - kv.Value.LastMs > 300000
				select kv.Key).ToList())
			{
				_entries.TryRemove(item2, out value);
			}
		}
		(PeerId, ushort, StoryMove) result;
		while (_in.TryDequeue(out result))
		{
			(PeerId Peer, ushort ClientId, StoryMove Move) it = result;
			ushort source = SourceFor?.Invoke(it.ClientId) ?? 1;
			if (serverOnly)
			{
				Run(it.Peer, it.Move, host(), serverSpace(), serverSession, source, it.ClientId);
				continue;
			}
			hostClient.PostInvoke(delegate
			{
				Run(it.Peer, it.Move, host(), serverSpace(), serverSession, source, it.ClientId);
			});
		}
		if (!_deferred.IsEmpty)
		{
			IStorySink storySink = host();
			if (storySink != null)
			{
				RetryDeferred(storySink, serverSpace());
			}
		}
	}

	internal void Run(PeerId peer, StoryMove move, IStorySink? host, ObjectSpace space, Session? serverSession, ushort sourceClientId = 1, ushort clientId = 0)
	{
		if (host == null)
		{
			return;
		}
		string text = StoryState.Name(move.StateMachineType);
		Entry value2;
		try
		{
			(PeerId, uint) key = (peer, move.StoryId);
			if (!_entries.TryGetValue(key, out Entry value))
			{
				Type type = ObjectCodec.FindType(move.StateMachineType);
				FieldInfo fieldInfo = ((type == null) ? null : ObjectCodec.Fields(type).FirstOrDefault((FieldInfo f) => StoryState.IsBuilder(f.FieldType)));
				if (fieldInfo == null)
				{
					Fail(host, peer, move.StoryId, text, "no state machine type " + move.StateMachineType + " / builder here");
					return;
				}
				NetworkStoryContext networkStoryContext = (NetworkStoryContext)Activator.CreateInstance(fieldInfo.FieldType);
				value = new Entry
				{
					Peer = peer,
					Id = move.StoryId,
					Name = text,
					Context = networkStoryContext
				};
				networkStoryContext.Narrator = new HostStoryNarrator(this, value, host, space);
				_entries[key] = value;
				Started++;
			}
			else
			{
				Resumed++;
			}
			value.LastMs = MpClock.NowMs();
			bool flag = value.Moves++ == 0;
			space.ClearLocalIds();
			space.LocalStubs.Clear();
			object obj = StoryState.Decode(move, space, value.Context, MakeRequest, out string fail);
			if (obj == null)
			{
				_entries.TryRemove(key, out value2);
				if (fail != null && ObjectCodec.IsUnknownModel(fail))
				{
					AlreadyReleased(host, peer, move.StoryId, text, value.Context, fail);
				}
				else
				{
					Fail(host, peer, move.StoryId, text, fail ?? "?");
				}
				return;
			}
			{
				string text2 = Gate?.Invoke(clientId, obj, serverSession);
				if (text2 != null)
				{
					_entries.TryRemove(key, out value2);
					Refused++;
					Count(text + (text2.StartsWith("passenger", StringComparison.Ordinal) ? ": refused (passenger seat)" : ": refused (strict grids)"));
					_log.Info($"host world sync: STORY {text} #{move.StoryId} from {peer} refused: {text2}");
					host.SendStoryFinish(peer, new StoryFinish
					{
						StoryId = move.StoryId,
						Status = 2,
						Text = "not allowed: " + text2
					});
					return;
				}
			}
			Count(text);
			if (flag && AudioRouting.IsBuildStory(text))
			{
				AudioRouting.Instance.NoteJoinerBuild((clientId != 0) ? clientId : sourceClientId, MpClock.NowMs());
			}
			if (FirstOf("run " + text) | (Started + Resumed <= 20))
			{
				_log.Info($"host world sync: STORY {text} #{move.StoryId} from {peer} runs on the server as ClientId {sourceClientId}");
			}
			((IAsyncStateMachine)obj).MoveNext();
		}
		catch (Exception ex)
		{
			_entries.TryRemove((peer, move.StoryId), out value2);
			Fail(host, peer, move.StoryId, text, ex.GetType().Name + ": " + ex.Message);
		}
		ServerRequest MakeRequest()
		{
			//IL_002f: Expected native int or pointer, but got VT
			ServerRequest serverRequest = new ServerRequest();
			serverRequest.Result = new ReplicationEventContext
			{
				IsLocal = false,
				Session = serverSession,
				SourceId = new ClientId(sourceClientId)
			};
			return serverRequest;
		}
	}

	private void Fail(IStorySink host, PeerId peer, uint id, string name, string why)
	{
		Failed++;
		Count(name + ": failed");
		_log.WarnOnce("host-story-" + name + StoryState.Shape(why), $"host world sync: story {name} from {peer} cannot run here: {why} (further ones of this kind only counted)");
		host.SendStoryFinish(peer, new StoryFinish
		{
			StoryId = id,
			Status = 2,
			Text = why
		});
	}

	private void AlreadyReleased(IStorySink host, PeerId peer, uint id, string name, NetworkStoryContext context, string why)
	{
		ReleasedNoOps++;
		bool flag = StoryState.ResultType(context) == typeof(DBNull);
		Count(name + ": model already released (no-op)");
		if (FirstOf("released " + name))
		{
			_log.Info($"host world sync: STORY {name} #{id} from {peer} names a model this host already released ({why}); {(flag ? "nothing to do, answered as done" : "skipped, as Keen does")} (further ones only counted)");
		}
		host.SendStoryFinish(peer, flag ? new StoryFinish
		{
			StoryId = id
		} : new StoryFinish
		{
			StoryId = id,
			Status = 2,
			Text = "model already released: " + why
		});
	}

	internal void Done(Entry e)
	{
		_entries.TryRemove((e.Peer, e.Id), out Entry _);
	}

	public string Describe()
	{
		return $"stories: received {Received}, started {Started}, resumed {Resumed}, moved to client {MovedToClient}, finished {Finished}, failed {Failed}, released-model no-ops {ReleasedNoOps}" + (_counts.IsEmpty ? "" : ("; " + string.Join(", ", from kv in _counts.OrderByDescending((KeyValuePair<string, long> kv) => kv.Value).Take(8)
			select $"{kv.Key} x{kv.Value}")));
	}
}
