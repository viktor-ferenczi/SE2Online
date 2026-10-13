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
			float num = 1f / 60f * maxTorque;
			Quaternion identity = Quaternion.Identity;
			if (rotation.X != 0f)
			{
				identity *= Quaternion.CreateFromAxisAngle(Left, rotation.X * num);
			}
			if (rotation.Y != 0f)
			{
				identity *= Quaternion.CreateFromAxisAngle(Down, rotation.Y * num);
			}
			if (rotation.Z != 0f)
			{
				identity *= Quaternion.CreateFromAxisAngle(Forward, rotation.Z * num);
			}
			return identity;
		}
		default:
			return Quaternion.Identity;
		}
	}

	public static float AngleDeg(Quaternion a, Quaternion b)
	{
		Quaternion quaternion = Quaternion.Conjugate(Quaternion.Normalize(a)) * Quaternion.Normalize(b);
		float y = MathF.Sqrt(quaternion.X * quaternion.X + quaternion.Y * quaternion.Y + quaternion.Z * quaternion.Z);
		return 2f * MathF.Atan2(y, MathF.Abs(quaternion.W)) * (180f / (float)Math.PI);
	}

	public static (float Deg, Quaternion Point, float U) ClosestOnArc(Quaternion a, Quaternion b, Quaternion h)
	{
		Vector4 vector = Vector4.Normalize(V(a));
		Vector4 vector2 = Vector4.Normalize(V(b));
		Vector4 vector3 = Vector4.Normalize(V(h));
		float num = Vector4.Dot(vector, vector2);
		if (num < 0f)
		{
			vector2 = -vector2;
			num = 0f - num;
		}
		if (Vector4.Dot(vector3, vector) + Vector4.Dot(vector3, vector2) < 0f)
		{
			vector3 = -vector3;
		}
		Vector4 vector4 = vector2 - num * vector;
		float num2 = vector4.Length();
		if (num2 < 1E-05f || num > 0.9999999f)
		{
			return (Deg: AngleDeg(Q(vector), Q(vector3)), Point: Q(vector), U: 0f);
		}
		Vector4 vector5 = vector4 / num2;
		float num3 = MathF.Atan2(Vector4.Dot(vector2, vector5), num);
		if (!(num3 > 0f))
		{
			return (Deg: AngleDeg(Q(vector), Q(vector3)), Point: Q(vector), U: 0f);
		}
		float num4 = Math.Clamp(MathF.Atan2(Vector4.Dot(vector3, vector5), Vector4.Dot(vector3, vector)), 0f, num3);
		Quaternion quaternion = Q(MathF.Cos(num4) * vector + MathF.Sin(num4) * vector5);
		return (Deg: AngleDeg(quaternion, Q(vector3)), Point: quaternion, U: (num3 > 0f) ? (num4 / num3) : 0f);
		static Quaternion Q(Vector4 v)
		{
			return new Quaternion(v.X, v.Y, v.Z, v.W);
		}
		static Vector4 V(Quaternion q)
		{
			return new Vector4(q.X, q.Y, q.Z, q.W);
		}
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
		double item = BinaryPrimitives.ReadDoubleLittleEndian(b);
		ref ReadOnlySpan<byte> reference = ref b;
		double item2 = BinaryPrimitives.ReadDoubleLittleEndian(reference.Slice(8, reference.Length - 8));
		reference = ref b;
		double item3 = BinaryPrimitives.ReadDoubleLittleEndian(reference.Slice(16, reference.Length - 16));
		reference = ref b;
		float x = BinaryPrimitives.ReadSingleLittleEndian(reference.Slice(24, reference.Length - 24));
		reference = ref b;
		float y = BinaryPrimitives.ReadSingleLittleEndian(reference.Slice(28, reference.Length - 28));
		reference = ref b;
		float z = BinaryPrimitives.ReadSingleLittleEndian(reference.Slice(32, reference.Length - 32));
		reference = ref b;
		return (X: item, Y: item2, Z: item3, O: new Quaternion(x, y, z, BinaryPrimitives.ReadSingleLittleEndian(reference.Slice(36, reference.Length - 36))));
	}

	public static byte[] WriteTransform(double x, double y, double z, Quaternion o)
	{
		byte[] array = new byte[40];
		BinaryPrimitives.WriteDoubleLittleEndian(array, x);
		BinaryPrimitives.WriteDoubleLittleEndian(array.AsSpan(8), y);
		BinaryPrimitives.WriteDoubleLittleEndian(array.AsSpan(16), z);
		BinaryPrimitives.WriteSingleLittleEndian(array.AsSpan(24), o.X);
		BinaryPrimitives.WriteSingleLittleEndian(array.AsSpan(28), o.Y);
		BinaryPrimitives.WriteSingleLittleEndian(array.AsSpan(32), o.Z);
		BinaryPrimitives.WriteSingleLittleEndian(array.AsSpan(36), o.W);
		return array;
	}
}
