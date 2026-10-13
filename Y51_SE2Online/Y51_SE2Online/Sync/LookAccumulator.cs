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
		bool flag = !_seen || netId != NetId || epoch != Epoch;
		if (!flag && !Seq.Newer(seq, _seq))
		{
			Stale++;
			return false;
		}
		if (flag)
		{
			if (netId != NetId)
			{
				Control = null;
			}
			NetId = netId;
			Epoch = epoch;
			_ax = (_ay = (_az = 0.0));
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
		return flag;
	}

	public bool Take(out float x, out float y, out float z)
	{
		double num = _tx - _ax;
		double num2 = _ty - _ay;
		double num3 = _tz - _az;
		double num4 = Math.Max(Math.Abs(num), Math.Max(Math.Abs(num2), Math.Abs(num3)));
		if (num4 > Math.PI)
		{
			Resyncs++;
			_ax = _tx;
			_ay = _ty;
			_az = _tz;
			num = (num2 = (num3 = 0.0));
			num4 = 0.0;
		}
		x = ((Math.Abs(num) >= 1E-06) ? ((float)num) : 0f);
		y = ((Math.Abs(num2) >= 1E-06) ? ((float)num2) : 0f);
		z = ((Math.Abs(num3) >= 1E-06) ? ((float)num3) : 0f);
		_ax += x;
		_ay += y;
		_az += z;
		bool flag = x != 0f || y != 0f || z != 0f;
		bool result = flag || _lastNonZero;
		_lastNonZero = flag;
		if (flag)
		{
			Injected++;
			if (num4 > MaxCatchUpRad)
			{
				MaxCatchUpRad = num4;
			}
		}
		return result;
	}

	public static byte[] WithRotation(byte[] control, float x, float y, float z)
	{
		byte[] array = (byte[])control.Clone();
		BinaryPrimitives.WriteSingleLittleEndian(array.AsSpan(12), x);
		BinaryPrimitives.WriteSingleLittleEndian(array.AsSpan(16), y);
		BinaryPrimitives.WriteSingleLittleEndian(array.AsSpan(20), z);
		return array;
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
