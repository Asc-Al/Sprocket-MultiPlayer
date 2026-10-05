using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace SprocketMP.Net
{
    public enum Msg : byte
    {
        Hello = 1,
        Welcome = 2,
        Roster = 3,
        Chat = 4,
        Kick = 5,
        TeamRequest = 6,
        Ping = 7,
        Pong = 8,

        BattleStart = 10,
        BattleEnd = 11,
        Claim = 12,
        Owners = 13,
        ClientInBattle = 14,
        Resync = 15,
        Retry = 16,
        MyTank = 17,
        Rename = 18,
        Lobby = 19,
        Blueprint = 23,
        LobbyAdd = 24,
        LobbyRemove = 25,
        Pick = 26,

        State = 20,
        Fire = 21,
        Damage = 22,

        Chunk = 30,
    }

    public sealed class Writer
    {
        readonly MemoryStream _ms = new MemoryStream(256);
        readonly BinaryWriter _w;
        public Writer(Msg type) { _w = new BinaryWriter(_ms); _w.Write((byte)type); }
        public Writer Byte(byte v) { _w.Write(v); return this; }
        public Writer Bool(bool v) { _w.Write(v); return this; }
        public Writer Int(int v) { _w.Write(v); return this; }
        public Writer UInt(uint v) { _w.Write(v); return this; }
        public Writer Short(short v) { _w.Write(v); return this; }
        public Writer Float(float v) { _w.Write(v); return this; }
        public Writer Double(double v) { _w.Write(v); return this; }
        public Writer Str(string v) { _w.Write(v ?? ""); return this; }
        public Writer Bytes(byte[] v) { _w.Write(v.Length); _w.Write(v); return this; }
        public Writer Vec(Vector3 v) { _w.Write(v.x); _w.Write(v.y); _w.Write(v.z); return this; }
        public Writer Quat(Quaternion q) { _w.Write(q.x); _w.Write(q.y); _w.Write(q.z); _w.Write(q.w); return this; }
        public byte[] ToArray() => _ms.ToArray();
    }

    public sealed class Reader
    {
        readonly BinaryReader _r;
        public Msg Type { get; }
        public Reader(byte[] data) { _r = new BinaryReader(new MemoryStream(data)); Type = (Msg)_r.ReadByte(); }
        public byte Byte() => _r.ReadByte();
        public bool Bool() => _r.ReadBoolean();
        public int Int() => _r.ReadInt32();
        public uint UInt() => _r.ReadUInt32();
        public short Short() => _r.ReadInt16();
        public float Float() => _r.ReadSingle();
        public double Double() => _r.ReadDouble();
        public string Str() => _r.ReadString();
        public byte[] Bytes() { int n = _r.ReadInt32(); return _r.ReadBytes(n); }
        public Vector3 Vec() => new Vector3(_r.ReadSingle(), _r.ReadSingle(), _r.ReadSingle());
        public Quaternion Quat() => new Quaternion(_r.ReadSingle(), _r.ReadSingle(), _r.ReadSingle(), _r.ReadSingle());
    }

    public static class Compression
    {
        public static byte[] Pack(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        public static byte[] Unpack(byte[] data)
        {
            using var src = new MemoryStream(data);
            using var z = new DeflateStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream();
            z.CopyTo(dst);
            return dst.ToArray();
        }
    }
}
