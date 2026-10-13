using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class EntityBundleMessage
{
	public const byte KindWorld = 0;

	public const byte KindSpawn = 1;

	public uint LifeSeq;

	public byte Kind;

	public uint HostTick;

	public byte[] Bundle = Array.Empty<byte>();

	public List<EntityLocator> Entities = new List<EntityLocator>();

	public List<(uint NetId, List<DataBlob> Data)> Data = new List<(uint, List<DataBlob>)>();

	public List<(uint Child, uint Parent)> Parents = new List<(uint, uint)>();

	public List<uint> Staged = new List<uint>();

	public List<uint> Skipped = new List<uint>();

	public List<Guid> RequiredBlobs = new List<Guid>();

	public const byte CompressedFlag = 128;

	public const int CompressAboveBytes = 65536;

	public int RawBodyBytes;

	public int WireBodyBytes;

	public int DataBytes
	{
		get
		{
			int num = 0;
			foreach (var datum in Data)
			{
				foreach (DataBlob item in datum.Data)
				{
					num += item.Bytes.Length;
				}
			}
			return num;
		}
	}

	public byte[] Encode()
	{
		WireWriter wireWriter = new WireWriter(64 + Bundle.Length + Entities.Count * 12 + DataBytes + Data.Count * 8);
		EncodeBody(wireWriter);
		byte[] array = wireWriter.ToArray();
		RawBodyBytes = array.Length;
		byte b = Kind;
		byte[] array2 = array;
		if (array.Length >= 65536)
		{
			using MemoryStream memoryStream = new MemoryStream(array.Length / 4 + 64);
			using (DeflateStream deflateStream = new DeflateStream(memoryStream, CompressionLevel.Fastest, leaveOpen: true))
			{
				deflateStream.Write(array, 0, array.Length);
			}
			if (memoryStream.Length + 4 < array.Length)
			{
				byte[] array3 = new byte[4 + memoryStream.Length];
				BinaryPrimitives.WriteInt32LittleEndian(array3, array.Length);
				memoryStream.Position = 0L;
				memoryStream.Read(array3, 4, (int)memoryStream.Length);
				array2 = array3;
				b |= 0x80;
			}
		}
		WireBodyBytes = array2.Length;
		WireWriter wireWriter2 = new WireWriter(16 + array2.Length);
		wireWriter2.U8(49).U32(LifeSeq).U8(b)
			.U32(HostTick)
			.Bytes(array2);
		return wireWriter2.ToArray();
	}

	private void EncodeBody(WireWriter w)
	{
		w.I32(Bundle.Length).Bytes(Bundle);
		w.I32(Entities.Count);
		foreach (EntityLocator entity in Entities)
		{
			w.U32(entity.NetId).I32(entity.Root).U8((byte)entity.Path.Length);
			int[] path = entity.Path;
			foreach (int v in path)
			{
				w.I32(v);
			}
		}
		w.I32(Data.Count);
		foreach (var (v2, list) in Data)
		{
			w.U32(v2).U8((byte)list.Count);
			foreach (DataBlob item in list)
			{
				w.U16(item.Key).U16((ushort)item.Bytes.Length).Bytes(item.Bytes);
			}
		}
		w.I32(Parents.Count);
		foreach (var (v3, v4) in Parents)
		{
			w.U32(v3).U32(v4);
		}
		w.I32(Staged.Count);
		foreach (uint item2 in Staged)
		{
			w.U32(item2);
		}
		w.I32(Skipped.Count);
		foreach (uint item3 in Skipped)
		{
			w.U32(item3);
		}
		WireGuids.Write(w, RequiredBlobs);
	}

	public static EntityBundleMessage Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		EntityBundleMessage entityBundleMessage = new EntityBundleMessage
		{
			LifeSeq = wireReader.U32(),
			Kind = wireReader.U8(),
			HostTick = wireReader.U32()
		};
		ReadOnlySpan<byte> readOnlySpan = d.Slice(10);
		byte[] array = null;
		if ((entityBundleMessage.Kind & 0x80) != 0)
		{
			entityBundleMessage.Kind &= 127;
			int num = BinaryPrimitives.ReadInt32LittleEndian(readOnlySpan);
			if (num < 0 || num > 536870912)
			{
				throw new InvalidDataException($"bundle body {num} B");
			}
			array = new byte[num];
			using DeflateStream deflateStream = new DeflateStream(new MemoryStream(readOnlySpan.Slice(4).ToArray()), CompressionMode.Decompress);
			int num2;
			for (int i = 0; i < num; i += num2)
			{
				num2 = deflateStream.Read(array, i, num - i);
				if (num2 <= 0)
				{
					throw new InvalidDataException($"bundle body ended at {i} of {num} B");
				}
			}
			entityBundleMessage.WireBodyBytes = readOnlySpan.Length;
			readOnlySpan = array;
		}
		entityBundleMessage.RawBodyBytes = readOnlySpan.Length;
		if (array == null)
		{
			entityBundleMessage.WireBodyBytes = readOnlySpan.Length;
		}
		WireReader r = new WireReader(readOnlySpan);
		entityBundleMessage.Bundle = r.Bytes(r.I32()).ToArray();
		int num3 = r.I32();
		entityBundleMessage.Entities = new List<EntityLocator>(num3);
		for (int j = 0; j < num3; j++)
		{
			uint netId = r.U32();
			int root = r.I32();
			int[] array2 = new int[r.U8()];
			for (int k = 0; k < array2.Length; k++)
			{
				array2[k] = r.I32();
			}
			entityBundleMessage.Entities.Add(new EntityLocator(netId, root, array2));
		}
		num3 = r.I32();
		entityBundleMessage.Data = new List<(uint, List<DataBlob>)>(num3);
		for (int l = 0; l < num3; l++)
		{
			uint item = r.U32();
			int num4 = r.U8();
			List<DataBlob> list = new List<DataBlob>(num4);
			for (int m = 0; m < num4; m++)
			{
				ushort key = r.U16();
				int n = r.U16();
				list.Add(new DataBlob(key, r.Bytes(n).ToArray()));
			}
			entityBundleMessage.Data.Add((item, list));
		}
		num3 = r.I32();
		for (int num5 = 0; num5 < num3; num5++)
		{
			entityBundleMessage.Parents.Add((r.U32(), r.U32()));
		}
		num3 = r.I32();
		for (int num6 = 0; num6 < num3; num6++)
		{
			entityBundleMessage.Staged.Add(r.U32());
		}
		num3 = r.I32();
		for (int num7 = 0; num7 < num3; num7++)
		{
			entityBundleMessage.Skipped.Add(r.U32());
		}
		entityBundleMessage.RequiredBlobs = WireGuids.Read(ref r);
		return entityBundleMessage;
	}

	public static void SetLifeSeq(byte[] encoded, uint seq)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(1), seq);
	}
}
