using System;

namespace Y51_SE2Online.Sync;

public readonly record struct Vec3D(double X, double Y, double Z)
{
	public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

	public static Vec3D operator +(Vec3D a, Vec3D b)
	{
		return new Vec3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
	}

	public static Vec3D operator -(Vec3D a, Vec3D b)
	{
		return new Vec3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
	}

	public static Vec3D operator *(Vec3D a, double k)
	{
		return new Vec3D(a.X * k, a.Y * k, a.Z * k);
	}
}
