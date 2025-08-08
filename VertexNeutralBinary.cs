// VertexNeutral v2 — Unity-only clean file (Exporter + Importer + Simple API)
// Target: .NET Framework 4.7, Unity 2020+ (works with newer too)
// Notes:
// - Little-endian throughout. No external libs. Safe code only (no unsafe blocks required).
// - Everything (textures, PBR props, colors) is optional via flags.
// - Simple API: VNB.Import(byte[]) -> GameObject, VNB.Export(GameObject) -> byte[]
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace VertexNeutralBinary
{
    #region Flags / enums
    [Flags]
    public enum GlobalFlags : uint
    {
        HasPositions = 1u << 0,
        HasNormals = 1u << 1,
        HasTangents = 1u << 2,
        HasVertexColors = 1u << 3,
        HasUV0 = 1u << 4,
        HasUV1 = 1u << 5,
        HasBounds = 1u << 6,
        IndicesU16 = 1u << 7,
        EmbedTextures = 1u << 8,
    }

    [Flags]
    public enum PbrFlags : uint
    {
        BaseColorFactor = 1u << 0,
        MetallicFactor = 1u << 1,
        RoughnessFactor = 1u << 2,
        EmissiveFactor = 1u << 3,
        AlphaMode = 1u << 4,
        DoubleSided = 1u << 5,
        BaseColorTex = 1u << 6,
        MetalRoughTex = 1u << 7,
        NormalTex = 1u << 8,
        OcclusionTex = 1u << 9,
        EmissiveTex = 1u << 10,
        TilingOffset = 1u << 11,
        Sampler = 1u << 12,
    }

    public enum Topology : byte { Triangles = 0, Lines = 1 }
    public enum TexSlot : byte { BaseColor = 0, MetalRough = 1, Normal = 2, Occlusion = 3, Emissive = 4 }
    public enum UriKindVN : byte { External = 0, Embedded = 1 }
    public enum AlphaMode : byte { Opaque = 0, Mask = 1, Blend = 2 }
    public enum MimeKind : byte { PNG = 0, JPG = 1, KTX2 = 2 }
    #endregion

    #region POD structs (binary layout helpers)
    internal struct Header
    {
        public uint Magic;      // 'VNB2' = 0x564E4232
        public ushort Version;  // 2
        public byte Endianness; // 0 little
        public byte CoordSys;   // 0 = Unity Y-up
        public float UnitScale; // 1.0
        public GlobalFlags GlobalFlags;
        public uint VertexCount;
        public uint IndexCount;
        public uint SubMeshCount;
        public uint MaterialCount;
        public byte[] Reserved; // 16 bytes
    }

    public struct SubMeshDescVN
    {
        public Topology Topology;
        public ushort MaterialIndex; // 0xFFFF = none
        public uint StartIndex;
        public uint IndexCount;
        public int BaseVertex;
        public uint FirstVertex;
        public uint VertexCount;
    }

    public struct SamplerVN
    {
        public byte WrapU; // 0=Repeat,1=Clamp,2=Mirror
        public byte WrapV;
        public byte MinFilter; // 0=Point,1=Bilinear,2=Trilinear
        public byte MagFilter;
    }

    public struct TexRefVN
    {
        public TexSlot Slot;
        public byte UVSet; // 0 or 1
        public UriKindVN RefType; // External or Embedded
        public bool HasOffset; public bool HasScale; public bool HasRotation;
        public SamplerVN? Sampler; // null if absent
        public string ExternalUri; // if External
        public MimeKind EmbeddedMime; // if Embedded
        public byte[] EmbeddedData; // if Embedded
        public float OffsetX, OffsetY; // opt
        public float ScaleX, ScaleY; // opt (Unity default 1,1)
        public float Rotation; // opt (radians)
    }

    public class PbrMaterialVN
    {
        public string Name;
        public PbrFlags Flags;
        public UnityColor BaseColorFactor = UnityColor.White;
        public float MetallicFactor = 0.0f;
        public float RoughnessFactor = 1.0f; // Unity smoothness = 1-roughness
        public UnityColor EmissiveFactor = UnityColor.BlackRGB;
        public AlphaMode AlphaMode = AlphaMode.Opaque;
        public float AlphaCutoff = 0.5f; // if Mask
        public bool DoubleSided = false;
        public List<TexRefVN> Textures = new List<TexRefVN>();
    }

    // Lightweight color to avoid UnityEngine dependency in pure serialization path
    public struct UnityColor
    {
        public float r, g, b, a;
        public UnityColor(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static UnityColor White => new UnityColor(1, 1, 1, 1);
        public static UnityColor BlackRGB => new UnityColor(0, 0, 0, 1);
    }
    #endregion

    #region Container model for export/import
    public class VnMeshData
    {
        public string Name;
        public GlobalFlags Flags;
        public float[] Positions;   // length = vtx*3, optional
        public float[] Normals;     // length = vtx*3, optional
        public float[] Tangents;    // length = vtx*4, optional
        public float[] Colors;      // length = vtx*4, optional (RGBA)
        public float[] UV0;         // length = vtx*2, optional
        public float[] UV1;         // length = vtx*2, optional
        public float[] BoundsMin;   // length = 3, optional
        public float[] BoundsMax;   // length = 3, optional
        public Array Indices;       // ushort[] or uint[] depending on Flags.IndicesU16
        public List<SubMeshDescVN> SubMeshes = new List<SubMeshDescVN>();
        public List<PbrMaterialVN> Materials = new List<PbrMaterialVN>();
        public uint VertexCount;
        public uint IndexCount;
    }
    #endregion

    #region Binary IO helpers
    static class IO
    {
        public static void Write(this BinaryWriter bw, Header h)
        {
            bw.Write(h.Magic);
            bw.Write(h.Version);
            bw.Write(h.Endianness);
            bw.Write(h.CoordSys);
            bw.Write(h.UnitScale);
            bw.Write((uint)h.GlobalFlags);
            bw.Write(h.VertexCount);
            bw.Write(h.IndexCount);
            bw.Write(h.SubMeshCount);
            bw.Write(h.MaterialCount);
            var pad = h.Reserved ?? new byte[16];
            if (pad.Length != 16) Array.Resize(ref pad, 16);
            bw.Write(pad);
        }

        public static Header ReadHeader(BinaryReader br)
        {
            var h = new Header
            {
                Magic = br.ReadUInt32(),
                Version = br.ReadUInt16(),
                Endianness = br.ReadByte(),
                CoordSys = br.ReadByte(),
                UnitScale = br.ReadSingle(),
                GlobalFlags = (GlobalFlags)br.ReadUInt32(),
                VertexCount = br.ReadUInt32(),
                IndexCount = br.ReadUInt32(),
                SubMeshCount = br.ReadUInt32(),
                MaterialCount = br.ReadUInt32(),
                Reserved = br.ReadBytes(16)
            };
            return h;
        }

        public static void WriteArray(this BinaryWriter bw, float[] arr) { if (arr != null) foreach (var f in arr) bw.Write(f); }
        public static void WriteArray(this BinaryWriter bw, ushort[] arr) { if (arr != null) foreach (var v in arr) bw.Write(v); }
        public static void WriteArray(this BinaryWriter bw, uint[] arr) { if (arr != null) foreach (var v in arr) bw.Write(v); }
        public static float[] ReadFloatArray(BinaryReader br, int count)
        {
            var bytes = br.ReadBytes(count * 4);
            var arr = new float[count];
            Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
            return arr;
        }
        public static ushort[] ReadU16Array(BinaryReader br, int count)
        {
            var bytes = br.ReadBytes(count * 2);
            var arr = new ushort[count];
            Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
            return arr;
        }
        public static uint[] ReadU32Array(BinaryReader br, int count)
        {
            var bytes = br.ReadBytes(count * 4);
            var arr = new uint[count];
            Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
            return arr;
        }
        public static string ReadUtf8(BinaryReader br) { var len = br.ReadUInt16(); var bytes = br.ReadBytes(len); return Encoding.UTF8.GetString(bytes); }
        public static void WriteUtf8(this BinaryWriter bw, string s) { var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty); if (bytes.Length > ushort.MaxValue) throw new InvalidOperationException("String too long"); bw.Write((ushort)bytes.Length); bw.Write(bytes); }
    }
    #endregion

    #region Exporter
    public static class VnExporter
    {
        public static byte[] Export(VnMeshData m)
        {
            if ((m.Flags & GlobalFlags.HasPositions) == 0) throw new InvalidOperationException("Positions are required.");
            m.VertexCount = (uint)(m.Positions.Length / 3);
            m.IndexCount = (uint)((m.Flags & GlobalFlags.IndicesU16) != 0 ? ((ushort[])m.Indices).Length : ((uint[])m.Indices).Length);

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                // Header
                var header = new Header
                {
                    Magic = 0x564E4232, // 'VNB2'
                    Version = 2,
                    Endianness = 0,
                    CoordSys = 0,
                    UnitScale = 1.0f,
                    GlobalFlags = m.Flags,
                    VertexCount = m.VertexCount,
                    IndexCount = m.IndexCount,
                    SubMeshCount = (uint)m.SubMeshes.Count,
                    MaterialCount = (uint)m.Materials.Count,
                    Reserved = new byte[16]
                };
                IO.Write(bw, header);

                // Name
                IO.WriteUtf8(bw, m.Name ?? string.Empty);

                // Vertex buffers
                if ((m.Flags & GlobalFlags.HasPositions) != 0) bw.WriteArray(m.Positions);
                if ((m.Flags & GlobalFlags.HasNormals) != 0) bw.WriteArray(m.Normals);
                if ((m.Flags & GlobalFlags.HasTangents) != 0) bw.WriteArray(m.Tangents);
                if ((m.Flags & GlobalFlags.HasVertexColors) != 0) bw.WriteArray(m.Colors);
                if ((m.Flags & GlobalFlags.HasUV0) != 0) bw.WriteArray(m.UV0);
                if ((m.Flags & GlobalFlags.HasUV1) != 0) bw.WriteArray(m.UV1);
                if ((m.Flags & GlobalFlags.HasBounds) != 0) { bw.WriteArray(m.BoundsMin); bw.WriteArray(m.BoundsMax); }

                // Indices
                if ((m.Flags & GlobalFlags.IndicesU16) != 0) bw.WriteArray((ushort[])m.Indices);
                else bw.WriteArray((uint[])m.Indices);

                // Submeshes
                foreach (var sm in m.SubMeshes)
                {
                    bw.Write((byte)sm.Topology);
                    bw.Write(sm.MaterialIndex);
                    bw.Write(sm.StartIndex);
                    bw.Write(sm.IndexCount);
                    bw.Write(sm.BaseVertex);
                    bw.Write(sm.FirstVertex);
                    bw.Write(sm.VertexCount);
                }

                // Materials
                foreach (var mat in m.Materials)
                {
                    IO.WriteUtf8(bw, mat.Name ?? string.Empty);
                    bw.Write((uint)mat.Flags);

                    if ((mat.Flags & PbrFlags.BaseColorFactor) != 0) { bw.Write(mat.BaseColorFactor.r); bw.Write(mat.BaseColorFactor.g); bw.Write(mat.BaseColorFactor.b); bw.Write(mat.BaseColorFactor.a); }
                    if ((mat.Flags & PbrFlags.MetallicFactor) != 0) bw.Write(mat.MetallicFactor);
                    if ((mat.Flags & PbrFlags.RoughnessFactor) != 0) bw.Write(mat.RoughnessFactor);
                    if ((mat.Flags & PbrFlags.EmissiveFactor) != 0) { bw.Write(mat.EmissiveFactor.r); bw.Write(mat.EmissiveFactor.g); bw.Write(mat.EmissiveFactor.b); }
                    if ((mat.Flags & PbrFlags.AlphaMode) != 0) { bw.Write((byte)mat.AlphaMode); if (mat.AlphaMode == AlphaMode.Mask) bw.Write(mat.AlphaCutoff); }
                    if ((mat.Flags & PbrFlags.DoubleSided) != 0) bw.Write((byte)(mat.DoubleSided ? 1 : 0));

                    // texCount
                    bw.Write((byte)(mat.Textures?.Count ?? 0));

                    if (mat.Textures != null)
                        foreach (var t in mat.Textures)
                        {
                            bw.Write((byte)t.Slot);
                            bw.Write(t.UVSet);
                            bw.Write((byte)t.RefType);

                            // tiling/offset flags packed
                            byte toFlags = 0;
                            if (t.HasOffset) toFlags |= 1;
                            if (t.HasScale) toFlags |= 2;
                            if (t.HasRotation) toFlags |= 4;
                            bw.Write(toFlags);
                            if (t.HasOffset) { bw.Write(t.OffsetX); bw.Write(t.OffsetY); }
                            if (t.HasScale) { bw.Write(t.ScaleX); bw.Write(t.ScaleY); }
                            if (t.HasRotation) { bw.Write(t.Rotation); }

                            // sampler
                            if ((mat.Flags & PbrFlags.Sampler) != 0 && t.Sampler.HasValue)
                            {
                                var s = t.Sampler.Value; bw.Write(s.WrapU); bw.Write(s.WrapV); bw.Write(s.MinFilter); bw.Write(s.MagFilter);
                            }

                            // payload
                            if (t.RefType == UriKindVN.External)
                            {
                                IO.WriteUtf8(bw, t.ExternalUri ?? string.Empty);
                            }
                            else
                            {
                                bw.Write((byte)t.EmbeddedMime);
                                bw.Write((uint)(t.EmbeddedData?.Length ?? 0));
                                if (t.EmbeddedData != null) bw.Write(t.EmbeddedData);
                            }
                        }
                }

                bw.Flush();
                return ms.ToArray();
            }
        }
    }
    #endregion

    #region Importer (Unity integration)
    public class VnImportResult
    {
        public string Name;
        public Mesh Mesh;
        public Material[] Materials;
        public VnMeshData Data; // raw data
    }

    public static class VnImporter
    {
        /// <summary>
        /// Import VN v2 bytes. If magic/version mismatch, tries legacy v1 path (best-effort).
        /// </summary>
        public static VnImportResult Import(byte[] bytes, bool makeMeshReadable, Func<string, byte[]> resolveExternalTexture = null)
        {
            using (var ms = new MemoryStream(bytes))
            using (var br = new BinaryReader(ms))
            {
                Header h;
                try { h = IO.ReadHeader(br); }
                catch { return ImportV1(bytes, makeMeshReadable); }

                if (h.Magic != 0x564E4232 || h.Version != 2)
                {
                    return ImportV1(bytes, makeMeshReadable);
                }

                var flags = h.GlobalFlags;
                var vcount = (int)h.VertexCount;
                var icount = (int)h.IndexCount;

                // get name
                string name = IO.ReadUtf8(br);

                var data = new VnMeshData { Name = name, Flags = flags, VertexCount = h.VertexCount, IndexCount = h.IndexCount };

                if ((flags & GlobalFlags.HasPositions) != 0) data.Positions = IO.ReadFloatArray(br, vcount * 3);
                if ((flags & GlobalFlags.HasNormals) != 0) data.Normals = IO.ReadFloatArray(br, vcount * 3);
                if ((flags & GlobalFlags.HasTangents) != 0) data.Tangents = IO.ReadFloatArray(br, vcount * 4);
                if ((flags & GlobalFlags.HasVertexColors) != 0) data.Colors = IO.ReadFloatArray(br, vcount * 4);
                if ((flags & GlobalFlags.HasUV0) != 0) data.UV0 = IO.ReadFloatArray(br, vcount * 2);
                if ((flags & GlobalFlags.HasUV1) != 0) data.UV1 = IO.ReadFloatArray(br, vcount * 2);
                if ((flags & GlobalFlags.HasBounds) != 0) { data.BoundsMin = IO.ReadFloatArray(br, 3); data.BoundsMax = IO.ReadFloatArray(br, 3); }

                if ((flags & GlobalFlags.IndicesU16) != 0) data.Indices = IO.ReadU16Array(br, icount);
                else data.Indices = IO.ReadU32Array(br, icount);

                // Submeshes
                int smCount = (int)h.SubMeshCount;
                for (int i = 0; i < smCount; i++)
                {
                    var sm = new SubMeshDescVN
                    {
                        Topology = (Topology)br.ReadByte(),
                        MaterialIndex = br.ReadUInt16(),
                        StartIndex = br.ReadUInt32(),
                        IndexCount = br.ReadUInt32(),
                        BaseVertex = br.ReadInt32(),
                        FirstVertex = br.ReadUInt32(),
                        VertexCount = br.ReadUInt32(),
                    };
                    data.SubMeshes.Add(sm);
                }

                // Materials
                int matCount = (int)h.MaterialCount;
                for (int m = 0; m < matCount; m++)
                {
                    name = IO.ReadUtf8(br);
                    var mflags = (PbrFlags)br.ReadUInt32();
                    var pm = new PbrMaterialVN { Name = name, Flags = mflags };

                    if ((mflags & PbrFlags.BaseColorFactor) != 0) pm.BaseColorFactor = new UnityColor(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    if ((mflags & PbrFlags.MetallicFactor) != 0) pm.MetallicFactor = br.ReadSingle();
                    if ((mflags & PbrFlags.RoughnessFactor) != 0) pm.RoughnessFactor = br.ReadSingle();
                    if ((mflags & PbrFlags.EmissiveFactor) != 0) pm.EmissiveFactor = new UnityColor(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), 1f);
                    if ((mflags & PbrFlags.AlphaMode) != 0) { pm.AlphaMode = (AlphaMode)br.ReadByte(); if (pm.AlphaMode == AlphaMode.Mask) pm.AlphaCutoff = br.ReadSingle(); }
                    if ((mflags & PbrFlags.DoubleSided) != 0) pm.DoubleSided = br.ReadByte() != 0;

                    int texCount = br.ReadByte();
                    for (int t = 0; t < texCount; t++)
                    {
                        var tr = new TexRefVN();
                        tr.Slot = (TexSlot)br.ReadByte();
                        tr.UVSet = br.ReadByte();
                        tr.RefType = (UriKindVN)br.ReadByte();
                        byte to = br.ReadByte(); tr.HasOffset = (to & 1) != 0; tr.HasScale = (to & 2) != 0; tr.HasRotation = (to & 4) != 0;
                        if (tr.HasOffset) { tr.OffsetX = br.ReadSingle(); tr.OffsetY = br.ReadSingle(); }
                        if (tr.HasScale) { tr.ScaleX = br.ReadSingle(); tr.ScaleY = br.ReadSingle(); }
                        if (tr.HasRotation) { tr.Rotation = br.ReadSingle(); }

                        if ((mflags & PbrFlags.Sampler) != 0)
                        {
                            tr.Sampler = new SamplerVN { WrapU = br.ReadByte(), WrapV = br.ReadByte(), MinFilter = br.ReadByte(), MagFilter = br.ReadByte() };
                        }

                        if (tr.RefType == UriKindVN.External)
                        {
                            tr.ExternalUri = IO.ReadUtf8(br);
                            if (resolveExternalTexture != null)
                            {
                                var bytesTex = resolveExternalTexture(tr.ExternalUri);
                                if (bytesTex != null) { tr.RefType = UriKindVN.Embedded; tr.EmbeddedMime = MimeKind.PNG; tr.EmbeddedData = bytesTex; }
                            }
                        }
                        else
                        {
                            tr.EmbeddedMime = (MimeKind)br.ReadByte();
                            int len = (int)br.ReadUInt32();
                            tr.EmbeddedData = br.ReadBytes(len);
                        }

                        pm.Textures.Add(tr);
                    }

                    data.Materials.Add(pm);
                }

                var result = new VnImportResult { Data = data };
                result.Mesh = BuildUnityMesh(data, makeMeshReadable);
                result.Materials = BuildUnityMaterials(data.Materials);
                return result;
            }
        }

        // —— Legacy v1 fallback (best-effort)
        private static VnImportResult ImportV1(byte[] bytes, bool readable)
        {
            var data = new VnMeshData();
            using (var ms = new MemoryStream(bytes))
            using (var br = new BinaryReader(ms))
            {
                int nbSub = br.ReadInt32();
                var subColors = new float[nbSub * 4];
                for (int i = 0; i < nbSub * 4; i++) subColors[i] = br.ReadSingle();
                var nbVertArray = new int[nbSub]; for (int i = 0; i < nbSub; i++) nbVertArray[i] = br.ReadInt32();
                int vcount = 0; for (int i = 0; i < nbSub; i++) vcount += nbVertArray[i];
                var pos = new float[vcount * 3]; for (int i = 0; i < pos.Length; i++) pos[i] = br.ReadSingle();
                var nor = new float[vcount * 3]; for (int i = 0; i < nor.Length; i++) nor[i] = br.ReadSingle();
                int triTotal = 0; var nbTriArr = new int[nbSub]; for (int i = 0; i < nbSub; i++) { nbTriArr[i] = br.ReadInt32(); triTotal += nbTriArr[i]; }
                var idx = new uint[triTotal]; for (int i = 0; i < triTotal; i++) idx[i] = br.ReadUInt32();
                int nbUVTotal = 0; var nbUVArr = new int[nbSub]; for (int i = 0; i < nbSub; i++) { nbUVArr[i] = br.ReadInt32(); nbUVTotal += nbUVArr[i]; }
                var uvs = new float[nbUVTotal * 2]; for (int i = 0; i < uvs.Length; i++) uvs[i] = br.ReadSingle();

                data.Flags = GlobalFlags.HasPositions | GlobalFlags.HasNormals | GlobalFlags.HasUV0;
                data.Positions = pos; data.Normals = nor; data.UV0 = uvs; data.Indices = idx; data.VertexCount = (uint)vcount; data.IndexCount = (uint)triTotal;

                var colors = new float[vcount * 4];
                int cursor = 0; for (int s = 0; s < nbSub; s++)
                {
                    var cR = subColors[s * 4 + 0]; var cG = subColors[s * 4 + 1]; var cB = subColors[s * 4 + 2]; var cA = subColors[s * 4 + 3];
                    int n = nbVertArray[s];
                    for (int v = 0; v < n; v++) { colors[(cursor + v) * 4 + 0] = cR; colors[(cursor + v) * 4 + 1] = cG; colors[(cursor + v) * 4 + 2] = cB; colors[(cursor + v) * 4 + 3] = cA; }
                    cursor += n;
                }
                data.Colors = colors; data.Flags |= GlobalFlags.HasVertexColors;

                data.SubMeshes.Add(new SubMeshDescVN { Topology = Topology.Triangles, MaterialIndex = 0xFFFF, StartIndex = 0, IndexCount = (uint)triTotal, BaseVertex = 0, FirstVertex = 0, VertexCount = (uint)vcount });
            }

            return new VnImportResult { Data = data, Mesh = BuildUnityMesh(data, readable), Materials = new Material[0] };
        }

        private static Mesh BuildUnityMesh(VnMeshData d, bool readable)
        {
#if UNITY_2020_2_OR_NEWER
            // ---- New path: single-shot GPU upload via MeshData ----
            var mesh = new Mesh();
            mesh.name = d.Name ?? "VN_Mesh";

            if (d.VertexCount > 65535) mesh.indexFormat = IndexFormat.UInt32;

            // Describe vertex layout (interleaved stream)
            var layout = new List<VertexAttributeDescriptor>(6);
            layout.Add(new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0));
            int stream = 0;
            if ((d.Flags & GlobalFlags.HasNormals) != 0) layout.Add(new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream));
            if ((d.Flags & GlobalFlags.HasTangents) != 0) layout.Add(new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4, stream));
            if ((d.Flags & GlobalFlags.HasVertexColors) != 0) layout.Add(new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream));
            if ((d.Flags & GlobalFlags.HasUV0) != 0) layout.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream));
            if ((d.Flags & GlobalFlags.HasUV1) != 0) layout.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2, stream));

            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            var md = meshDataArray[0];

            // Compute vertex stride
            int stride = 3 * sizeof(float); // position
            if ((d.Flags & GlobalFlags.HasNormals) != 0) stride += 3 * sizeof(float);
            if ((d.Flags & GlobalFlags.HasTangents) != 0) stride += 4 * sizeof(float);
            if ((d.Flags & GlobalFlags.HasVertexColors) != 0) stride += 4 * sizeof(float);
            if ((d.Flags & GlobalFlags.HasUV0) != 0) stride += 2 * sizeof(float);
            if ((d.Flags & GlobalFlags.HasUV1) != 0) stride += 2 * sizeof(float);

            md.SetVertexBufferParams((int)d.VertexCount, layout.ToArray());
            md.SetIndexBufferParams((int)d.IndexCount, (d.Flags & GlobalFlags.IndicesU16) != 0 ? IndexFormat.UInt16 : IndexFormat.UInt32);

            // Fill vertex buffer (interleaved) in one pass
            var vb = md.GetVertexData<byte>(0);
            int offsetPos = 0;
            int offset = 0;
            int offNormal = offsetPos + 3 * sizeof(float);
            int offTangent = offNormal + (((d.Flags & GlobalFlags.HasNormals) != 0) ? 3 * sizeof(float) : 0);
            int offColor = offTangent + (((d.Flags & GlobalFlags.HasTangents) != 0) ? 4 * sizeof(float) : 0);
            int offUV0 = offColor + (((d.Flags & GlobalFlags.HasVertexColors) != 0) ? 4 * sizeof(float) : 0);
            int offUV1 = offUV0 + (((d.Flags & GlobalFlags.HasUV0) != 0) ? 2 * sizeof(float) : 0);

            // Local helpers to write floats without unsafe
            void WriteFloat(byte[] dst, int baseOffset, float v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, dst, baseOffset, 4); }

            // We’ll write into a temporary row buffer to avoid many BitConverter allocations per component
            var row = new byte[stride];
            for (int i = 0; i < d.VertexCount; i++)
            {
                int rowOff = 0;
                // pos
                WriteFloat(row, rowOff + 0, d.Positions[i * 3 + 0]);
                WriteFloat(row, rowOff + 4, d.Positions[i * 3 + 1]);
                WriteFloat(row, rowOff + 8, d.Positions[i * 3 + 2]);

                rowOff = offNormal;
                if ((d.Flags & GlobalFlags.HasNormals) != 0)
                {
                    WriteFloat(row, rowOff + 0, d.Normals[i * 3 + 0]);
                    WriteFloat(row, rowOff + 4, d.Normals[i * 3 + 1]);
                    WriteFloat(row, rowOff + 8, d.Normals[i * 3 + 2]);
                }

                rowOff = offTangent;
                if ((d.Flags & GlobalFlags.HasTangents) != 0)
                {
                    WriteFloat(row, rowOff + 0, d.Tangents[i * 4 + 0]);
                    WriteFloat(row, rowOff + 4, d.Tangents[i * 4 + 1]);
                    WriteFloat(row, rowOff + 8, d.Tangents[i * 4 + 2]);
                    WriteFloat(row, rowOff + 12, d.Tangents[i * 4 + 3]);
                }

                rowOff = offColor;
                if ((d.Flags & GlobalFlags.HasVertexColors) != 0)
                {
                    WriteFloat(row, rowOff + 0, d.Colors[i * 4 + 0]);
                    WriteFloat(row, rowOff + 4, d.Colors[i * 4 + 1]);
                    WriteFloat(row, rowOff + 8, d.Colors[i * 4 + 2]);
                    WriteFloat(row, rowOff + 12, d.Colors[i * 4 + 3]);
                }

                rowOff = offUV0;
                if ((d.Flags & GlobalFlags.HasUV0) != 0)
                {
                    WriteFloat(row, rowOff + 0, d.UV0[i * 2 + 0]);
                    WriteFloat(row, rowOff + 4, d.UV0[i * 2 + 1]);
                }

                rowOff = offUV1;
                if ((d.Flags & GlobalFlags.HasUV1) != 0)
                {
                    WriteFloat(row, rowOff + 0, d.UV1[i * 2 + 0]);
                    WriteFloat(row, rowOff + 4, d.UV1[i * 2 + 1]);
                }

                // copy row to vb
                vb.Slice(i * stride, stride).CopyFrom(row);
            }

            // Index buffer (single write)
            if ((d.Flags & GlobalFlags.IndicesU16) != 0)
            {
                var dst = md.GetIndexData<ushort>();
                var src = (ushort[])d.Indices;
                dst.CopyFrom(src);
            }
            else
            {
                var dst = md.GetIndexData<uint>();
                var src = (uint[])d.Indices;
                dst.CopyFrom(src);
            }

            // Submeshes in one call
            var subCount = d.SubMeshes.Count;
            var subDescs = new SubMeshDescriptor[subCount];
            for (int i = 0; i < subCount; i++)
            {
                var s = d.SubMeshes[i];
                subDescs[i] = new SubMeshDescriptor((int)s.StartIndex, (int)s.IndexCount)
                {
                    baseVertex = s.BaseVertex,
                    topology = s.Topology == Topology.Triangles ? MeshTopology.Triangles : MeshTopology.Lines,
                    firstVertex = (int)s.FirstVertex,
                    vertexCount = (int)s.VertexCount
                };
            }
            md.subMeshCount = subCount;
            for (int i = 0; i < subCount; i++) md.SetSubMesh(i, subDescs[i], MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);

            // Bounds
            Bounds? providedBounds = null;
            if ((d.Flags & GlobalFlags.HasBounds) != 0)
            {
                var bmin = new Vector3(d.BoundsMin[0], d.BoundsMin[1], d.BoundsMin[2]);
                var bmax = new Vector3(d.BoundsMax[0], d.BoundsMax[1], d.BoundsMax[2]);
                providedBounds = new Bounds((bmin + bmax) * 0.5f, bmax - bmin);
            }

            // Apply to Mesh and make it non-readable (frees system memory copy)
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);

            if (providedBounds.HasValue)
                mesh.bounds = providedBounds.Value;
            else
                mesh.RecalculateBounds();

            mesh.UploadMeshData(!readable); // non-readable: big memory + perf win

            return mesh;

#else
    // ---- Fallback path (older Unity): trim allocs & calls ----
    var mesh = new Mesh();
    mesh.name = d.Name ?? "VN_Mesh";
    if (d.VertexCount > 65535) mesh.indexFormat = IndexFormat.UInt32;

    // Use arrays, not List<>, to avoid alloc churn
    if ((d.Flags & GlobalFlags.HasPositions) != 0)
    {
        var vtx = new Vector3[d.VertexCount];
        for (int i = 0; i < vtx.Length; i++)
            vtx[i] = new Vector3(d.Positions[i*3+0], d.Positions[i*3+1], d.Positions[i*3+2]);
        mesh.SetVertices(vtx);
    }
    if ((d.Flags & GlobalFlags.HasNormals) != 0)
    {
        var n = new Vector3[d.VertexCount];
        for (int i = 0; i < n.Length; i++)
            n[i] = new Vector3(d.Normals[i*3+0], d.Normals[i*3+1], d.Normals[i*3+2]);
        mesh.SetNormals(n);
    }
    if ((d.Flags & GlobalFlags.HasTangents) != 0)
    {
        var t = new Vector4[d.VertexCount];
        for (int i = 0; i < t.Length; i++)
            t[i] = new Vector4(d.Tangents[i*4+0], d.Tangents[i*4+1], d.Tangents[i*4+2], d.Tangents[i*4+3]);
        mesh.SetTangents(t);
    }
    if ((d.Flags & GlobalFlags.HasVertexColors) != 0)
    {
        var c = new Color[d.VertexCount];
        for (int i = 0; i < c.Length; i++)
            c[i] = new Color(d.Colors[i*4+0], d.Colors[i*4+1], d.Colors[i*4+2], d.Colors[i*4+3]);
        mesh.SetColors(c);
    }
    if ((d.Flags & GlobalFlags.HasUV0) != 0)
    {
        var u0 = new Vector2[d.VertexCount];
        for (int i = 0; i < u0.Length; i++)
            u0[i] = new Vector2(d.UV0[i*2+0], d.UV0[i*2+1]);
        mesh.SetUVs(0, u0);
    }
    if ((d.Flags & GlobalFlags.HasUV1) != 0)
    {
        var u1 = new Vector2[d.VertexCount];
        for (int i = 0; i < u1.Length; i++)
            u1[i] = new Vector2(d.UV1[i*2+0], d.UV1[i*2+1]);
        mesh.SetUVs(1, u1);
    }

    // Indices
    if ((d.Flags & GlobalFlags.IndicesU16) != 0)
        mesh.SetIndices(Array.ConvertAll((ushort[])d.Indices, x => (int)x), MeshTopology.Triangles, 0, false);
    else
        mesh.SetIndices(Array.ConvertAll((uint[])d.Indices,   x => (int)x), MeshTopology.Triangles, 0, false);

    // Submeshes in one pass
    mesh.subMeshCount = d.SubMeshes.Count;
    var updates = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
    for (int i = 0; i < d.SubMeshes.Count; i++)
    {
        var s = d.SubMeshes[i];
        var desc = new SubMeshDescriptor((int)s.StartIndex, (int)s.IndexCount)
        {
            baseVertex  = s.BaseVertex,
            topology    = s.Topology == Topology.Triangles ? MeshTopology.Triangles : MeshTopology.Lines,
            firstVertex = (int)s.FirstVertex,
            vertexCount = (int)s.VertexCount
        };
        mesh.SetSubMesh(i, desc, updates);
    }

    if ((d.Flags & GlobalFlags.HasBounds) != 0)
    {
        var bmin = new Vector3(d.BoundsMin[0], d.BoundsMin[1], d.BoundsMin[2]);
        var bmax = new Vector3(d.BoundsMax[0], d.BoundsMax[1], d.BoundsMax[2]);
        mesh.bounds = new Bounds((bmin + bmax) * 0.5f, bmax - bmin);
    }
    else mesh.RecalculateBounds();

    mesh.UploadMeshData(true); // free CPU copy
    return mesh;
#endif
        }

        private static Material[] BuildUnityMaterials(List<PbrMaterialVN> list)
        {
            if (list == null || list.Count == 0) return new Material[0];
            var mats = new Material[list.Count];
            for (int i = 0; i < list.Count; i++) mats[i] = CreateMaterial(list[i]);
            return mats;
        }

        private static Material CreateMaterial(PbrMaterialVN m)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = string.IsNullOrEmpty(m.Name) ? "VN_Mat" : m.Name };

            if ((m.Flags & PbrFlags.BaseColorFactor) != 0) mat.SetColor("_BaseColor", new Color(m.BaseColorFactor.r, m.BaseColorFactor.g, m.BaseColorFactor.b, m.BaseColorFactor.a));
            if ((m.Flags & PbrFlags.MetallicFactor) != 0) mat.SetFloat("_Metallic", m.MetallicFactor);
            if ((m.Flags & PbrFlags.RoughnessFactor) != 0) mat.SetFloat("_Smoothness", 1f - Mathf.Clamp01(m.RoughnessFactor));
            if ((m.Flags & PbrFlags.EmissiveFactor) != 0) { mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", new Color(m.EmissiveFactor.r, m.EmissiveFactor.g, m.EmissiveFactor.b, 1)); }

            if ((m.Flags & PbrFlags.AlphaMode) != 0)
            {
                SetupAlphaMode(mat, m.AlphaMode, m.AlphaCutoff);
            }

            if ((m.Flags & PbrFlags.DoubleSided) != 0)
            {
                mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            }

            foreach (var t in m.Textures)
            {
                if (t.RefType != UriKindVN.Embedded || t.EmbeddedData == null || t.EmbeddedData.Length == 0) continue;

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                tex.name = m.Name + "_" + t.Slot;
                tex.LoadImage(t.EmbeddedData, true);
                tex.wrapModeU = t.Sampler?.WrapU == 1 ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                tex.wrapModeV = t.Sampler?.WrapV == 1 ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;

                string prop = TexturePropertyFromSlot(t.Slot);
                if (!string.IsNullOrEmpty(prop))
                {
                    mat.SetTexture(prop, tex);
                    if (t.HasScale) mat.SetTextureScale(prop, new Vector2(t.ScaleX, t.ScaleY));
                    if (t.HasOffset) mat.SetTextureOffset(prop, new Vector2(t.OffsetX, t.OffsetY));
                }
            }

            return mat;
        }

        private static string TexturePropertyFromSlot(TexSlot slot)
        {
            switch (slot)
            {
                case TexSlot.BaseColor: return "_BaseMap"; // URP Lit; Standard fallback handled by Unity property remap
                case TexSlot.MetalRough: return "_MetallicGlossMap";
                case TexSlot.Normal: return "_BumpMap";
                case TexSlot.Occlusion: return "_OcclusionMap";
                case TexSlot.Emissive: return "_EmissionMap";
                default: return null;
            }
        }

        private static void SetupAlphaMode(Material mat, AlphaMode mode, float cutoff)
        {
            switch (mode)
            {
                case AlphaMode.Opaque:
                    mat.SetFloat("_Surface", 0);
                    mat.SetInt("_SrcBlend", (int)BlendMode.One);
                    mat.SetInt("_DstBlend", (int)BlendMode.Zero);
                    mat.SetInt("_ZWrite", 1);
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.DisableKeyword("_ALPHABLEND_ON");
                    mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.renderQueue = -1;
                    break;
                case AlphaMode.Mask:
                    mat.SetFloat("_Surface", 0);
                    mat.SetFloat("_Cutoff", cutoff);
                    mat.EnableKeyword("_ALPHATEST_ON");
                    mat.SetInt("_ZWrite", 1);
                    mat.renderQueue = (int)RenderQueue.AlphaTest;
                    break;
                case AlphaMode.Blend:
                    mat.SetFloat("_Surface", 1);
                    mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                    mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    mat.SetInt("_ZWrite", 0);
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.EnableKeyword("_ALPHABLEND_ON");
                    mat.renderQueue = (int)RenderQueue.Transparent;
                    break;
            }
        }
    }
    #endregion

    #region Unity helpers: Build VnMeshData from a Unity Mesh (for export)
    public static class VnUnityBridge
    {
        public static VnMeshData FromUnityMesh(Mesh mesh, List<SubMeshDescVN> subDescs, bool withNormals = true, bool withTangents = false, bool withColors = false, bool withUV1 = false, bool indicesU16 = false)
        {
            if (mesh == null) throw new ArgumentNullException("mesh");
#if UNITY_2020_2_OR_NEWER
            var data = new VnMeshData();
            data.Name = mesh.name;
            data.Flags = GlobalFlags.HasPositions;

            using (var mda = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                var md = mda[0];
                int v = md.vertexCount;
                data.VertexCount = (uint)v;

                // ---------- Positions (required) ----------
                var pos = new NativeArray<Vector3>(v, Allocator.Temp);
                md.GetVertices(pos);
                data.Positions = new float[v * 3];
                for (int i = 0; i < v; i++)
                {
                    var p = pos[i];
                    int o = i * 3;
                    data.Positions[o + 0] = p.x;
                    data.Positions[o + 1] = p.y;
                    data.Positions[o + 2] = p.z;
                }
                pos.Dispose();

                // ---------- Normals ----------
                if (withNormals && md.HasVertexAttribute(VertexAttribute.Normal))
                {
                    var nor = new NativeArray<Vector3>(v, Allocator.Temp);
                    md.GetNormals(nor);
                    data.Normals = new float[v * 3];
                    for (int i = 0; i < v; i++)
                    {
                        var n = nor[i];
                        int o = i * 3;
                        data.Normals[o + 0] = n.x;
                        data.Normals[o + 1] = n.y;
                        data.Normals[o + 2] = n.z;
                    }
                    nor.Dispose();
                    data.Flags |= GlobalFlags.HasNormals;
                }

                // ---------- Tangents ----------
                if (withTangents && md.HasVertexAttribute(VertexAttribute.Tangent))
                {
                    var tan = new NativeArray<Vector4>(v, Allocator.Temp);
                    md.GetTangents(tan);
                    data.Tangents = new float[v * 4];
                    for (int i = 0; i < v; i++)
                    {
                        var t = tan[i];
                        int o = i * 4;
                        data.Tangents[o + 0] = t.x;
                        data.Tangents[o + 1] = t.y;
                        data.Tangents[o + 2] = t.z;
                        data.Tangents[o + 3] = t.w;
                    }
                    tan.Dispose();
                    data.Flags |= GlobalFlags.HasTangents;
                }

                // ---------- Vertex Colors ----------
                if (withColors && md.HasVertexAttribute(VertexAttribute.Color))
                {
                    var col = new NativeArray<Color>(v, Allocator.Temp);
                    md.GetColors(col);
                    data.Colors = new float[v * 4];
                    for (int i = 0; i < v; i++)
                    {
                        var c = col[i];
                        int o = i * 4;
                        data.Colors[o + 0] = c.r;
                        data.Colors[o + 1] = c.g;
                        data.Colors[o + 2] = c.b;
                        data.Colors[o + 3] = c.a;
                    }
                    col.Dispose();
                    data.Flags |= GlobalFlags.HasVertexColors;
                }

                // ---------- UV0 ----------
                if (md.HasVertexAttribute(VertexAttribute.TexCoord0))
                {
                    var uv0 = new NativeArray<Vector2>(v, Allocator.Temp);
                    md.GetUVs(0, uv0);
                    if (uv0.Length == v) // safety
                    {
                        data.UV0 = new float[v * 2];
                        for (int i = 0; i < v; i++)
                        {
                            var u = uv0[i];
                            int o = i * 2;
                            data.UV0[o + 0] = u.x;
                            data.UV0[o + 1] = u.y;
                        }
                        data.Flags |= GlobalFlags.HasUV0;
                    }
                    uv0.Dispose();
                }

                // ---------- UV1 ----------
                if (withUV1 && md.HasVertexAttribute(VertexAttribute.TexCoord1))
                {
                    var uv1 = new NativeArray<Vector2>(v, Allocator.Temp);
                    md.GetUVs(1, uv1);
                    if (uv1.Length == v)
                    {
                        data.UV1 = new float[v * 2];
                        for (int i = 0; i < v; i++)
                        {
                            var u = uv1[i];
                            int o = i * 2;
                            data.UV1[o + 0] = u.x;
                            data.UV1[o + 1] = u.y;
                        }
                        data.Flags |= GlobalFlags.HasUV1;
                    }
                    uv1.Dispose();
                }

                // ---------- Submeshes + Indices (aggregate once) ----------
                int subCount = md.subMeshCount;
                subDescs = subDescs ?? new List<SubMeshDescVN>(subCount);
                subDescs.Clear();

                // First pass: compute total index count + decide 16/32-bit
                long totalIdx = 0;
                uint maxIndex = 0;

                for (int si = 0; si < subCount; si++)
                {
                    var smd = md.GetSubMesh(si);
                    totalIdx += smd.indexCount;

                    // Quick max scan to decide U16 vs U32
                    // Use a small temp buffer to avoid allocating huge arrays at once
                    var tmp = new NativeArray<int>((int)smd.indexCount, Allocator.Temp);
                    md.GetIndices(tmp, si);
                    for (int k = 0; k < tmp.Length; k++)
                    {
                        uint idx = (uint)tmp[k];
                        if (idx > maxIndex) maxIndex = idx;
                    }
                    tmp.Dispose();
                }

                bool canU16 = (data.VertexCount <= 65535u) && (maxIndex <= ushort.MaxValue);
                bool useU16 = indicesU16 ? canU16 : canU16; // force U16 when possible

                // Allocate final index buffer
                data.IndexCount = (uint)totalIdx;
                if (useU16)
                {
                    var all = new ushort[totalIdx];
                    int cursor = 0;

                    for (int si = 0; si < subCount; si++)
                    {
                        var smd = md.GetSubMesh(si);
                        var tmp = new NativeArray<int>((int)smd.indexCount, Allocator.Temp);
                        md.GetIndices(tmp, si);

                        // Record submesh desc
                        var sd = new SubMeshDescVN
                        {
                            Topology = smd.topology == MeshTopology.Triangles ? Topology.Triangles : Topology.Lines,
                            MaterialIndex = 0xFFFF,
                            StartIndex = (uint)cursor,
                            IndexCount = (uint)tmp.Length,
                            BaseVertex = smd.baseVertex,
                            FirstVertex = (uint)smd.firstVertex,
                            VertexCount = (uint)smd.vertexCount
                        };
                        subDescs.Add(sd);

                        // Copy indices
                        for (int k = 0; k < tmp.Length; k++) all[cursor++] = (ushort)tmp[k];

                        tmp.Dispose();
                    }

                    data.Indices = all;
                    data.Flags |= GlobalFlags.IndicesU16;
                }
                else
                {
                    var all = new uint[totalIdx];
                    int cursor = 0;

                    for (int si = 0; si < subCount; si++)
                    {
                        var smd = md.GetSubMesh(si);
                        var tmp = new NativeArray<int>((int)smd.indexCount, Allocator.Temp);
                        md.GetIndices(tmp, si);

                        var sd = new SubMeshDescVN
                        {
                            Topology = smd.topology == MeshTopology.Triangles ? Topology.Triangles : Topology.Lines,
                            MaterialIndex = 0xFFFF,
                            StartIndex = (uint)cursor,
                            IndexCount = (uint)tmp.Length,
                            BaseVertex = smd.baseVertex,
                            FirstVertex = (uint)smd.firstVertex,
                            VertexCount = (uint)smd.vertexCount
                        };
                        subDescs.Add(sd);

                        for (int k = 0; k < tmp.Length; k++) all[cursor++] = (uint)tmp[k];

                        tmp.Dispose();
                    }

                    data.Indices = all;
                    // no IndicesU16 flag
                }

                data.SubMeshes = subDescs;
            }

            return data;
#else
            var data = new VnMeshData();
            data.Flags = GlobalFlags.HasPositions;
            if (withNormals && mesh.normals != null && mesh.normals.Length == mesh.vertexCount) data.Flags |= GlobalFlags.HasNormals;
            if (withTangents && mesh.tangents != null && mesh.tangents.Length == mesh.vertexCount) data.Flags |= GlobalFlags.HasTangents;
            if (withColors && mesh.colors != null && mesh.colors.Length == mesh.vertexCount) data.Flags |= GlobalFlags.HasVertexColors;
            if (indicesU16) data.Flags |= GlobalFlags.IndicesU16;

            int v = mesh.vertexCount;
            var verts = mesh.vertices; data.Positions = new float[v * 3]; for (int i = 0; i < v; i++) { var p = verts[i]; data.Positions[i * 3 + 0] = p.x; data.Positions[i * 3 + 1] = p.y; data.Positions[i * 3 + 2] = p.z; }

            if ((data.Flags & GlobalFlags.HasNormals) != 0) { var n = mesh.normals; data.Normals = new float[v * 3]; for (int i = 0; i < v; i++) { var p = n[i]; data.Normals[i * 3 + 0] = p.x; data.Normals[i * 3 + 1] = p.y; data.Normals[i * 3 + 2] = p.z; } }
            if ((data.Flags & GlobalFlags.HasTangents) != 0) { var t = mesh.tangents; data.Tangents = new float[v * 4]; for (int i = 0; i < v; i++) { var p = t[i]; data.Tangents[i * 4 + 0] = p.x; data.Tangents[i * 4 + 1] = p.y; data.Tangents[i * 4 + 2] = p.z; data.Tangents[i * 4 + 3] = p.w; } }
            if ((data.Flags & GlobalFlags.HasVertexColors) != 0) { var c = mesh.colors; data.Colors = new float[v * 4]; for (int i = 0; i < v; i++) { var p = c[i]; data.Colors[i * 4 + 0] = p.r; data.Colors[i * 4 + 1] = p.g; data.Colors[i * 4 + 2] = p.b; data.Colors[i * 4 + 3] = p.a; } }

            var uv0Arr = mesh.uv;
            if (uv0Arr != null && uv0Arr.Length == v)
            {
                data.UV0 = new float[v * 2];
                for (int i = 0; i < v; i++) { var p = uv0Arr[i]; data.UV0[i * 2 + 0] = p.x; data.UV0[i * 2 + 1] = p.y; }
                data.Flags |= GlobalFlags.HasUV0;
            }

            if (withUV1)
            {
                var uv1Arr = mesh.uv2;
                if (uv1Arr != null && uv1Arr.Length == v)
                {
                    data.UV1 = new float[v * 2];
                    for (int i = 0; i < v; i++) { var p = uv1Arr[i]; data.UV1[i * 2 + 0] = p.x; data.UV1[i * 2 + 1] = p.y; }
                    data.Flags |= GlobalFlags.HasUV1;
                }
            }

            var all = new List<int>();
            subDescs = subDescs ?? new List<SubMeshDescVN>();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var smi = mesh.GetSubMesh(i);
                var idx = mesh.GetIndices(i);
                var sd = new SubMeshDescVN
                {
                    Topology = smi.topology == MeshTopology.Triangles ? Topology.Triangles : Topology.Lines,
                    MaterialIndex = 0xFFFF,
                    StartIndex = (uint)all.Count,
                    IndexCount = (uint)idx.Length,
                    BaseVertex = smi.baseVertex,
                    FirstVertex = (uint)smi.firstVertex,
                    VertexCount = (uint)smi.vertexCount
                };
                all.AddRange(idx);
                subDescs.Add(sd);
            }

            data.SubMeshes = subDescs;
            if ((data.Flags & GlobalFlags.IndicesU16) != 0)
            {
                var u16 = new ushort[all.Count]; for (int i = 0; i < all.Count; i++) u16[i] = (ushort)all[i]; data.Indices = u16; data.IndexCount = (uint)u16.Length;
            }
            else
            {
                var u32 = new uint[all.Count]; for (int i = 0; i < all.Count; i++) u32[i] = (uint)all[i]; data.Indices = u32; data.IndexCount = (uint)u32.Length;
            }

            data.VertexCount = (uint)v;
            return data;
        }
#endif
        }
    }
    #endregion

    #region Simple Facade API (Unity-only)
    public static class VNB
    {
        /// <summary>
        /// Import VN v2 bytes and return a ready-to-use GameObject with MeshFilter + MeshRenderer.
        /// </summary>
        public static GameObject Import(byte[] bytes, bool addColliders = false, bool makeMeshReadable = true)
        {
            var result = VertexNeutralBinary.VnImporter.Import(bytes, makeMeshReadable, null);

            var name = result.Data != null
                        && result.Data.Name != null ?
                           result.Data.Name
                : "VN_Imported";

            var go = new GameObject(name);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();

            mf.sharedMesh = result.Mesh;
            mr.sharedMaterials = result.Materials ?? new Material[0];

            if (addColliders)
            {
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false; // Set to true if you need a convex collider
            }

            return go;
        }

        /// <summary>
        /// Export a GameObject (MeshFilter+MeshRenderer or SkinnedMeshRenderer) to VN v2 bytes.
        /// </summary>
        public static byte[] Export(GameObject go)
        {
            if (go == null) throw new ArgumentNullException("go");

            Mesh mesh = null;
            Material[] mats = null;

            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            if (mf != null && mr != null)
            {
                mesh = mf.sharedMesh;
                mats = mr.sharedMaterials;
            }
            else
            {
                var sk = go.GetComponent<SkinnedMeshRenderer>();
                if (sk == null) throw new InvalidOperationException("GameObject must have MeshFilter+MeshRenderer or SkinnedMeshRenderer");
                mesh = sk.sharedMesh;
                mats = sk.sharedMaterials;
            }
            if (mesh == null) throw new InvalidOperationException("No mesh found to export.");

            var data = VertexNeutralBinary.VnUnityBridge.FromUnityMesh(
                mesh,
                subDescs: null,
                withNormals: mesh.normals != null && mesh.normals.Length == mesh.vertexCount,
                withTangents: mesh.tangents != null && mesh.tangents.Length == mesh.vertexCount,
                withColors: mesh.colors != null && mesh.colors.Length == mesh.vertexCount,
                withUV1: true,
                indicesU16: mesh.indexFormat == IndexFormat.UInt16
            );
            data.Name = go.name;

            // Assign material indices (round robin if submesh count > material count)
            for (int i = 0; i < data.SubMeshes.Count; i++)
            {
                var sm = data.SubMeshes[i];
                sm.MaterialIndex = (ushort)((mats != null && mats.Length > 0) ? (i % mats.Length) : 0xFFFF);
                data.SubMeshes[i] = sm;
            }

            // Build VN materials from Unity materials
            data.Materials = new List<VertexNeutralBinary.PbrMaterialVN>();
            if (mats != null)
            {
                for (int i = 0; i < mats.Length; i++)
                {
                    var pm = BuildPbrFromUnityMaterial(mats[i]);
                    data.Materials.Add(pm);
                }
            }

            // Mark embed textures if any material has at least one embedded payload
            foreach (var m in data.Materials)
            {
                foreach (var t in m.Textures)
                {
                    if (t.RefType == VertexNeutralBinary.UriKindVN.Embedded)
                    {
                        data.Flags |= VertexNeutralBinary.GlobalFlags.EmbedTextures;
                        goto doneEmbed;
                    }
                }
            }
            doneEmbed:;

            return VertexNeutralBinary.VnExporter.Export(data);
        }

        // -------------------- Helpers --------------------

        private static VertexNeutralBinary.PbrMaterialVN BuildPbrFromUnityMaterial(Material src)
        {
            var pm = new VertexNeutralBinary.PbrMaterialVN { Name = src != null ? src.name : "Mat", Flags = 0 };
            if (src == null) return pm;

            // Base color factor
            if (src.HasProperty("_BaseColor"))
            {
                var c = src.GetColor("_BaseColor");
                pm.BaseColorFactor = new VertexNeutralBinary.UnityColor(c.r, c.g, c.b, c.a);
                pm.Flags |= VertexNeutralBinary.PbrFlags.BaseColorFactor;
            }
            else if (src.HasProperty("_Color"))
            {
                var c = src.GetColor("_Color");
                pm.BaseColorFactor = new VertexNeutralBinary.UnityColor(c.r, c.g, c.b, c.a);
                pm.Flags |= VertexNeutralBinary.PbrFlags.BaseColorFactor;
            }

            // Metallic / Smoothness
            if (src.HasProperty("_Metallic"))
            {
                pm.MetallicFactor = src.GetFloat("_Metallic");
                pm.Flags |= VertexNeutralBinary.PbrFlags.MetallicFactor;
            }
            if (src.HasProperty("_Smoothness"))
            {
                var s = src.GetFloat("_Smoothness");
                pm.RoughnessFactor = 1f - s;
                pm.Flags |= VertexNeutralBinary.PbrFlags.RoughnessFactor;
            }

            // Emission
            if (src.IsKeywordEnabled("_EMISSION") || src.HasProperty("_EmissionColor"))
            {
                var e = src.HasProperty("_EmissionColor") ? src.GetColor("_EmissionColor") : Color.black;
                if (e.maxColorComponent > 0f)
                {
                    pm.EmissiveFactor = new VertexNeutralBinary.UnityColor(e.r, e.g, e.b, 1);
                    pm.Flags |= VertexNeutralBinary.PbrFlags.EmissiveFactor;
                }
            }

            // Alpha
            if (src.HasProperty("_Surface"))
            {
                var surf = (int)src.GetFloat("_Surface"); // 0 Opaque, 1 Transparent
                if (surf == 1) { pm.Flags |= VertexNeutralBinary.PbrFlags.AlphaMode; pm.AlphaMode = VertexNeutralBinary.AlphaMode.Blend; }
                if (src.IsKeywordEnabled("_ALPHATEST_ON"))
                {
                    pm.Flags |= VertexNeutralBinary.PbrFlags.AlphaMode;
                    pm.AlphaMode = VertexNeutralBinary.AlphaMode.Mask;
                    pm.AlphaCutoff = src.HasProperty("_Cutoff") ? src.GetFloat("_Cutoff") : 0.5f;
                }
            }

            // Double-sided
            if (src.HasProperty("_Cull"))
            {
                var cull = (int)src.GetFloat("_Cull");
                if (cull == (int)CullMode.Off) { pm.DoubleSided = true; pm.Flags |= VertexNeutralBinary.PbrFlags.DoubleSided; }
            }

            // Textures — embed readable textures or fallback to external (name)
            TryAddTexture(src, pm, VertexNeutralBinary.TexSlot.BaseColor, new[] { "_BaseMap", "_MainTex" });
            TryAddTexture(src, pm, VertexNeutralBinary.TexSlot.MetalRough, new[] { "_MetallicGlossMap" });
            TryAddTexture(src, pm, VertexNeutralBinary.TexSlot.Normal, new[] { "_BumpMap" });
            TryAddTexture(src, pm, VertexNeutralBinary.TexSlot.Occlusion, new[] { "_OcclusionMap" });
            TryAddTexture(src, pm, VertexNeutralBinary.TexSlot.Emissive, new[] { "_EmissionMap" });

            return pm;
        }

        private static void TryAddTexture(Material src, VertexNeutralBinary.PbrMaterialVN pm, VertexNeutralBinary.TexSlot slot, string[] propNames)
        {
            Texture tex = null;
            string prop = null;

            foreach (var p in propNames)
            {
                if (src.HasProperty(p))
                {
                    tex = src.GetTexture(p);
                    if (tex != null) { prop = p; break; }
                }
            }
            if (tex == null) return;

            var tr = new VertexNeutralBinary.TexRefVN { Slot = slot, UVSet = 0, RefType = VertexNeutralBinary.UriKindVN.External };

            // tiling/offset
            var scale = src.GetTextureScale(prop);
            var off = src.GetTextureOffset(prop);
            tr.HasScale = !(Mathf.Approximately(scale.x, 1f) && Mathf.Approximately(scale.y, 1f));
            tr.HasOffset = !(Mathf.Approximately(off.x, 0f) && Mathf.Approximately(off.y, 0f));
            if (tr.HasScale) { tr.ScaleX = scale.x; tr.ScaleY = scale.y; pm.Flags |= VertexNeutralBinary.PbrFlags.TilingOffset; }
            if (tr.HasOffset) { tr.OffsetX = off.x; tr.OffsetY = off.y; pm.Flags |= VertexNeutralBinary.PbrFlags.TilingOffset; }

            // Try to embed if Texture2D & readable
            var t2d = tex as Texture2D;
            if (t2d != null)
            {
                try
                {
                    var png = t2d.EncodeToPNG();
                    if (png != null && png.Length > 0)
                    {
                        tr.RefType = VertexNeutralBinary.UriKindVN.Embedded;
                        tr.EmbeddedMime = VertexNeutralBinary.MimeKind.PNG;
                        tr.EmbeddedData = png;

                        switch (slot)
                        {
                            case VertexNeutralBinary.TexSlot.BaseColor: pm.Flags |= VertexNeutralBinary.PbrFlags.BaseColorTex; break;
                            case VertexNeutralBinary.TexSlot.MetalRough: pm.Flags |= VertexNeutralBinary.PbrFlags.MetalRoughTex; break;
                            case VertexNeutralBinary.TexSlot.Normal: pm.Flags |= VertexNeutralBinary.PbrFlags.NormalTex; break;
                            case VertexNeutralBinary.TexSlot.Occlusion: pm.Flags |= VertexNeutralBinary.PbrFlags.OcclusionTex; break;
                            case VertexNeutralBinary.TexSlot.Emissive: pm.Flags |= VertexNeutralBinary.PbrFlags.EmissiveTex; break;
                        }
                        pm.Textures.Add(tr);
                        return;
                    }
                }
                catch
                {
                    // texture not readable or encode failed; fall back to external name
                }
            }

            // Fallback: store external reference using texture name (consumer may resolve it)
            tr.RefType = VertexNeutralBinary.UriKindVN.External;
            tr.ExternalUri = tex != null ? tex.name : string.Empty;

            switch (slot)
            {
                case VertexNeutralBinary.TexSlot.BaseColor: pm.Flags |= VertexNeutralBinary.PbrFlags.BaseColorTex; break;
                case VertexNeutralBinary.TexSlot.MetalRough: pm.Flags |= VertexNeutralBinary.PbrFlags.MetalRoughTex; break;
                case VertexNeutralBinary.TexSlot.Normal: pm.Flags |= VertexNeutralBinary.PbrFlags.NormalTex; break;
                case VertexNeutralBinary.TexSlot.Occlusion: pm.Flags |= VertexNeutralBinary.PbrFlags.OcclusionTex; break;
                case VertexNeutralBinary.TexSlot.Emissive: pm.Flags |= VertexNeutralBinary.PbrFlags.EmissiveTex; break;
            }
            pm.Textures.Add(tr);
        }
    }
    #endregion
}