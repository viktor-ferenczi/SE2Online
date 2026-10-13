using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Sync;

public sealed class LookPredictor
{
	public const double MinLagMs = 30.0;

	public const double MaxLagMs = 350.0;

	public long LagSamples;

	private readonly IMpLog _log;

	private readonly (long T, Quaternion Q)[] _hist = new (long, Quaternion)[192];

	private int _histHead;

	private int _histCount;

	private readonly Queue<long> _snaps = new Queue<long>();

	private Quaternion _p = Quaternion.Identity;

	private bool _hasP;

	private double _hx;

	private double _hy;

	private double _hz;

	private Quaternion _ho = Quaternion.Identity;

	private bool _hasHost;

	private bool _flushHost;

	private Vector3 _com;

	private LookMode _mode;

	private long _settleUntil;

	private long _lastTurnMs = -2305843009213693952L;

	public double TotalX;

	public double TotalY;

	public double TotalZ;

	public long Steps;

	public long Turns;

	public long HostSamples;

	public long Agreed;

	public long Blended;

	public long Snapped;

	public long Adopted;

	public long Waiting;

	public long ModeChanges;

	public long Written;

	public float LastErrDeg;

	public float MaxBlendDeg;

	public float AgreeDeg { get; set; } = 0.01f;

	public float DeadbandDeg { get; set; } = 0.25f;

	public float BlendMin { get; set; } = 0.1f;

	public float BlendMax { get; set; } = 0.6f;

	public float SnapDeg { get; set; } = 25f;

	public float SettleSnapDeg { get; set; } = 90f;

	public int SettleMs { get; set; } = 600;

	public int HistoryMs { get; set; } = 1500;

	public int MinWindowMs { get; set; } = 120;

	private double _lagMs = 400.0;

	/// <summary>
	/// Input to host-reflected delay. Measured from the host's input acks when there are any; the estimate from
	/// matching turns against the host is only a fallback (it went to 330-1344 ms after leaving a seat, and every
	/// extra millisecond put the predicted body speed x that far ahead, into walls and stairs).
	/// </summary>
	public double LagMs { get => MeasuredLagMs ?? _lagMs; private set => _lagMs = value; }

	public double? MeasuredLagMs { get; set; }

	public double ExtraLagMs { get; set; }

	public int SnapLimit { get; set; } = 6;

	public int SnapWindowMs { get; set; } = 5000;

	public int QuietMs { get; set; } = 300;

	public uint NetId { get; private set; }

	public ushort Epoch { get; private set; }

	public bool Disabled { get; private set; }

	public LookMode Mode => _mode;

	public Quaternion LastStep { get; private set; } = Quaternion.Identity;

	public string? LastEvent { get; set; }

	public bool Intercepts
	{
		get
		{
			if (NetId != 0)
			{
				if (_mode == LookMode.Off || Disabled)
				{
					return _flushHost;
				}
				return true;
			}
			return false;
		}
	}

	public Quaternion Predicted => _p;

	public Quaternion Shown
	{
		get
		{
			if (!_hasP)
			{
				return _ho;
			}
			return _p;
		}
	}

	public LookPredictor(IMpLog log)
	{
		_log = log;
	}

	public void Begin(uint netId, long now)
	{
		NetId = netId;
		Epoch++;
		TotalX = (TotalY = (TotalZ = 0.0));
		_hasP = (_hasHost = (_flushHost = false));
		_histCount = 0;
		_snaps.Clear();
		_mode = LookMode.Off;
		_settleUntil = now + SettleMs;
		Disabled = false;
		_log.Info($"client: look prediction on for own character NetworkId {netId} (epoch {Epoch}): turning shows the same frame, " + "the host's result only corrects it (-y51online:predict:off to compare)");
	}

	public void End(string why)
	{
		if (NetId != 0)
		{
			_log.Info($"client: look prediction ended for NetworkId {NetId} ({why}); {Describe()}");
			NetId = 0u;
			_hasP = (_hasHost = (_flushHost = false));
			_histCount = 0;
			_mode = LookMode.Off;
		}
	}

	public void SetMode(LookMode mode, long now, string detail)
	{
		if (mode != _mode && NetId != 0)
		{
			LookMode mode2 = _mode;
			_mode = mode;
			ModeChanges++;
			_settleUntil = now + SettleMs;
			if (mode == LookMode.Off)
			{
				_flushHost = _hasP;
				_hasP = false;
				_histCount = 0;
			}
			else if (mode2 == LookMode.Off)
			{
				_hasP = false;
				_histCount = 0;
			}
			if (ModeChanges <= 50 || ModeChanges % 100 == 0L)
			{
				_log.Info($"client: look prediction: {ModeText(mode)} ({detail})");
			}
		}
	}

	private static string ModeText(LookMode m)
	{
		return m switch
		{
			LookMode.Ground => "walking: body yaw predicted (head pitch is the game's own client-side LookOffsetData)", 
			LookMode.Jetpack => "jetpack: pitch, yaw and roll predicted about the centre of mass", 
			_ => "not on foot (seated / no character): the host's orientation is applied as is", 
		};
	}

	public bool Step(long now, Vector3 rotation, float maxTorque, Vector3 centerOfMass)
	{
		LastStep = Quaternion.Identity;
		if (NetId == 0 || _mode == LookMode.Off)
		{
			return false;
		}
		Steps++;
		_com = centerOfMass;
		if (rotation == Vector3.Zero || !float.IsFinite(rotation.X + rotation.Y + rotation.Z))
		{
			return false;
		}
		Turns++;
		_lastTurnMs = now;
		TotalX += rotation.X;
		TotalY += rotation.Y;
		TotalZ += rotation.Z;
		if (Disabled || !_hasP)
		{
			return false;
		}
		Quaternion quaternion = LookMath.Step(_mode, rotation, maxTorque);
		if (quaternion == Quaternion.Identity)
		{
			return false;
		}
		_p = Quaternion.Normalize(_p * quaternion);
		LastStep = quaternion;
		Push(now, _p);
		return true;
	}

	public bool OnHost(long now, double x, double y, double z, Quaternion o)
	{
		HostSamples++;
		_hx = x;
		_hy = y;
		_hz = z;
		_ho = Quaternion.Normalize(o);
		_hasHost = true;
		if (NetId == 0 || _mode == LookMode.Off || Disabled)
		{
			return _flushHost;
		}
		if (!_hasP)
		{
			if (now - _lastTurnMs >= QuietMs)
			{
				Adopt(now, _ho);
				LastEvent = "adopted the host's orientation";
			}
			else
			{
				Waiting++;
				LastEvent = "host orientation shown as is (waiting for a quiet moment)";
			}
			return true;
		}
		bool flag = ExtraLagMs > 0.0;
		int num = (flag ? ((int)Math.Clamp((LagMs + ExtraLagMs) * 1.25 + 50.0, MinWindowMs, Math.Min(HistoryMs, 700))) : ((int)Math.Clamp(LagMs * 1.5 + 50.0, MinWindowMs, HistoryMs)));
		float num2 = LookMath.AngleDeg(_ho, _p);
		Quaternion value = _p;
		long num3 = now;
		float num4 = 0f;
		(long, Quaternion) tuple = (now, _p);
		for (int i = 0; i < _histCount; i++)
		{
			var (num5, quaternion) = _hist[(_histHead - 1 - i + _hist.Length) % _hist.Length];
			if (now - tuple.Item1 > num)
			{
				break;
			}
			var (num6, quaternion2, num7) = LookMath.ClosestOnArc(quaternion, tuple.Item2, _ho);
			if (num6 < num2)
			{
				num2 = num6;
				value = quaternion2;
				num3 = num5 + (long)Math.Round((float)(tuple.Item1 - num5) * num7);
				long num8 = Math.Max(1L, tuple.Item1 - num5);
				num4 = LookMath.AngleDeg(quaternion, tuple.Item2) / (float)num8;
			}
			tuple = (num5, quaternion);
		}
		if (num2 < 0.5f && num4 >= 0.03f)
		{
			LagMs = (flag ? Math.Clamp(LagMs + 0.1 * ((double)(now - num3) - ExtraLagMs - LagMs), 30.0, 350.0) : (LagMs + 0.1 * ((double)(now - num3) - LagMs)));
			LagSamples++;
		}
		LastErrDeg = num2;
		bool flag2 = now < _settleUntil;
		if (num2 <= AgreeDeg)
		{
			Agreed++;
			return true;
		}
		if (num2 >= (flag2 ? SettleSnapDeg : SnapDeg))
		{
			Snap(now, num2, flag2 ? "while the movement state settles" : "the host turned the character by itself (teleport, collision, seat)");
			LastEvent = "snapped " + num2.ToString("0.0", CultureInfo.InvariantCulture) + " deg";
			return true;
		}
		float num9 = Math.Clamp((num2 - DeadbandDeg) / ((flag2 ? SettleSnapDeg : SnapDeg) - DeadbandDeg), 0f, 1f);
		float amount = (flag2 ? BlendMax : (BlendMin + (BlendMax - BlendMin) * num9));
		Quaternion quaternion3 = LookMath.Canonical(Quaternion.Normalize(_ho * Quaternion.Conjugate(value)));
		Quaternion quaternion4 = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, quaternion3, amount));
		_p = Quaternion.Normalize(quaternion4 * _p);
		for (int j = 0; j < _histCount; j++)
		{
			int num10 = (_histHead - 1 - j + _hist.Length) % _hist.Length;
			_hist[num10].Q = Quaternion.Normalize(quaternion4 * _hist[num10].Q);
		}
		if (num2 <= DeadbandDeg)
		{
			Agreed++;
			return true;
		}
		Blended++;
		LastEvent = "corrected " + num2.ToString("0.0", CultureInfo.InvariantCulture) + " deg x" + amount.ToString("0.00", CultureInfo.InvariantCulture);
		if (num2 > MaxBlendDeg)
		{
			MaxBlendDeg = num2;
		}
		return true;
	}

	public void Rebase(Quaternion delta)
	{
		delta = Quaternion.Normalize(delta);
		_p = Quaternion.Normalize(delta * _p);
		_ho = Quaternion.Normalize(delta * _ho);
		for (int i = 0; i < _histCount; i++)
		{
			int num = (_histHead - 1 - i + _hist.Length) % _hist.Length;
			_hist[num].Q = Quaternion.Normalize(delta * _hist[num].Q);
		}
	}

	public void SeedLag(double pingMs)
	{
		LagMs = Math.Clamp(pingMs + 67.0, 30.0, 350.0);
	}

	private void Adopt(long now, Quaternion o)
	{
		_p = o;
		_hasP = true;
		_histCount = 0;
		Push(now, o);
		Adopted++;
	}

	private void Snap(long now, float errDeg, string why)
	{
		Snapped++;
		_snaps.Enqueue(now);
		while (_snaps.Count > 0 && now - _snaps.Peek() > SnapWindowMs)
		{
			_snaps.Dequeue();
		}
		if (Snapped <= 20 || Snapped % 100 == 0L)
		{
			_log.Info($"client: look prediction snapped to the host's orientation ({errDeg.ToString("0.0", CultureInfo.InvariantCulture)} deg off: {why}; snap #{Snapped})");
		}
		_hasP = false;
		_histCount = 0;
		if (_snaps.Count > SnapLimit)
		{
			Disabled = true;
			_log.Warn($"client: look prediction keeps disagreeing with the host ({_snaps.Count} snaps in {SnapWindowMs / 1000} s); switched off for NetworkId {NetId} until the next character, the host's orientation is applied as is; {Describe()}");
		}
	}

	private void Push(long now, Quaternion q)
	{
		_hist[_histHead] = (T: now, Q: q);
		_histHead = (_histHead + 1) % _hist.Length;
		if (_histCount < _hist.Length)
		{
			_histCount++;
		}
	}

	public byte[]? Display()
	{
		if (!_hasHost)
		{
			return null;
		}
		if (_flushHost)
		{
			_flushHost = false;
			Written++;
			return LookMath.WriteTransform(_hx, _hy, _hz, _ho);
		}
		if (NetId == 0 || _mode == LookMode.Off || Disabled)
		{
			return null;
		}
		Written++;
		if (!_hasP)
		{
			return LookMath.WriteTransform(_hx, _hy, _hz, _ho);
		}
		Vector3 vector = ((_mode == LookMode.Jetpack) ? LookMath.PivotShift(_ho, _p, _com) : Vector3.Zero);
		return LookMath.WriteTransform(_hx + (double)vector.X, _hy + (double)vector.Y, _hz + (double)vector.Z, _p);
	}

	public string Describe()
	{
		if (NetId != 0)
		{
			return $"look prediction {(Disabled ? "SWITCHED OFF" : _mode.ToString().ToLowerInvariant())} for NetworkId {NetId} epoch {Epoch}: {Turns} turning ticks of {Steps}, host samples {HostSamples} (agreed {Agreed}, corrected {Blended}, snapped {Snapped}, adopted {Adopted}, shown as is before adoption {Waiting}), last error {LastErrDeg.ToString("0.00", CultureInfo.InvariantCulture)} deg, largest corrected {MaxBlendDeg.ToString("0.0", CultureInfo.InvariantCulture)} deg, host lag {LagMs.ToString("0", CultureInfo.InvariantCulture)} ms, mode changes {ModeChanges}, transforms written {Written}";
		}
		return "look prediction: idle";
	}
}
