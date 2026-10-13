using System;
using System.Collections.ObjectModel;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Toolbars;
using Keen.VRage.Core.Game.Systems;

namespace Y51_SE2Online.SyncEngine;

internal static class StartingToolbar
{
	public static readonly Guid Survival = new Guid("ff0e68fd-a8bf-45e0-bd5d-43150c275766");

	public static readonly Guid Creative = new Guid("38369453-f304-423e-aa4f-998faab67dd7");

	public static bool IsBlank(ToolbarPerPlayerData toolbar)
	{
		foreach (ObservableCollection<ToolbarTile> toolbarTile in toolbar.ToolbarTiles)
		{
			if (toolbarTile == null)
			{
				continue;
			}
			foreach (ToolbarTile item in toolbarTile)
			{
				if (item != null && !(item is DeselectTile))
				{
					return false;
				}
			}
		}
		return true;
	}

	public static (DefaultToolbarDefinition? Def, string Which) For(Session server, IdentityId identity, DefaultToolbarDefinition? fallback)
	{
		bool flag = false;
		try
		{
			flag = ((IGameMode)server.SessionComponents.TryGet<GameModeSessionComponent>())?.IsSurvival(identity) ?? false;
		}
		catch (Exception)
		{
		}
		if (!(ObjectCodec.FindDefinition(flag ? Survival : Creative, typeof(DefaultToolbarDefinition)) is DefaultToolbarDefinition item))
		{
			return (Def: fallback, Which: "generic default");
		}
		return (Def: item, Which: flag ? "survival" : "creative");
	}
}
