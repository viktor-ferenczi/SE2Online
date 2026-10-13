using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Client.UI.Library;
using Keen.Game2.Client.UI.Overlays;
using Keen.Game2.Client.UI.Shared.SystemNotification;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

public static class HarnessRejoin
{
	private sealed class HarnessNotifications : NotificationsScreenViewModel
	{
		public override IDisposable? StartTimer(TimeSpan interval, Action tick)
		{
			return null;
		}
	}

	private sealed class ListLog : IMpLog
	{
		public string Text = "";

		public void Info(string m)
		{
		}

		public void Warn(string m)
		{
			Text = Text + m + "; ";
		}

		public void Error(string m)
		{
			Text = Text + m + "; ";
		}
	}

	private static volatile bool _keyPending;

	public static int Generation => ClientWorld.Current?.Generation ?? 0;

	public static string MenuStep(bool atMenu, bool transitioning, bool clientUp)
	{
		if (RejoinKey.Override == null)
		{
			RejoinKey.Override = () =>
			{
				bool keyPending = _keyPending;
				_keyPending = false;
				return keyPending;
			};
		}
		ClientWorld clientWorld = ClientWorld.Current ?? throw new InvalidOperationException("no ClientWorld");
		bool value = clientWorld.MenuStep(atMenu, transitioning, clientUp, MpClock.NowMs(), null);
		return $"join #{clientWorld.Generation}, idle {value}, stage {ClientWorld.Progress.Stage}";
	}

	public static void PressKey()
	{
		_keyPending = true;
	}

	public static (object? World, object? Session) Current()
	{
		ClientWorld current = ClientWorld.Current;
		return (World: current, Session: current?.BoundSession);
	}

	public static bool Detached(object session)
	{
		if (session is ClientSession clientSession)
		{
			return clientSession.IsDetached;
		}
		return false;
	}

	public static string Progress()
	{
		return ClientWorld.Progress.ToString();
	}

	public static (bool Ok, string Detail) JoinUi()
	{
		SharedUIComponent sharedUIComponent = (SharedUIComponent)RuntimeHelpers.GetUninitializedObject(typeof(SharedUIComponent));
		LoadingTransitionScreenViewModel screen = (LoadingTransitionScreenViewModel)RuntimeHelpers.GetUninitializedObject(typeof(LoadingTransitionScreenViewModel));
		sharedUIComponent._loadingTransitionScreenViewModel = screen;
		JoinStatusUi joinStatusUi = new JoinStatusUi(new ListLog());
		JoinProgress joinProgress = new JoinProgress();
		long num = 1000000L;
		joinProgress.SetHost("Luca_Skywalker");
		joinProgress.Accepted("Concordia {Loc:Evil}", 20971520L, 6, num, 0L);
		joinProgress.Set(JoinStage.Loading, num);
		joinStatusUi.Apply(sharedUIComponent, joinProgress, num + 83000);
		string name = screen.Name.RawText ?? "", front = screen.FrontText.ToString() ?? "";
		bool ok = name == "Concordia (Loc:Evil)" && front.Contains("Step 2 of 4") && front.Contains("Starting the engine");
		return (Ok: ok, Detail: $"title '{name}', centre '{front}'; " + joinStatusUi.Describe());
	}

	public static (bool Ok, string Detail) ExitGuard()
	{
		EngineMember renderTextureDestroy = HookTargets.RenderTextureDestroy;
		Type type = EngineMembers.Type(renderTextureDestroy) ?? throw new MissingMemberException(renderTextureDestroy.Type);
		FieldInfo fieldInfo = EngineMembers.Field(HookTargets.Reflected.Single((EngineMember m) => m.Id == "render-streaming-copylist"));
		object uninitializedObject = RuntimeHelpers.GetUninitializedObject(type);
		object uninitializedObject2 = RuntimeHelpers.GetUninitializedObject(fieldInfo.FieldType);
		fieldInfo.SetValue(uninitializedObject, uninitializedObject2);
		bool flag = Harmony.GetPatchInfo(EngineMembers.Method(renderTextureDestroy))?.Prefixes.Any((Patch x) => x.PatchMethod.DeclaringType == typeof(RenderExitGuard)) ?? false;
		long abandoned = RenderExitGuard.Abandoned;
		try
		{
			RenderExitGuard.EarlyExitOverride = () => false;
			RenderExitGuard.DestroyPrefix(uninitializedObject);
			if (fieldInfo.GetValue(uninitializedObject) != uninitializedObject2)
			{
				return (Ok: false, Detail: "the copy list was touched outside shutdown");
			}
			RenderExitGuard.EarlyExitOverride = () => true;
			RenderExitGuard.DestroyPrefix(uninitializedObject);
			if (fieldInfo.GetValue(uninitializedObject) != null || RenderExitGuard.Abandoned != abandoned + 1)
			{
				return (Ok: false, Detail: $"not dropped at shutdown (abandoned {RenderExitGuard.Abandoned - abandoned})");
			}
			RenderExitGuard.DestroyPrefix(uninitializedObject);
		}
		finally
		{
			RenderExitGuard.EarlyExitOverride = null;
		}
		return (Ok: flag, Detail: $"Harmony prefix on {type.Name}.Destroy {(flag ? "installed" : "MISSING")}; untouched while running, dropped at shutdown, second call a no-op");
	}

	private static IList Msgs(NotificationsScreenViewModel screen)
	{
		return (IList)typeof(NotificationsScreenViewModel).GetProperty("SystemMessages").GetValue(screen);
	}

	private static NotificationViewModel Msg(NotificationsScreenViewModel screen, int i)
	{
		return (NotificationViewModel)Msgs(screen)[i];
	}

	private static NotificationViewModel? First(NotificationsScreenViewModel screen)
	{
		if (Msgs(screen).Count <= 0)
		{
			return null;
		}
		return Msg(screen, 0);
	}

	public static (bool Ok, string Detail) SkipSlotDropsCommand()
	{
		CommandLayout commandLayout = CommandLayouts.ByName("Keen.Game2.Simulation.WorldObjects.Characters.CharacterRequestSignals+RelativeDampenerSetSignal") ?? throw new InvalidOperationException("no layout for Keen.Game2.Simulation.WorldObjects.Characters.CharacterRequestSignals+RelativeDampenerSetSignal");
		string text = string.Join(",", commandLayout.Translated.Select((KeyValuePair<int, int> kv) => $"{kv.Key}:{kv.Value}"));
		if (!commandLayout.Translated.TryGetValue(1, out var value) || value != 1)
		{
			return (Ok: false, Detail: "layout translation strategies " + text + " (expected arg 1 = Skip)");
		}
		Entity character = EntityAllocator.AllocateStub();
		Entity grid = EntityAllocator.AllocateStub();
		ObjectSpace sp = new ObjectSpace
		{
			Name = "host-client",
			IdOf = (Entity _) => (uint?)null,
			Resolve = (uint id, bool _) => id switch
			{
				6u => grid, 
				5u => character, 
				_ => null, 
			},
			Explain = (uint id) => $" (harness: id {id} unknown)"
		};
		List<(int, Segment)> scratch = new List<(int, Segment)>();
		int bytes = Math.Max(0, commandLayout.PayloadBytes);
		string fail = null;
		ReplicationCommandBuffer cb = new ReplicationCommandBuffer();
		bool flag = SignalCodec.Write(cb, commandLayout, Cmd(99u), sp, scratch, ref fail);
		string text2 = fail ?? "";
		fail = null;
		bool flag2 = SignalCodec.Write(cb, commandLayout, Cmd(6u), sp, scratch, ref fail);
		return (Ok: (!flag && text2.Contains("dropped") && text2.Contains("harness: id 99 unknown")) & flag2, Detail: $"arg strategies {text}; unresolvable target: {(flag ? "WRITTEN (would call the handler with null)" : ("dropped ('" + text2 + "')"))}; resolvable target: {(flag2 ? "written" : ("FAILED " + fail))}");
		WireCommand Cmd(uint target)
		{
			return new WireCommand
			{
				Key = 1,
				Payload = new byte[bytes],
				Refs = 
				{
					WireValue.Ent(5u),
					WireValue.Ent(target)
				}
			};
		}
	}

	public static (bool Ok, string Detail) CharacterGuard()
	{
		MethodInfo methodInfo = EngineMembers.Method(HookTargets.CharacterEntityRemoved);
		bool flag = Harmony.GetPatchInfo(methodInfo)?.Prefixes.Any((Patch x) => x.PatchMethod.DeclaringType == typeof(CharacterRemovalGuard)) ?? false;
		Component _instance = (Component)RuntimeHelpers.GetUninitializedObject(methodInfo.DeclaringType);
		long skipped = CharacterRemovalGuard.Skipped;
		bool flag2 = CharacterRemovalGuard.OnEntityRemovedPrefix(_instance);
		Component component = (Component)RuntimeHelpers.GetUninitializedObject(methodInfo.DeclaringType);
		EngineMembers.Field(HookTargets.Reflected.Single((EngineMember m) => m.Id == "component-entity")).SetValue(component, EntityAllocator.AllocateStub());
		bool flag3 = CharacterRemovalGuard.OnEntityRemovedPrefix(component);
		return (Ok: flag && !flag2 && !flag3 && CharacterRemovalGuard.Skipped == skipped + 2, Detail: $"Harmony prefix on {methodInfo.DeclaringType.Name}.{methodInfo.Name} {(flag ? "installed" : "MISSING")}; component without entity: {(flag2 ? "RAN (would NRE)" : "skipped")}; stub entity: {(flag3 ? "ran" : "skipped")}");
	}
}
