using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Y51_SE2Online.Sync;

public static class LookMath
{
	public static readonly Vector3 Up = new Vector3(0f, 1f, 0f);

	public static readonly Vector3 Down = new Vector3(0f, -1f, 0f);

	public static readonly Vector3 Left = new Vector3(-1f, 0f, 0f);

	public static readonly Vector3 Forward = new Vector3(0f, 0f, -1f);

	public const float DefaultJetpackMaxTorque = 65f;

	public static Quaternion Step(LookMode mode, Vector3 rotation, float maxTorque)
	{
		switch (mode)
		{
		case LookMode.Ground:
			if (rotation.Y == 0f)
			{
				return Quaternion.Identity;
			}
			return Quaternion.CreateFromAxisAngle(Up, 0f - rotation.Y);
		case LookMode.Jetpack:
		{
			float scale = 1f / 60f * maxTorque;
			Quaternion step = Quaternion.Identity;
			if (rotation.X != 0f)
			{
				step *= Quaternion.CreateFromAxisAngle(Left, rotation.X * scale);
			}
			if (rotation.Y != 0f)
			{
				step *= Quaternion.CreateFromAxisAngle(Down, rotation.Y * scale);
			}
			if (rotation.Z != 0f)
			{
				step *= Quaternion.CreateFromAxisAngle(Forward, rotation.Z * scale);
			}
			return step;
		}
		default:
			return Quaternion.Identity;
		}
	}

	public static float AngleDeg(Quaternion a, Quaternion b)
	{
		Quaternion d = Quaternion.Conjugate(Quaternion.Normalize(a)) * Quaternion.Normalize(b);
		float sinHalf = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
		return 2f * MathF.Atan2(sinHalf, MathF.Abs(d.W)) * (180f / (float)Math.PI);
	}

	public static (float Deg, Quaternion Point, float U) ClosestOnArc(Quaternion a, Quaternion b, Quaternion h)
	{
		Vector4 va = Vector4.Normalize(V(a));
		Vector4 vb = Vector4.Normalize(V(b));
		Vector4 vh = Vector4.Normalize(V(h));
		float cos = Vector4.Dot(va, vb);
		if (cos < 0f)
		{
			vb = -vb;
			cos = 0f - cos;
		}
		if (Vector4.Dot(vh, va) + Vector4.Dot(vh, vb) < 0f)
		{
			vh = -vh;
		}
		// Unit vector orthogonal to a in the a-b plane: the arc from a to b is cos(t) a + sin(t) ortho.
		Vector4 perp = vb - cos * va;
		float perpLength = perp.Length();
		if (perpLength < 1E-05f || cos > 0.9999999f)
		{
			return (Deg: AngleDeg(Q(va), Q(vh)), Point: Q(va), U: 0f);
		}
		Vector4 ortho = perp / perpLength;
		float arc = MathF.Atan2(Vector4.Dot(vb, ortho), cos);
		if (!(arc > 0f))
		{
			return (Deg: AngleDeg(Q(va), Q(vh)), Point: Q(va), U: 0f);
		}
		float along = Math.Clamp(MathF.Atan2(Vector4.Dot(vh, ortho), Vector4.Dot(vh, va)), 0f, arc);
		Quaternion point = Q(MathF.Cos(along) * va + MathF.Sin(along) * ortho);
		return (Deg: AngleDeg(point, Q(vh)), Point: point, U: arc > 0f ? along / arc : 0f);

		static Quaternion Q(Vector4 v) => new Quaternion(v.X, v.Y, v.Z, v.W);
		static Vector4 V(Quaternion q) => new Vector4(q.X, q.Y, q.Z, q.W);
	}

	public static Quaternion Canonical(Quaternion q)
	{
		if (!(q.W < 0f))
		{
			return q;
		}
		return new Quaternion(0f - q.X, 0f - q.Y, 0f - q.Z, 0f - q.W);
	}

	public static Vector3 PivotShift(Quaternion host, Quaternion shown, Vector3 com)
	{
		if (com == Vector3.Zero)
		{
			return Vector3.Zero;
		}
		Quaternion rotation = Quaternion.Normalize(Quaternion.Conjugate(host) * shown);
		return Vector3.Transform(com - Vector3.Transform(com, rotation), host);
	}

	public static (double X, double Y, double Z, Quaternion O) ReadTransform(ReadOnlySpan<byte> b)
	{
		return (BinaryPrimitives.ReadDoubleLittleEndian(b), BinaryPrimitives.ReadDoubleLittleEndian(b[8..]), BinaryPrimitives.ReadDoubleLittleEndian(b[16..]),
			new Quaternion(BinaryPrimitives.ReadSingleLittleEndian(b[24..]), BinaryPrimitives.ReadSingleLittleEndian(b[28..]), BinaryPrimitives.ReadSingleLittleEndian(b[32..]), BinaryPrimitives.ReadSingleLittleEndian(b[36..])));
	}

	public static byte[] WriteTransform(double x, double y, double z, Quaternion o)
	{
		byte[] buffer = new byte[40];
		BinaryPrimitives.WriteDoubleLittleEndian(buffer, x);
		BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(8), y);
		BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(16), z);
		BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(24), o.X);
		BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(28), o.Y);
		BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(32), o.Z);
		BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(36), o.W);
		return buffer;
	}
}
