using System;
using System.Numerics;

namespace Y51_SE2Online.Sync;

// Position lead for the joiner's own character: shown = newest host position + the velocity predicted for the
// inputs the host had not applied yet (host stamp minus the round trip, up to now). Nothing accumulates, so a
// wrong model only offsets the body by (error x round trip) until the input changes.
public sealed class MovePredictor
{
	private readonly (long T, Vector3 Model, Vector3 Other)[] _hist = new (long, Vector3, Vector3)[512];

	private int _head;

	private int _count;

	private long _hostHigh = -1L;

	private long _offset = long.MinValue;

	private long _offsetSetMs;

	private double _anchor = double.NaN;

	// The offset estimate steps (+2 ms a second, down at once on a quick packet); anchoring on it directly moved the
	// lead by speed x step (0.1-0.6 m at jetpack/ship speeds, a snap back or forward). Slewed like playout instead.
	private double _play = double.NaN;

	private long _playSetMs = long.MinValue;

	private readonly WindowMin _offsetWindow = new();

	private double _late;

	private Vector3 _model;

	private long _lastStepMs;

	private bool _ground;

	public int MaxLeadMs { get; set; } = 500;

	public float Gain { get; private set; } = 1f;

	public Vector3 Velocity { get; private set; }

	public bool Active => !double.IsNaN(_anchor) && _count > 0;

	public void Reset()
	{
		_count = 0;
		_anchor = double.NaN;
		_ground = false;
		Gain = 1f;
		Velocity = default;
	}

	// Walking: the model velocity chases the input's target at the controller's MaxAcceleration; it starts
	// from the host's horizontal velocity whenever walking (re)starts.
	public void StepGround(long now, Vector3 target, Vector3 hostHorizontal, Vector3 rest, float maxAcceleration, bool inputChanged)
	{
		if (!_ground || _count == 0)
		{
			_model = hostHorizontal;
		}
		else
		{
			_model = Approach(_model, target, maxAcceleration * Math.Clamp(now - _lastStepMs, 0L, 100L) / 1000f);
		}
		Step(now, _model, rest, inputChanged);
		_ground = true;
	}

	// model: the input-driven part (scaled by Gain); rest: taken from the host as is (gravity, jetpack drift).
	public void Step(long now, Vector3 model, Vector3 rest, bool inputChanged)
	{
		_ground = false;
		_lastStepMs = now;
		if (inputChanged)
		{
			Gain = 1f;
		}
		Velocity = model * Gain + rest;
		_hist[_head] = (now, model, rest);
		_head = (_head + 1) % _hist.Length;
		if (_count < _hist.Length)
		{
			_count++;
		}
	}

	// Anchors on the host's own stamp (min-filtered clock offset, as TransformSmoother does) so arrival jitter
	// does not move the window; _late is the mean jitter, which LookPredictor.LagMs contains and Lead removes.
	public void OnHost(long now, uint hostMs)
	{
		if (hostMs == 0)
		{
			_anchor = now - _late;
			return;
		}
		long hostT = Unwrap(hostMs);
		long transit = now - hostT;
		_offset = _offsetWindow.Add(now, transit);
		_late += 0.05 * ((double)(transit - _offset) - _late);
		double dt = _playSetMs == long.MinValue ? 0 : Math.Clamp(now - _playSetMs, 0L, 100L);
		_playSetMs = now;
		_play = double.IsNaN(_play) || Math.Abs(_offset - _play) > 250.0 ? _offset : _play + Math.Clamp(_offset - _play, -0.02 * dt, 0.02 * dt);
		_anchor = hostT + _play;
	}

	// How much of the modelled velocity the host achieved (walls, slopes). Only steady stretches count, so the
	// start/stop ramp and a slightly off lag estimate are not taken for a wall.
	public void Observe(Vector3 hostModelPart, double lagMs)
	{
		if (double.IsNaN(_anchor))
		{
			return;
		}
		long then = (long)Math.Round(InputTime(lagMs));
		Vector3 model = ModelAt(then);
		float speedSq = model.LengthSquared();
		if (speedSq < 0.25f || (ModelAt(then - 100) - model).LengthSquared() > 0.01f * speedSq)
		{
			return;
		}
		float ratio = Math.Clamp(Vector3.Dot(hostModelPart, model) / speedSq, 0f, 1f);
		Gain += 0.2f * (ratio - Gain);
	}

	public Vector3 Lead(long now, double lagMs)
	{
		if (!Active)
		{
			return Vector3.Zero;
		}
		double from = Math.Max(InputTime(lagMs), now - MaxLeadMs);
		Vector3 model = Vector3.Zero;
		Vector3 rest = Vector3.Zero;
		double end = now;
		for (int i = 0; i < _count && end > from; i++)
		{
			var (t, m, r) = _hist[(_head - 1 - i + _hist.Length) % _hist.Length];
			double start = Math.Max(t, from);
			if (end > start)
			{
				float seconds = (float)((end - start) / 1000.0);
				model += m * seconds;
				rest += r * seconds;
			}
			end = Math.Min(end, t);
		}
		return model * Gain + rest;
	}

	// CharacterMovementComponent.ApplyControlData (2.4.0.95) feeding CharacterPhysicsData: speed is
	// max(|FB|, |LR|) x BaseGroundVelocity along the input direction; local forward is -Z, right is +X.
	public static Vector3 GroundVelocity(Quaternion orientation, float moveX, float moveZ, bool sprinting, bool crouched, float jogMultiplier, float crouchMultiplier, float baseVelocity)
	{
		float fb = 0f - moveZ;
		float fbScale = 1f;
		float lrScale = jogMultiplier;
		if (crouched)
		{
			fbScale = (lrScale = crouchMultiplier);
		}
		else if (!sprinting || fb < 0f || MathF.Abs(moveX) > 1E-05f)
		{
			fbScale = jogMultiplier;
		}
		else if (fb > 1E-05f)
		{
			fb = 1f;
		}
		fb *= fbScale;
		float lr = moveX * lrScale;
		float speed = MathF.Max(MathF.Abs(fb), MathF.Abs(lr)) * baseVelocity;
		if (speed < 1E-06f)
		{
			return Vector3.Zero;
		}
		return Vector3.Transform(Vector3.Normalize(new Vector3(lr, 0f, 0f - fb)) * speed, orientation);
	}

	public static Vector3 Approach(Vector3 current, Vector3 target, float maxStep)
	{
		Vector3 delta = target - current;
		float length = delta.Length();
		if (length <= maxStep || length < 1E-06f)
		{
			return target;
		}
		return current + delta * (maxStep / length);
	}

	private double InputTime(double lagMs)
	{
		return _anchor - Math.Max(0.0, lagMs - _late);
	}

	private Vector3 ModelAt(long t)
	{
		for (int i = 0; i < _count; i++)
		{
			var entry = _hist[(_head - 1 - i + _hist.Length) % _hist.Length];
			if (entry.T <= t)
			{
				return entry.Model;
			}
		}
		return Vector3.Zero;
	}

	private long Unwrap(uint hostMs)
	{
		if (_hostHigh < 0)
		{
			_hostHigh = hostMs;
			return _hostHigh;
		}
		long t = _hostHigh + ((int)hostMs - (int)_hostHigh);
		if (t > _hostHigh)
		{
			_hostHigh = t;
		}
		return t;
	}
}
