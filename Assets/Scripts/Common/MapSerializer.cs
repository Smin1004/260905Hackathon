using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

/// <summary>
/// MapData ↔ 바이트 직렬화 (Docs/203 5장, Docs/205 5장).
///
/// 전송 포맷 = 양자화 바이너리 + GZip.
///   - 좌표는 0.01u 단위 int16 (0~30u → 0~3000), 점당 4바이트
///   - 60 스트로크 × 300점 상한에서 압축 전 ≤ 72KB, 압축 후 보통 그 절반 이하 → 목표 100KB 안
/// JSON(JsonUtility)은 디버그·로그 확인용으로만 제공한다 (점당 ~20B라 전송에는 부적합).
///
/// 네트워크 담당은 Serialize() 결과를 MapChunker.Split()로 나눠 보내고, 수신측은 MapChunkAssembler로 조립해 Deserialize() 한다.
/// </summary>
public static class MapSerializer
{
    // 'C','J','M' + 포맷 버전
    static readonly byte[] Magic = { (byte)'C', (byte)'J', (byte)'M', 1 };

    const float InvQ = 1f / MapConstants.Quantization;
    const float WidthScale = 1000f;   // 0.15u → 150

    public static byte[] Serialize(MapData map)
    {
        if (map == null) throw new ArgumentNullException(nameof(map));

        using var raw = new MemoryStream();
        using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            WriteVec(w, map.StartPos);
            WriteVec(w, map.HasGoal ? map.GoalPos : new Vector2(-1f, -1f));
            w.Write((ushort)map.Strokes.Count);
            foreach (var s in map.Strokes)
            {
                w.Write((byte)Mathf.Clamp(s.ColorId, 0, 255));
                w.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(s.Width * WidthScale), 0, ushort.MaxValue));
                w.Write((ushort)s.Points.Count);
                foreach (var p in s.Points) WriteVec(w, p);
            }
        }

        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw.GetBuffer(), 0, (int)raw.Length);
        return packed.ToArray();
    }

    public static MapData Deserialize(byte[] data)
    {
        if (data == null || data.Length == 0) throw new ArgumentException("empty payload");

        using var packed = new MemoryStream(data);
        using var gz = new GZipStream(packed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        gz.CopyTo(raw);
        raw.Position = 0;

        using var r = new BinaryReader(raw);
        var magic = r.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2])
            throw new InvalidDataException("not a map payload");
        if (magic[3] != Magic[3])
            throw new InvalidDataException($"unsupported map format version {magic[3]}");

        var map = new MapData();
        map.StartPos = ReadVec(r);
        map.GoalPos = ReadVec(r);
        int strokeCount = r.ReadUInt16();
        for (int i = 0; i < strokeCount; i++)
        {
            var s = new StrokeData
            {
                ColorId = r.ReadByte(),
                Width = r.ReadUInt16() / WidthScale,
            };
            int n = r.ReadUInt16();
            s.Points.Capacity = n;
            for (int p = 0; p < n; p++) s.Points.Add(ReadVec(r));
            map.Strokes.Add(s);
        }
        return map;
    }

    /// <summary>압축 전 예상 크기 (UI 표시용, 빠름). 실제 전송 크기는 Serialize().Length.</summary>
    public static int EstimateRawBytes(MapData map)
    {
        int n = 4 + 8 + 2;
        foreach (var s in map.Strokes) n += 1 + 2 + 2 + s.Points.Count * 4;
        return n;
    }

    /// <summary>디버그용 JSON.</summary>
    public static string ToJson(MapData map, bool pretty = false) => JsonUtility.ToJson(map, pretty);
    public static MapData FromJson(string json) => JsonUtility.FromJson<MapData>(json);

    static void WriteVec(BinaryWriter w, Vector2 v)
    {
        w.Write((short)Mathf.Clamp(Mathf.RoundToInt(v.x * InvQ), short.MinValue, short.MaxValue));
        w.Write((short)Mathf.Clamp(Mathf.RoundToInt(v.y * InvQ), short.MinValue, short.MaxValue));
    }

    static Vector2 ReadVec(BinaryReader r)
    {
        return new Vector2(r.ReadInt16() * MapConstants.Quantization, r.ReadInt16() * MapConstants.Quantization);
    }
}

/// <summary>페이로드를 네트워크 청크로 나누고 조립한다 (Docs/205 5장 MapChunk 메시지).</summary>
public static class MapChunker
{
    public static List<byte[]> Split(byte[] data, int chunkSize = MapConstants.NetworkChunkSize)
    {
        var list = new List<byte[]>();
        if (data == null || data.Length == 0) return list;
        for (int off = 0; off < data.Length; off += chunkSize)
        {
            int len = Mathf.Min(chunkSize, data.Length - off);
            var c = new byte[len];
            Buffer.BlockCopy(data, off, c, 0, len);
            list.Add(c);
        }
        return list;
    }

    public static int ChunkCount(int totalBytes, int chunkSize = MapConstants.NetworkChunkSize)
        => totalBytes <= 0 ? 0 : (totalBytes + chunkSize - 1) / chunkSize;
}

/// <summary>수신측 청크 조립기. 순서가 보장되는 전달(ReliableSequenced)을 전제로 하지만 순서가 바뀌어도 동작한다.</summary>
public class MapChunkAssembler
{
    byte[][] _parts;
    int _received;

    public bool IsComplete => _parts != null && _received == _parts.Length;
    public int Expected => _parts?.Length ?? 0;
    public int Received => _received;

    /// <returns>이 청크로 조립이 완성되면 true.</returns>
    public bool Add(int index, int count, byte[] bytes)
    {
        if (count <= 0 || index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_parts == null || _parts.Length != count) { _parts = new byte[count][]; _received = 0; }
        if (_parts[index] == null) _received++;
        _parts[index] = bytes;
        return IsComplete;
    }

    public byte[] Assemble()
    {
        if (!IsComplete) throw new InvalidOperationException("chunks incomplete");
        int total = 0;
        foreach (var p in _parts) total += p.Length;
        var all = new byte[total];
        int off = 0;
        foreach (var p in _parts) { Buffer.BlockCopy(p, 0, all, off, p.Length); off += p.Length; }
        return all;
    }

    public void Reset() { _parts = null; _received = 0; }
}
