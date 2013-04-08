using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;

namespace O5M
{
    struct Boundary
    {
        public Int64 minLat;
        public Int64 minLon;
        public Int64 maxLat;
        public Int64 maxLon;
    }

    struct FileInfo
    {
        public Boundary bounds;
    }

    struct Author
    {
        public string id;
        public string name;
    }

    struct Header
    {
        public Int64 version;
        public Int64 timestamp;
        public Int64 changeset;
        public Author author;
    }

    struct Node
    {
        public Int64 id;
        public Header header;

        public Int64 lat;
        public Int64 lon;

        public Dictionary<string, string> tags;
    }

    struct Way
    {
        public Int64 id;
        public Header header;

        public List<Int64> refs;
        public Dictionary<string, string> tags;
    }

    // this could be reorganized into a static class that returns a sort of
    // 'ParsedData' instead, i.e. 'public ParsedData Parse(string file)',
    // where ParsedData contains the O5MParser member list data and fileInfo
    public class O5MParser
    {
        string file;
        FileInfo fileInfo;

        List<Node> nodes;
        List<Way> ways;

        List<string> stringTable;
        Dictionary<string, Int64> deltas;

        public O5MParser(string file)
        {
            this.file = file;

            stringTable = new List<string>();
            deltas = new Dictionary<string, long>();

            nodes = new List<Node>();
            ways = new List<Way>();
        }

        public bool Parse()
        {
            bool success = false;

            using (var fs = new FileStream(file, FileMode.Open))
            using (var br = new BinaryReader(fs))
            {
                // check header and parse if possible
                if (ValidateHeader(br) && ParseFile(br))
                    success = true;
            }

            return success;
        }

        private bool ValidateHeader(BinaryReader br)
        {
            // expected header: ff e0 046f 356d 32
            //     reset flag --^^ ^^^^^^^^^^^^^^^-- header
            if (br.ReadBytes(7).SequenceEqual(new byte[] { 0xFF, 0xE0, 0x04, 0x6F, 0x35, 0x6D, 0x32 }))
                return true;
            return false;
        }

        private bool ParseFile(BinaryReader br)
        {
            bool reading = true;

            while (reading && br.BaseStream.Position < br.BaseStream.Length)
            {
                byte b = br.ReadByte();

                switch (b)
                {
                    case 0xFF: // reset
                        stringTable.Clear();
                        deltas.Clear();
                        break;

                    case 0xDB:
                        if (!ParseBoundary(br))
                            return false;
                        break;

                    case 0x10:
                        if (!ParseNode(br))
                            return false;
                        break;

                    case 0x11:
                        if (!ParseWay(br))
                            return false;
                        break;
                }
            }

            return true;
        }

        private bool ParseWay(BinaryReader br)
        {
            int bytes = 0;
            int bytesSoFar = 0;

            Int64 len = ParseNumber(br, false);
            Int64 id = ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytes), "wayid");
            bytesSoFar += bytes;

            // bytes...
            var way = ParseWayContents(br, bytesSoFar, (int)len);
            way.id = id;
            ways.Add(way);

            return true;
        }

        private Way ParseWayContents(BinaryReader br, int bytesSoFar, int bytesTotal)
        {
            int bytes = 0;

            var way = new Way();
            way.header = ParseHeader(br, out bytes);
            bytesSoFar += bytes;

            // reference parsing
            way.refs = ParseWayRefs(br, out bytes);
            bytesSoFar += bytes;

            way.tags = ParseTags(br, bytesSoFar, bytesTotal);

            return way;
        }

        private List<Int64> ParseWayRefs(BinaryReader br, out int bytes)
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
                    var refNum = ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytesRead), "noderefs");
                    totalBytes += bytesRead;
                    bytes += bytesRead;
                    refs.Add(refNum);
                }
            }

            return refs;
        }

        private bool ParseNode(BinaryReader br)
        {
            int bytes = 0;

            Int64 len = ParseNumber(br, false);
            Int64 id = ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytes), "nodeid");

            var node = ParseNodeContents(br, bytes, (int)len);
            node.id = id;
            nodes.Add(node);

            return true;
        }

        private Node ParseNodeContents(BinaryReader br, int bytesSoFar, int bytesTotal)
        {
            int bytes = 0;

            var node = new Node();
            node.header = ParseHeader(br, out bytes);
            bytesSoFar += bytes;

            node.lon = ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytes), "nodelon");
            bytesSoFar += bytes;

            node.lat = ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytes), "nodelat");
            bytesSoFar += bytes;

            node.tags = ParseTags(br, bytesSoFar, bytesTotal);

            return node;
        }

        private Dictionary<string, string> ParseTags(BinaryReader br, int bytesSoFar, int bytesTotal)
        {
            int bytes = 0;
            var tags = new Dictionary<string, string>();

            while (bytesSoFar < bytesTotal)
            {
                if (br.PeekChar() == 0x00)
                {
                    var tag = ParseStringPair(br, out bytes);
                    bytesSoFar += bytes;

                    var splitTag = tag.Split('\0');
                    tags[splitTag[0]] = splitTag[1];
                    if (!stringTable.Contains(tag))
                        stringTable.Add(tag);
                }
                else
                {
                    var entry = ParseNumberAndTrackBytes(br, false, out bytes);
                    bytesSoFar += bytes;

                    var tag = stringTable[stringTable.Count - Convert.ToInt32(entry)];
                    var splitTag = tag.Split('\0');
                    tags[splitTag[0]] = splitTag[1];
                }
            }

            return tags;
        }

        private Header ParseHeader(BinaryReader br, out int bytes)
        {
            bytes = 0;
            int bytesRead = 0;

            var header = new Header();

            header.version = ParseNumberAndTrackBytes(br, false, out bytesRead);
            bytes += bytesRead;

            if (header.version != 0)
            {
                header.timestamp = ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytesRead), "nodetimestamp"); // seconds from 1970
                bytes += bytesRead;

                // specs unclear if you filter author with timestamp or version
                //if (header.timestamp != 0)
                //{
                header.changeset = ApplyDelta(ParseNumberAndTrackBytes(br, true, out bytesRead), "nodechangeset");
                bytes += bytesRead;

                var authorInfo = ParseUIDStringAndTrackBytes(br, out bytesRead).Split('\0'); // [0] uid, [1] user name
                bytes += bytesRead;

                header.author.id = authorInfo[0];
                header.author.name = authorInfo[1];
                //}
            }

            return header;
        }

        private bool ParseBoundary(BinaryReader br)
        {
            // len, minlon, minlat, maxlon, maxlat
            Int64 len = ParseNumber(br, false);
            fileInfo.bounds.minLon = ParseNumber(br, true);
            fileInfo.bounds.minLat = ParseNumber(br, true);
            fileInfo.bounds.maxLon = ParseNumber(br, true);
            fileInfo.bounds.maxLat = ParseNumber(br, true);

            return true;
        }

        private string ParseStringPair(BinaryReader br, out int bytes)
        {
            var byteStr = new List<byte>();
            bytes = 0;

            if (br.PeekChar() != 0x00) // table entry
            {
                var entry = ParseNumberAndTrackBytes(br, false, out bytes);
                return stringTable[stringTable.Count - Convert.ToInt32(entry)];
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
            bytes++;

            while ((curr = br.ReadByte()) != 0x00)
            {
                byteStr.Add(curr);
                bytes++;
            }
            bytes++; // to catch the 0x00

            var retString = Encoding.UTF8.GetString(byteStr.ToArray(), 0, byteStr.Count);
            if (!stringTable.Contains(retString))
                stringTable.Add(retString);

            return retString;
        }

        private string ParseUIDStringAndTrackBytes(BinaryReader br, out int bytes)
        {
            var byteStr = new List<byte>();
            bytes = 0;
            int bytesRead = 0;

            if (br.PeekChar() != 0x00) // table entry
            {
                var entry = ParseNumberAndTrackBytes(br, false, out bytes);
                return stringTable[stringTable.Count - Convert.ToInt32(entry)];
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
            if (!stringTable.Contains(uidString))
                stringTable.Add(uidString);

            return uidString;
        }

        private string ParseUIDString(BinaryReader br)
        {
            int bytes = 0;
            return ParseUIDStringAndTrackBytes(br, out bytes);
        }

        // convenience function for parsing numbers
        private Int64 ParseNumber(BinaryReader br, bool signed)
        {
            int bytes = 0;
            return ParseNumberAndTrackBytes(br, signed, out bytes);
        }

        private Int64 ParseNumberAndTrackBytes(BinaryReader br, bool signed, out int place)
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

        private Int64 Int64FromBytes(byte[] bytes, int places, bool signed)
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

        private Int64 ApplyDelta(Int64 val, string key)
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
}
