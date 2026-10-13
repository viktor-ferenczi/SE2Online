using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Keen.Game2.Client.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

public static class HarnessSteamJoin
{
	public static (bool Ok, string Detail) NametagProvider()
	{
		Entity entity = EntityAllocator.AllocateStub();
		entity.Components = ImmutableArray.Create(new Component());
		NametagMarkerProvider nametagMarkerProvider = new NametagMarkerProvider();
		EngineMembers.Field(HookTargets.Reflected.Single((EngineMember m) => m.Id == "component-entity")).SetValue(nametagMarkerProvider, entity);
		entity.Components = entity.Components.Add(nametagMarkerProvider);
		int num = 0;
		foreach (IGPSMarkersProvider item in entity.Components.OfType<IGPSMarkersProvider>())
		{
			num += ((item == nametagMarkerProvider) ? 1 : 100);
		}
		if (num != 1)
		{
			return (Ok: false, Detail: $"Components.OfType<IGPSMarkersProvider> found {num}");
		}
		if (!entity.Has(out IGPSMarkersProvider feature, (Func<IGPSMarkersProvider, bool>?)null) || feature != nametagMarkerProvider || nametagMarkerProvider.Entity != entity)
		{
			return (Ok: false, Detail: "Entity.Has / provider.Entity mismatch");
		}
		int sessionComps = 0;
		entity.ForEach((SessionComponent _) =>
		{
			sessionComps++;
		});
		if (sessionComps != 0)
		{
			return (Ok: false, Detail: $"ForEach<SessionComponent> saw {sessionComps}");
		}
		GPSMarker gPSMarker = new GPSMarker(null, "Iqhawe", "Y51_SE2Online player", Vector3D.Zero, default, new ColorSRGB(140, 210, 255), alwaysShow: true, hidden: false, removeWithEntity: false, "Y51_SE2Online.Nametag", showOnMap: false);
		gPSMarker.Position = new Vector3D(1.0, 2.0, 3.0);
		gPSMarker.Hidden = true;
		gPSMarker.Hidden = false;
		gPSMarker.Name = "Josh";
		ObservableList<GPSMarker> current2 = new ObservableList<GPSMarker> { gPSMarker };
		nametagMarkerProvider.Current = current2;
		ReadOnlySpan<GPSMarker> readOnlySpan = ((IGPSMarkersProvider)nametagMarkerProvider).Markers.AsSpan();
		if (readOnlySpan.Length != 1 || readOnlySpan[0].Position != new Vector3D(1.0, 2.0, 3.0) || readOnlySpan[0].Name != "Josh" || readOnlySpan[0].Hidden)
		{
			return (Ok: false, Detail: "marker list / properties not as set");
		}
		string text;
		try
		{
			Type type = typeof(Entity).Assembly.GetType("Keen.VRage.DCS.Internal.RuntimeComponentInfo", throwOnError: true);
			object obj = type.GetMethod("For", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[1] { typeof(NametagMarkerProvider) });
			text = ((obj == null) ? "RuntimeComponentInfo: null" : $"RuntimeComponentInfo built ({type.GetProperty("DisplayName")?.GetValue(obj) ?? type.GetField("DisplayName")?.GetValue(obj)})");
		}
		catch (TargetInvocationException ex)
		{
			text = $"RuntimeComponentInfo not available in this metadata context ({ex.InnerException?.GetType().Name}: {ex.InnerException?.Message})";
		}
		return (Ok: true, Detail: "provider found by the renderer's lookup, skipped by session-component walks; marker moved/renamed/hidden outside DCS; " + text);
	}
}
