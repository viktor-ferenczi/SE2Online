using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.Game2.Client.GameSystems.GPS;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

internal sealed class NametagOverlay
{
	private sealed class Tag
	{
		public GPSMarker Marker;

		public string Name;

		public Entity? Target;
	}

	public static bool Enabled = true;

	public static double Range = 200.0;

	public const double HeadOffset = 2.1;

	public const string MarkerIdentifier = "Y51_SE2Online.Nametag";

	private static readonly FieldInfo EntityBackingField = EngineMembers.Field(HookTargets.Reflected.Single((EngineMember m) => m.Id == "component-entity"));

	private static readonly FieldInfo GpsConfigField = EngineMembers.Field(HookTargets.Reflected.Single((EngineMember m) => m.Id == "gps-render-config"));

	private readonly IMpLog _log;

	private readonly string _side;

	private readonly Dictionary<ushort, Tag> _tags = new Dictionary<ushort, Tag>();

	private volatile Tag[] _active = Array.Empty<Tag>();

	private static readonly List<WeakReference<NametagOverlay>> Overlays = new List<WeakReference<NametagOverlay>>();

	public static long DrawRefreshes;

	private static int _drawFailed;

	private Entity? _sessionEntity;

	private NametagMarkerProvider? _provider;

	private GPSMarkerTypeDefinition? _type;

	private object? _styleConfig;

	private bool _failed;

	private string _lastShown = "";

	public long Updates;

	public NametagOverlay(IMpLog log, string side)
	{
		_log = log;
		_side = side;
		lock (Overlays)
		{
			Overlays.RemoveAll((WeakReference<NametagOverlay> w) => !w.TryGetTarget(out NametagOverlay _));
			Overlays.Add(new WeakReference<NametagOverlay>(this));
		}
	}

	public static void DrawPrefix(Keen.Game2.Client.GameSystems.GPS.GPSMarkerRenderSessionComponent __instance)
	{
		try
		{
			MarkerColors.ApplyToAntennas(__instance);
		}
		catch (Exception)
		{
		}
		if (!Enabled || _drawFailed != 0)
		{
			return;
		}
		try
		{
			lock (Overlays)
			{
				foreach (WeakReference<NametagOverlay> overlay in Overlays)
				{
					if (overlay.TryGetTarget(out var target) && !target._failed)
					{
						target.PlacePositions();
					}
				}
			}
			DrawRefreshes++;
		}
		catch (Exception)
		{
			_drawFailed = 1;
		}
	}

	private void PlacePositions()
	{
		Tag[] active = _active;
		foreach (Tag tag in active)
		{
			Entity target = tag.Target;
			if (target != null && !EngineCodec.IsDead(target))
			{
				Vector3D value = HeadPosition(target);
				if (tag.Marker.Position != value)
				{
					tag.Marker.Position = value;
				}
			}
		}
	}

	private static Vector3D HeadPosition(Entity e)
	{
		WorldTransform worldTransform = e.Data.GetWorldTransform();
		Vector3 up = Quaternion.GetUp(worldTransform.Orientation);
		return worldTransform.Position + new Vector3D(up.X, up.Y, up.Z) * 2.1;
	}

	public static void ResetAll()
	{
		lock (Overlays)
		{
			foreach (var entry in Overlays)
			{
				if (!entry.TryGetTarget(out var overlay)) continue;
				overlay._active = Array.Empty<Tag>();
				if (overlay._provider != null) overlay._provider.Current = new ObservableList<GPSMarker>();
			}
			Overlays.Clear();
		}
	}

	public static void Configure(MpOptions o)
	{
		Enabled = o.Nametags;
		Range = o.NametagRange;
	}

	public void Update(Session session, Entity sessionEntity, IReadOnlyList<(ushort ClientId, string Name, Entity? Target)> players)
	{
		if (_failed || !Enabled)
		{
			return;
		}
		try
		{
			UpdateCore(session, sessionEntity, players);
		}
		catch (Exception value)
		{
			_failed = true;
			_log.Error($"nametags({_side}): failed and switched off for this session (the game is unaffected): {value}");
			try
			{
				if (_provider != null)
				{
					_provider.Current = new ObservableList<GPSMarker>();
				}
			}
			catch (Exception)
			{
			}
		}
	}

	private void UpdateCore(Session session, Entity sessionEntity, IReadOnlyList<(ushort ClientId, string Name, Entity? Target)> players)
	{
		if (sessionEntity != _sessionEntity)
		{
			if (players.Count == 0)
			{
				return;
			}
			Attach(session, sessionEntity);
			if (_failed)
			{
				return;
			}
		}
		Updates++;
		bool flag = false;
		HashSet<ushort> seen = new HashSet<ushort>();
		foreach (var (num, text, entity) in players)
		{
			if (entity != null && !EngineCodec.IsDead(entity))
			{
				seen.Add(num);
				if (!_tags.TryGetValue(num, out Tag value))
				{
					GPSMarkersConfiguration gPSMarkersConfiguration = (GPSMarkersConfiguration)_styleConfig;
					GPSMarker marker = new GPSMarker(_type, text, "Y51_SE2Online player", entity.Data.GetWorldTransform().Position, PlayerIcon.Get(gPSMarkersConfiguration.DefaultIcon), new ColorSRGB(140, 210, 255), alwaysShow: true, hidden: false, removeWithEntity: false, "Y51_SE2Online.Nametag", showOnMap: false);
					Dictionary<ushort, Tag> tags = _tags;
					ushort key = num;
					Tag tag = new Tag
					{
						Marker = marker,
						Name = text
					};
					value = tag;
					tags[key] = tag;
					flag = true;
				}
				else if (value.Name != text)
				{
					value.Name = text;
					value.Marker.Name = text;
				}
				value.Target = entity;
				value.Marker.Color = MarkerColors.For(TeamPermissions.OwnerOf(entity)) ?? new ColorSRGB(140, 210, 255);
			}
		}
		foreach (ushort item in _tags.Keys.Where((ushort k) => !seen.Contains(k)).ToList())
		{
			_tags.Remove(item);
			flag = true;
		}
		if (flag)
		{
			ObservableList<GPSMarker> observableList = new ObservableList<GPSMarker>();
			foreach (Tag value3 in _tags.Values)
			{
				observableList.Add(value3.Marker);
			}
			_provider.Current = observableList;
			string text2 = string.Join(", ", from kv in _tags
				orderby kv.Key
				select $"'{kv.Value.Name}' (ClientId {kv.Key})");
			if (text2 != _lastShown)
			{
				_lastShown = text2;
				_log.Info($"nametags({_side}): showing {_tags.Count} tag(s){((_tags.Count > 0) ? (": " + text2) : "")} (within {Range:0} m)");
			}
		}
		if (flag || _active.Length != _tags.Count)
		{
			_active = _tags.Values.ToArray();
		}
		if (_tags.Count == 0)
		{
			return;
		}
		Vector3D? vector3D = null;
		try
		{
			PlayerControllerComponent playerControllerComponent = session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController;
			if (playerControllerComponent != null)
			{
				vector3D = GPSMarkerHelpers.GetAnchorPosition(playerControllerComponent);
			}
		}
		catch (Exception)
		{
		}
		foreach (Tag value4 in _tags.Values)
		{
			Entity target = value4.Target;
			if (target == null || EngineCodec.IsDead(target))
			{
				value4.Marker.Hidden = true;
				continue;
			}
			Vector3D value2 = HeadPosition(target);
			// CommitFrame is not ordered after Physics.RigidPositionUpdate (seated pilots re-attach there) and races the HUD draw.
			if (_drawFailed != 0 || DrawRefreshes == 0)
			{
				value4.Marker.Position = value2;
			}
			bool flag2 = vector3D.HasValue && Vector3D.Distance(vector3D.GetValueOrDefault(), in value2) > Range;
			if (value4.Marker.Hidden != flag2)
			{
				value4.Marker.Hidden = flag2;
			}
		}
	}

	private void Attach(Session session, Entity sessionEntity)
	{
		_sessionEntity = sessionEntity;
		_tags.Clear();
		_lastShown = "";
		GPSMarkerRenderSessionComponent gPSMarkerRenderSessionComponent = session.SessionComponents.TryGet<GPSMarkerRenderSessionComponent>();
		if (gPSMarkerRenderSessionComponent == null)
		{
			_failed = true;
			_log.Warn("nametags(" + _side + "): this client session has no GPSMarkerRenderSessionComponent (HUD markers); nametags off");
			return;
		}
		if (!(GpsConfigField.GetValue(gPSMarkerRenderSessionComponent) is GPSMarkersConfiguration gPSMarkersConfiguration))
		{
			_failed = true;
			_log.Warn("nametags(" + _side + "): the HUD marker renderer has no GPSMarkersConfiguration yet; nametags off");
			return;
		}
		_styleConfig = gPSMarkersConfiguration;
		_type = null;
		using (HashSet<GPSMarkerTypeDefinition>.Enumerator enumerator = gPSMarkersConfiguration.FullDisplayMarkerTypes.GetEnumerator())
		{
			if (enumerator.MoveNext())
			{
				GPSMarkerTypeDefinition current = enumerator.Current;
				_type = current;
			}
		}
		if (_type == null)
		{
			_type = gPSMarkersConfiguration.PlayerMadeMarkerType;
		}
		NametagMarkerProvider nametagMarkerProvider = sessionEntity.Components.OfType<NametagMarkerProvider>().FirstOrDefault();
		if (nametagMarkerProvider != null)
		{
			_provider = nametagMarkerProvider;
		}
		else
		{
			NametagMarkerProvider nametagMarkerProvider2 = new NametagMarkerProvider();
			EntityBackingField.SetValue(nametagMarkerProvider2, sessionEntity);
			sessionEntity.Components = sessionEntity.Components.Add(nametagMarkerProvider2);
			_provider = nametagMarkerProvider2;
		}
		_log.Info($"nametags({_side}): HUD marker provider attached to the client session ({sessionEntity.Components.Length} components; marker type '{_type?.Name ?? "?"}'); tags show other players' names within {Range:0} m");
	}
}
