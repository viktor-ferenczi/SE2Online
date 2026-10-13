using System;
using System.Globalization;
using System.Numerics;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Sync;

public sealed class OwnBodyFilter
{
	public long Frames;

	public long Blended;

	public long BlendedBig;

	public long Snaps;

	public long Restarts;

	public long Logged;

	public double MaxJumpM;

	public float MaxJumpDeg;

	private readonly IMpLog? _log;

	private bool _has;

	private bool _startFromShown;

	private long _lastMs;

	private long _lastLogMs = -4611686018427387904L;

	private int _suppressed;

	private Vec3D _src;

	private Vec3D _off;

	private Vec3D _shown;

	private Vec3D _vEst;

	private Vec3D _lastJump;

	private long _lastJumpFrame = -10L;

	private Quaternion _srcQ = Quaternion.Identity;

	private Quaternion _offQ = Quaternion.Identity;

	private Quaternion _shownQ = Quaternion.Identity;

	public double TauMs { get; set; } = 80.0;

	public double SnapMeters { get; set; } = 3.0;

	public float SnapDeg { get; set; } = 100f;

	public double MaxOffsetMeters { get; set; } = 10.0;

	public float MaxOffsetDeg { get; set; } = 45f;

	public double LogMeters { get; set; } = 0.25;

	public float LogDeg { get; set; } = 3f;

	public bool Enabled { get; set; } = true;

	public double PassMeters { get; set; } = 0.002;

	public float PassDeg { get; set; } = 0.5f;

	public OwnBodyFilter(IMpLog? log = null)
	{
		_log = log;
	}

	public void Reset()
	{
		_has = false;
		_startFromShown = false;
		_off = default;
		_vEst = default;
		_offQ = Quaternion.Identity;
	}

	public void NoteShown(long now, double x, double y, double z, Quaternion q)
	{
		_shown = new Vec3D(x, y, z);
		_shownQ = Quaternion.Normalize(q);
		_startFromShown = true;
		_lastMs = now;
	}

	public void Rebase(Quaternion delta)
	{
		delta = Quaternion.Normalize(delta);
		_srcQ = Quaternion.Normalize(delta * _srcQ);
		_shownQ = Quaternion.Normalize(delta * _shownQ);
		_offQ = Quaternion.Normalize(delta * _offQ * Quaternion.Conjugate(delta));
	}

	public (double X, double Y, double Z, Quaternion Q) Filter(long now, double x, double y, double z, Quaternion q, Vector3? velocity, Quaternion ownTurn, string source, string? why, bool smoothedSource = false)
	{
		Frames++;
		Vec3D vec3D = new Vec3D(x, y, z);
		q = Quaternion.Normalize(q);
		if (!Enabled)
		{
			_src = (_shown = vec3D);
			_srcQ = (_shownQ = q);
			_has = true;
			_lastMs = now;
			return (X: x, Y: y, Z: z, Q: q);
		}
		if (!_has || now - _lastMs > 1000)
		{
			Restarts++;
			_off = default;
			_offQ = Quaternion.Identity;
			if (_startFromShown && now - _lastMs <= 1000)
			{
				Vec3D off = _shown - vec3D;
				if (off.Length <= SnapMeters)
				{
					_off = off;
				}
				Quaternion quaternion = Quaternion.Normalize(_shownQ * Quaternion.Conjugate(q));
				if (LookMath.AngleDeg(quaternion, Quaternion.Identity) <= SnapDeg)
				{
					_offQ = quaternion;
				}
				Report(now, off.Length, LookMath.AngleDeg(_shownQ, q), source, why ?? "own body hand-over (seat / smoothed -> this source)", snapped: false);
			}
			_startFromShown = false;
			_has = true;
		}
		else
		{
			_startFromShown = false;
			double num = Math.Clamp(now - _lastMs, 0L, 100L);
			double num2 = Math.Exp((0.0 - num) / TauMs);
			_off *= num2;
			_offQ = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, _offQ, (float)num2));
			if (num > 0.0)
			{
				Vec3D vec3D2 = (vec3D - _src) * (1000.0 / num);
				if (vec3D2.Length < 500.0)
				{
					_vEst += (vec3D2 - _vEst) * 0.25;
				}
			}
			Vec3D vec3D3;
			if (velocity.HasValue)
			{
				Vector3 valueOrDefault = velocity.GetValueOrDefault();
				vec3D3 = new Vec3D(valueOrDefault.X, valueOrDefault.Y, valueOrDefault.Z);
			}
			else
			{
				vec3D3 = _vEst;
			}
			Vec3D vec3D4 = vec3D3;
			Vec3D vec3D5 = _src + vec3D4 * (num / 1000.0);
			Vec3D vec3D6 = vec3D - vec3D5;
			double length = vec3D6.Length;
			Quaternion value = Quaternion.Normalize(_srcQ * ownTurn);
			Quaternion quaternion2 = Quaternion.Normalize(q * Quaternion.Conjugate(value));
			float num3 = LookMath.AngleDeg(quaternion2, Quaternion.Identity);
			bool flag = length > SnapMeters;
			bool flag2 = num3 > SnapDeg;
			if (flag)
			{
				_off = default;
				Snaps++;
			}
			// Raw host samples: every frame's deviation from the velocity prediction is blended (a speed-scaled pass
			// limit, about 0.3 m at a walk, let all of their frame-to-frame noise through). A smoothed timeline (seat,
			// moving deck, fast flight) is already smooth and is what the ship itself is drawn from: blending it made the
			// body trail its seat whenever the ship accelerated, so there only real jumps are blended.
			else if (length > (smoothedSource ? 0.2 + 0.25 * vec3D4.Length * num / 1000.0 : PassMeters))
			{
				_off -= vec3D6;
				Blended++;
				if (_lastJumpFrame == Frames - 1 && (vec3D6 + _lastJump).Length < 0.3 * _lastJump.Length + PassMeters)
				{
					_off -= _lastJump * (1.0 - num2);
					_lastJumpFrame = -10L;
				}
				else
				{
					_lastJump = vec3D6;
					_lastJumpFrame = Frames;
				}
			}
			if (flag2)
			{
				_offQ = Quaternion.Identity;
				Snaps++;
			}
			else if (num3 > PassDeg)
			{
				_offQ = Quaternion.Normalize(_offQ * Quaternion.Conjugate(quaternion2));
				Blended++;
			}
			if (length > MaxJumpM)
			{
				MaxJumpM = length;
			}
			if (num3 > MaxJumpDeg)
			{
				MaxJumpDeg = num3;
			}
			if (length >= LogMeters || num3 >= LogDeg)
			{
				BlendedBig++;
				Report(now, length, num3, source, why, flag | flag2);
			}
		}
		double length2 = _off.Length;
		if (length2 > MaxOffsetMeters)
		{
			_off *= MaxOffsetMeters / length2;
		}
		float num4 = LookMath.AngleDeg(_offQ, Quaternion.Identity);
		if (num4 > MaxOffsetDeg)
		{
			_offQ = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, _offQ, MaxOffsetDeg / num4));
		}
		_src = vec3D;
		_srcQ = q;
		_shown = vec3D + _off;
		_shownQ = Quaternion.Normalize(_offQ * q);
		_lastMs = now;
		return (X: _shown.X, Y: _shown.Y, Z: _shown.Z, Q: _shownQ);
	}

	private void Report(long now, double m, float deg, string source, string? why, bool snapped)
	{
		if (_log != null)
		{
			if (now - _lastLogMs < 1000)
			{
				_suppressed++;
				return;
			}
			Logged++;
			string text = ((_suppressed > 0) ? $" (+{_suppressed} more in the last second)" : "");
			_suppressed = 0;
			_lastLogMs = now;
			IMpLog? log = _log;
			string text2 = $"client: own body: frame discontinuity {m.ToString("0.00", CultureInfo.InvariantCulture)} m / {deg.ToString("0.0", CultureInfo.InvariantCulture)} deg from source '{source}'";
			string obj = ((why != null) ? (" (" + why + ")") : "");
			string text3;
			if (snapped)
			{
				text3 = " -> shown as is (beyond the snap limit)";
			}
			else
			{
				text3 = (Enabled ? " -> blended out" : " -> shown as is (bodyfilter off)");
			}
			log.Info(text2 + obj + text3 + text);
		}
	}

	public string Describe()
	{
		return $"own-body blend {(Enabled ? "on" : "off")}: {Frames} frames, {Blended} blended ({BlendedBig} big), {Snaps} snaps, {Restarts} restarts, largest source jump {MaxJumpM.ToString("0.00", CultureInfo.InvariantCulture)} m / {MaxJumpDeg.ToString("0.0", CultureInfo.InvariantCulture)} deg";
	}
}
