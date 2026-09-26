using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SteamPassBridge
{
    // Minimal reader/writer for Steam's binary KeyValues format (shortcuts.vdf).
    // Unknown value types are rejected rather than guessed, so we never corrupt the file.
    class VdfNode
    {
        public const byte Map = 0x00, Str = 0x01, Int = 0x02, Float = 0x03, UInt64 = 0x07, End = 0x08;

        public byte Type;
        public string Key;
        public object Value; // string | int | float | ulong | List<VdfNode>

        public VdfNode(byte type, string key, object value) { Type = type; Key = key; Value = value; }

        public List<VdfNode> Children { get { return (List<VdfNode>)Value; } }

        public VdfNode Get(string key)
        {
            foreach (var c in Children)
                if (string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        public string GetString(string key)
        {
            var n = Get(key);
            return n != null && n.Value is string ? (string)n.Value : null;
        }

        public void Set(string key, byte type, object value)
        {
            var n = Get(key);
            if (n == null) Children.Add(new VdfNode(type, key, value));
            else { n.Type = type; n.Value = value; }
        }

        public static VdfNode Load(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return new VdfNode(Map, "shortcuts", new List<VdfNode>());
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                byte t = r.ReadByte();
                if (t != Map) throw new InvalidDataException("shortcuts.vdf does not start with a map");
                string key = ReadCString(r);
                return new VdfNode(Map, key, ReadMap(r));
            }
        }

        static List<VdfNode> ReadMap(BinaryReader r)
        {
            var list = new List<VdfNode>();
            while (true)
            {
                byte t = r.ReadByte();
                if (t == End || t == 0x0B) return list;
                string key = ReadCString(r);
                switch (t)
                {
                    case Map: list.Add(new VdfNode(t, key, ReadMap(r))); break;
                    case Str: list.Add(new VdfNode(t, key, ReadCString(r))); break;
                    case Int: list.Add(new VdfNode(t, key, r.ReadInt32())); break;
                    case Float: list.Add(new VdfNode(t, key, r.ReadSingle())); break;
                    case UInt64: list.Add(new VdfNode(t, key, r.ReadUInt64())); break;
                    default: throw new InvalidDataException("Unsupported VDF value type 0x" + t.ToString("X2"));
                }
            }
        }

        static string ReadCString(BinaryReader r)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = r.ReadByte()) != 0) bytes.Add(b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public void Save(string path)
        {
            string tmp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                Write(w, this);
                w.Write(End);
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        static void Write(BinaryWriter w, VdfNode n)
        {
            w.Write(n.Type);
            WriteCString(w, n.Key);
            switch (n.Type)
            {
                case Map:
                    foreach (var c in n.Children) Write(w, c);
                    w.Write(End);
                    break;
                case Str: WriteCString(w, (string)n.Value); break;
                case Int: w.Write((int)n.Value); break;
                case Float: w.Write((float)n.Value); break;
                case UInt64: w.Write((ulong)n.Value); break;
            }
        }

        static void WriteCString(BinaryWriter w, string s)
        {
            w.Write(Encoding.UTF8.GetBytes(s ?? ""));
            w.Write((byte)0);
        }
    }
}
