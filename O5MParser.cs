using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;

namespace O5M
{
    public struct Boundary
    {
        public Int64 minLat;
        public Int64 minLon;
        public Int64 maxLat;
        public Int64 maxLon;
    }

    // only  contains bounds for now, but there are other fields that could
    // be stored here when implemented
    public struct FileInfo
    {
        public Boundary bounds;
    }

    public struct Author
    {
        public string id;
        public string name;
    }

    public struct Header
    {
        public Int64 version;
        public Int64 timestamp;
        public Int64 changeset;
        public Author author;
    }

    public struct Node
    {
        public Int64 id;
        public Header header;

        public Int64 lat;
        public Int64 lon;

        public Dictionary<string, string> tags;
    }

    public struct Way
    {
        public Int64 id;
        public Header header;

        public List<Int64> refs;
        public Dictionary<string, string> tags;
    }

    public class ParsedData
    {
        public bool Complete { get; internal set; }
        public FileInfo Details { get; internal set; }
        public List<Node> Nodes { get; internal set; }
        public List<Way> Ways { get; internal set; }

        // I can't recall if StringTable being public is necessary,
        // I've left it this way just in case.
        public List<string> StringTable { get; internal set; }

        private Dictionary<string, Int64> deltas;

        public ParsedData()
        {
            Complete = false;
            Details = new FileInfo();
            Nodes = new List<Node>();
            Ways = new List<Way>();
            StringTable = new List<string>();

            deltas = new Dictionary<string, long>();
        }

        internal void ClearTables()
        {
            StringTable.Clear();
            deltas.Clear();
        }

        internal Int64 ApplyDelta(Int64 val, string key)
        {
            if (val == 0)
                return val;

            if (deltas.ContainsKey(key))
            {
                var newVal = val + deltas[key];
                deltas[key] = newVal;
                return newVal;
            }
            else
                deltas[key] = val;

            return val;
        }
    }

    // main class, call O5M.O5MPaser.Parse to get the ParsedData from a .o5m file
    public static class O5MParser
    {
        public static ParsedData Parse(string file)
        {
            var parsed = new ParsedData();

            bool success = false;

            using (var fs = new FileStream(file, FileMode.Open))
            using (var br = new BinaryReader(fs))
            {
                // check header and parse if possible
                if (ValidateHeader(br) && ParseFile(br, parsed))
                    success = true;
            }

            parsed.Complete = success;
            return parsed;
        }

        private static bool ValidateHeader(BinaryReader br)
        {
            // expected header: ff e0 046f 356d 32
            //     reset flag --^^ ^^^^^^^^^^^^^^^-- header
            if (br.ReadBytes(7).SequenceEqual(new byte[] { 0xFF, 0xE0, 0x04, 0x6F, 0x35, 0x6D, 0x32 }))
                return true;
            return false;
        }

        private static bool ParseFile(BinaryReader br, ParsedData parsed)
        {
            bool reading = true;

            while (reading && br.BaseStream.Position < br.BaseStream.Length)
            {
                byte b = br.ReadByte();

                switch (b)
                {
                    case 0xFF: // reset
                        parsed.ClearTables();
                        break;

                    case 0xDB:
                        if (!ParseBoundary(br, parsed))
                            return false;
                        break;

                    case 0x10:
                        if (!ParseNode(br, parsed))
                            return false;
                        break;

                    case 0x11:
                        if (!ParseWay(br, parsed))
                            return false;
                        break;
                }
            }

            return true;
        }

        private static bool ParseWay(BinaryReader br, ParsedData parsed)
        {
            int bytes = 0;
            int bytesSoFar = 0;

            Int64 len = ParseNumber(br, false);
            Int64 id = parsed.ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytes), "wayid");
            bytesSoFar += bytes;

            // bytes...
            var way = ParseWayContents(br, parsed, bytesSoFar, (int)len);
            way.id = id;
            parsed.Ways.Add(way);

            return true;
        }

        private static Way ParseWayContents(BinaryReader br, ParsedData parsed, int bytesSoFar, int bytesTotal)
        {
            int bytes = 0;

            var way = new Way();
            way.header = ParseHeader(br, parsed, out bytes);
            bytesSoFar += bytes;

            // reference parsing
            way.refs = ParseWayRefs(br, parsed, out bytes);
            bytesSoFar += bytes;

            way.tags = ParseTags(br, parsed, bytesSoFar, bytesTotal);

            return way;
        }

        private static List<Int64> ParseWayRefs(BinaryReader br, ParsedData parsed, out int bytes)
        {
            bytes = 0;
            int bytesRead = 0;
            var refs = new List<Int64>();

            var refLen = ParseNumberAndTrackBytes(br, false, out bytesRead);
            bytes += bytesRead;

            if (refLen > 0) // parse refs
            {
                Int64 totalBytes = 0;
                while (totalBytes < refLen)
                {
                    var refNum = parsed.ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytesRead), "noderefs");
                    totalBytes += bytesRead;
                    bytes += bytesRead;
                    refs.Add(refNum);
                }
            }

            return refs;
        }

        private static bool ParseNode(BinaryReader br, ParsedData parsed)
        {
            int bytes = 0;

            Int64 len = ParseNumber(br, false);
            Int64 id = parsed.ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytes), "nodeid");

            var node = ParseNodeContents(br, parsed, bytes, (int)len);
            node.id = id;
            parsed.Nodes.Add(node);

            return true;
        }

        private static Node ParseNodeContents(BinaryReader br, ParsedData parsed, int bytesSoFar, int bytesTotal)
        {
            int bytes = 0;

            var node = new Node();
            node.header = ParseHeader(br, parsed, out bytes);
            bytesSoFar += bytes;

            node.lon = parsed.ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytes), "nodelon");
            bytesSoFar += bytes;

            node.lat = parsed.ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytes), "nodelat");
            bytesSoFar += bytes;

            node.tags = ParseTags(br, parsed, bytesSoFar, bytesTotal);

            return node;
        }

        private static Dictionary<string, string> ParseTags(BinaryReader br, ParsedData parsed, int bytesSoFar, int bytesTotal)
        {
            int bytes = 0;
            var tags = new Dictionary<string, string>();

            while (bytesSoFar < bytesTotal)
            {
                if (br.PeekChar() == 0x00)
                {
                    var tag = ParseStringPair(br, parsed, out bytes);
                    bytesSoFar += bytes;

                    var splitTag = tag.Split('\0');
                    tags[splitTag[0]] = splitTag[1];
                    if (!parsed.StringTable.Contains(tag))
                        parsed.StringTable.Add(tag);
                }
                else
                {
                    var entry = ParseNumberAndTrackBytes(br, false, out bytes);
                    bytesSoFar += bytes;

                    var tag = parsed.StringTable[parsed.StringTable.Count - Convert.ToInt32(entry)];
                    var splitTag = tag.Split('\0');
                    tags[splitTag[0]] = splitTag[1];
                }
            }

            return tags;
        }

        private static Header ParseHeader(BinaryReader br, ParsedData parsed, out int bytes)
        {
            bytes = 0;
            int bytesRead = 0;

            var header = new Header();

            header.version = ParseNumberAndTrackBytes(br, false, out bytesRead);
            bytes += bytesRead;

            if (header.version != 0)
            {
                header.timestamp = parsed.ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytesRead), "nodetimestamp"); // seconds from 1970
                bytes += bytesRead;

                // specs unclear if you filter author with timestamp or version
                //if (header.timestamp != 0)
                //{
                header.changeset = parsed.ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytesRead), "nodechangeset");
                bytes += bytesRead;

                var authorInfo = ParseUIDStringAndTrackBytes(br, parsed, out bytesRead).Split('\0'); // [0] uid, [1] user name
                bytes += bytesRead;

                header.author.id = authorInfo[0];
                header.author.name = authorInfo[1];
                //}
            }

            return header;
        }

        private static bool ParseBoundary(BinaryReader br, ParsedData parsed)
        {
            // len, minlon, minlat, maxlon, maxlat
            Int64 len = ParseNumber(br, false);

            var details = new FileInfo();
            details.bounds.minLon = ParseNumber(br, true);
            details.bounds.minLat = ParseNumber(br, true);
            details.bounds.maxLon = ParseNumber(br, true);
            details.bounds.maxLat = ParseNumber(br, true);
            parsed.Details = details;

            return true;
        }

        private static string ParseStringPair(BinaryReader br, ParsedData parsed, out int bytes)
        {
            var byteStr = new List<byte>();
            bytes = 0;

            if (br.PeekChar() != 0x00) // table entry
            {
                var entry = ParseNumberAndTrackBytes(br, false, out bytes);
                return parsed.StringTable[parsed.StringTable.Count - Convert.ToInt32(entry)];
            }

            br.ReadByte();
            bytes++;

            byte curr;
            while ((curr = br.ReadByte()) != 0x00)
            {
                byteStr.Add(curr);
                bytes++;
            }
            bytes++; // to catch the 0x00

            byteStr.Add(0x00);

            while ((curr = br.ReadByte()) != 0x00)
            {
                byteStr.Add(curr);
                bytes++;
            }
            bytes++; // to catch the 0x00

            var retString = Encoding.UTF8.GetString(byteStr.ToArray(), 0, byteStr.Count);
            if (!parsed.StringTable.Contains(retString))
                parsed.StringTable.Add(retString);

            return retString;
        }

        private static string ParseUIDStringAndTrackBytes(BinaryReader br, ParsedData parsed, out int bytes)
        {
            var byteStr = new List<byte>();
            bytes = 0;
            int bytesRead = 0;

            if (br.PeekChar() != 0x00) // table entry
            {
                var entry = ParseNumberAndTrackBytes(br, false, out bytes);
                return parsed.StringTable[parsed.StringTable.Count - Convert.ToInt32(entry)];
            }

            br.ReadByte(); // 0x00
            bytes++;

            byte curr;
            var uid = ParseNumberAndTrackBytes(br, false, out bytesRead).ToString();
            bytes += bytesRead;

            br.ReadByte(); // 0x00
            bytes++;

            while ((curr = br.ReadByte()) != 0x00)
            {
                byteStr.Add(curr);
                bytes++;
            }
            bytes++; // to catch the 0x00

            var uidString = uid + '\0' + Encoding.UTF8.GetString(byteStr.ToArray(), 0, byteStr.Count);
            if (!parsed.StringTable.Contains(uidString))
                parsed.StringTable.Add(uidString);

            return uidString;
        }

        private static string ParseUIDString(BinaryReader br, ParsedData parsed)
        {
            int bytes = 0;
            return ParseUIDStringAndTrackBytes(br, parsed, out bytes);
        }

        // convenience function for parsing numbers
        private static Int64 ParseNumber(BinaryReader br, bool signed)
        {
            int bytes = 0;
            return ParseNumberAndTrackBytes(br, signed, out bytes);
        }

        private static Int64 ParseNumberAndTrackBytes(BinaryReader br, bool signed, out int place)
        {
            Int64 res = 0;
            var done = false;
            var bytes = new byte[8];
            var first = true;
            bool neg = false;
            place = 0;

            while (!done)
            {
                var curr = br.ReadByte();

                // most sig bit 1 is continuation bit (1 = next byte also part of number)
                if ((curr & 0x80) == 0)
                    done = true;

                // remove continuation bit, not part of the number
                curr &= 0x7F;

                // least sig bit of first byte is sign bit (1 = negative)
                if (signed && first)
                {
                    if ((curr & 0x01) == 1)
                        neg = true;
                    first = false;

                    // remove sign bit
                    curr >>= 1;
                }

                // store current byte and move to next place
                bytes[place] = curr;
                place++;
            }

            // convert byte array into int64 and check negation
            res = Int64FromBytes(bytes, place, signed);
            if (neg)
                res = -res - 1;

            return res;
        }

        private static Int64 Int64FromBytes(byte[] bytes, int places, bool signed)
        {
            Int64 ret = 0;
            var shift = 0;

            // here be dragons
            for (int i = 0; i < places; i++)
            {
                ret |= ((Int64)bytes[i] << shift);
                shift += ((signed && i == 0) ? 6 : 7); // sign bit must be shifted away
            }

            return ret;
        }
    }
}
