using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Keen.Game2.Simulation.GameSystems.Toolbars;

namespace Y51_SE2Online.SyncEngine;

internal static class HarnessFix084
{
	public static (bool Ok, string Detail) StartingToolbarCheck()
	{
		ToolbarPerPlayerData toolbarPerPlayerData = (ToolbarPerPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ToolbarPerPlayerData));
		ObservableCollection<ToolbarTile> observableCollection = new ObservableCollection<ToolbarTile>
		{
			null,
			(DeselectTile)RuntimeHelpers.GetUninitializedObject(typeof(DeselectTile))
		};
		toolbarPerPlayerData.ToolbarTiles = new ObservableCollection<ObservableCollection<ToolbarTile>> { observableCollection };
		bool flag = StartingToolbar.IsBlank(toolbarPerPlayerData);
		observableCollection.Add((ToolbarTile)RuntimeHelpers.GetUninitializedObject(typeof(ToolToolbarTile)));
		bool flag2 = !StartingToolbar.IsBlank(toolbarPerPlayerData);
		return (Ok: flag & flag2, Detail: $"empty/deselect slots -> blank {flag}; with a tool tile -> kept {flag2}; survival default {StartingToolbar.Survival}, creative {StartingToolbar.Creative}");
	}
}
