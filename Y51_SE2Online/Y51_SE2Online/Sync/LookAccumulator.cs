using System;
using System.Buffers.Binary;
using System.Globalization;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class LookAccumulator
{
	public const int ControlDataBytes = 24;

	public const int RotationOffset = 12;

	public const double Epsilon = 1E-06;

	public const double ResyncRad = Math.PI;

	private bool _seen;

	private uint _seq;

	private double _tx;

	private double _ty;

	private double _tz;

	private double _ax;

	private double _ay;

	private double _az;

	private bool _lastNonZero;

	public ushort? ControlKey;

	public byte[]? Control;

	public long Messages;

	public long Stale;

	public long Epochs;

	public long Injected;

	public long Resyncs;

	public double MaxCatchUpRad;

	public uint NetId { get; private set; }

	public ushort Epoch { get; private set; }

	public (double X, double Y, double Z) Pending => (X: _tx - _ax, Y: _ty - _ay, Z: _tz - _az);

	public bool Offer(uint seq, uint netId, ushort epoch, double x, double y, double z)
	{
		Messages++;
		bool restart = !_seen || netId != NetId || epoch != Epoch;
		if (!restart && !Seq.Newer(seq, _seq))
		{
			Stale++;
			return false;
		}
		if (restart)
		{
			if (netId != NetId)
			{
				Control = null;
			}
			NetId = netId;
			Epoch = epoch;
			_ax = _ay = _az = 0.0;
			_lastNonZero = false;
			Epochs++;
		}
		_seen = true;
		_seq = seq;
		if (double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z))
		{
			_tx = x;
			_ty = y;
			_tz = z;
		}
		return restart;
	}

	public bool Take(out float x, out float y, out float z)
	{
		double dx = _tx - _ax;
		double dy = _ty - _ay;
		double dz = _tz - _az;
		double catchUp = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
		if (catchUp > Math.PI)
		{
			Resyncs++;
			_ax = _tx;
			_ay = _ty;
			_az = _tz;
			dx = dy = dz = 0.0;
			catchUp = 0.0;
		}
		x = Math.Abs(dx) >= 1E-06 ? (float)dx : 0f;
		y = Math.Abs(dy) >= 1E-06 ? (float)dy : 0f;
		z = Math.Abs(dz) >= 1E-06 ? (float)dz : 0f;
		_ax += x;
		_ay += y;
		_az += z;
		bool turned = x != 0f || y != 0f || z != 0f;
		// One extra tick after the last turn so the game sees the rotation go back to zero.
		bool result = turned || _lastNonZero;
		_lastNonZero = turned;
		if (turned)
		{
			Injected++;
			if (catchUp > MaxCatchUpRad)
			{
				MaxCatchUpRad = catchUp;
			}
		}
		return result;
	}

	public static byte[] WithRotation(byte[] control, float x, float y, float z)
	{
		byte[] copy = (byte[])control.Clone();
		BinaryPrimitives.WriteSingleLittleEndian(copy.AsSpan(12), x);
		BinaryPrimitives.WriteSingleLittleEndian(copy.AsSpan(16), y);
		BinaryPrimitives.WriteSingleLittleEndian(copy.AsSpan(20), z);
		return copy;
	}

	public string Describe()
	{
		if (_seen)
		{
			return $"look totals for NetworkId {NetId} epoch {Epoch}: {Messages} msgs ({Stale} stale), {Injected} ticks turned, {Resyncs} resyncs, largest catch-up {(MaxCatchUpRad * 180.0 / Math.PI).ToString("0.0", CultureInfo.InvariantCulture)} deg";
		}
		return "look totals: none";
	}
}
