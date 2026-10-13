using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

internal static class HarnessFix083
{
	private sealed class TextLog : IMpLog
	{
		public string Text = "";

		public void Info(string m)
		{
			Text = Text + m + "; ";
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

	public static (bool Ok, string Detail) StringIdCheck()
	{
		CommandLayout commandLayout = CommandLayouts.ByName("Keen.VRage.Game.Components.Fractures.BreakableComponent+OnFractureIntegrityChangedSignal");
		List<string> list = (from x in CommandLayouts.All
			where x != null && x.Forwardable && x.StringIdOffsets.Length != 0
			select ClientUpstream.Short(x.Name)).ToList();
		bool flag = commandLayout != null && commandLayout.Forwardable && commandLayout.StringIdOffsets.Length >= 1 && commandLayout.PayloadBytes > commandLayout.StringIdOffsets[0];
		if (commandLayout == null || !flag)
		{
			return (Ok: false, Detail: "fracture layout: " + ((commandLayout == null) ? "NOT FOUND" : $"offsets [{string.Join(",", commandLayout.StringIdOffsets)}], {commandLayout.PayloadBytes} B, why {commandLayout.Why}"));
		}
		StringIdTable stringIdTable = new StringIdTable();
		StringId stringId = StringId.Get("y51online_harness_Fracture_15");
		byte[] array = new byte[commandLayout.PayloadBytes];
		BinaryPrimitives.WriteInt32LittleEndian(array.AsSpan(commandLayout.StringIdOffsets[0]), stringId.Id);
		stringIdTable.NoteHost(array, commandLayout.StringIdOffsets);
		stringIdTable.NoteHost(array, commandLayout.StringIdOffsets);
		List<string> list2 = stringIdTable.TakePending();
		bool flag2 = list2 != null && list2.Count == 1 && list2[0] == StringIdTable.Entry(stringId.Id, "y51online_harness_Fracture_15");
		StringIdTable stringIdTable2 = new StringIdTable();
		bool flag3 = stringIdTable2.TryRegister(StringIdTable.Entry(987654, "y51online_harness_Fracture_15")) && !stringIdTable2.TryRegister("SomeStringHashName");
		byte[] array2 = new byte[commandLayout.PayloadBytes];
		BinaryPrimitives.WriteInt32LittleEndian(array2.AsSpan(commandLayout.StringIdOffsets[0]), 987654);
		byte[] array3 = stringIdTable2.Map(array2, commandLayout.StringIdOffsets, toLocal: true);
		bool flag4 = array3 != null && BinaryPrimitives.ReadInt32LittleEndian(array3.AsSpan(commandLayout.StringIdOffsets[0])) == stringId.Id;
		byte[] array4 = ((array3 != null) ? stringIdTable2.Map(array3, commandLayout.StringIdOffsets, toLocal: false) : null);
		bool flag5 = array4 != null && BinaryPrimitives.ReadInt32LittleEndian(array4.AsSpan(commandLayout.StringIdOffsets[0])) == 987654;
		BinaryPrimitives.WriteInt32LittleEndian(array2.AsSpan(commandLayout.StringIdOffsets[0]), 424242);
		bool flag6 = stringIdTable2.Map(array2, commandLayout.StringIdOffsets, toLocal: true) == null;
		return (Ok: flag2 & flag3 & flag4 & flag5 & flag6, Detail: $"fracture signal: StringId at payload offset {commandLayout.StringIdOffsets[0]} of {commandLayout.PayloadBytes} B; announced once by text {flag2}, host 987654 -> own {stringId.Id} {flag4}, back {flag5}, unannounced dropped {flag6}; {list.Count} forwardable signals carry StringIds" + ((list.Count > 0) ? (" (e.g. " + string.Join(", ", list.Take(6)) + ")") : ""));
	}

	public static (bool Ok, string Detail) SlowTickCheck()
	{
		TextLog textLog = new TextLog();
		CommandLayout commandLayout = CommandLayouts.ByName("Keen.VRage.Game.Components.Fractures.BreakableComponent+OnFractureIntegrityChangedSignal");
		if (commandLayout == null)
		{
			return (Ok: false, Detail: "no fracture layout");
		}
		bool enabled = SlowTicks.Enabled;
		IMpLog log = SlowTicks.Log;
		SlowTicks.Enabled = true;
		SlowTicks.Log = textLog;
		long slow = SlowTicks.Slow;
		SlowTicks.Begin();
		for (int i = 0; i < 3; i++)
		{
			SlowTicks.Record(commandLayout.Stub, Stopwatch.Frequency / 10);
		}
		SlowTicks.Record(commandLayout.Stub + 1, Stopwatch.Frequency / 1000);
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.ElapsedMilliseconds < 60)
		{
		}
		SlowTicks.End();
		SlowTicks.Begin();
		SlowTicks.End();
		long loadingTicks = SlowTicks.LoadingTicks;
		SlowTicks.Begin();
		SlowTicks.Note = "world part 1 build (8444 entities)";
		stopwatch.Restart();
		while (stopwatch.ElapsedMilliseconds < 60)
		{
		}
		SlowTicks.End();
		bool flag = SlowTicks.LoadingTicks == loadingTicks + 1 && textLog.Text.Contains("long tick") && textLog.Text.Contains("world part 1 build");
		string text = textLog.Text;
		SlowTicks.Enabled = enabled;
		SlowTicks.Log = log;
		return (Ok: (SlowTicks.Slow == slow + 1 && text.Contains("SLOW TICK") && text.Contains("OnFractureIntegrityChangedSignal x3 300.0 ms") && text.Contains("in 4 commands")) & flag, Detail: text.Trim());
	}
}
