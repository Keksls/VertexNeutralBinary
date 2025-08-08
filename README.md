# VertexNeutral v2 — Unity Mesh Exporter/Importer

A tiny, dependency-free binary format and Unity-only toolkit for moving meshes (plus optional PBR material data) in and out of Unity.

- **Target:** .NET Framework **4.7**, **Unity 2020+** (newer works too)  
- **Endianness:** Little-endian  
- **Safety:** Pure managed code, **no `unsafe`**  
- **Format:** Single custom binary (`.vnb` recommended)  
- **API:** `VNB.Import(byte[]) -> GameObject`, `VNB.Export(GameObject) -> byte[]`  
- **Textures & PBR:** Fully optional, via flags; can be **embedded** or **external**

---

## Why VertexNeutral?

- **Dead-simple**: One file, one mesh container, optional materials.
- **Deterministic & fast**: Straightforward binary layout; no reflection/JSON.
- **Unity-friendly**: Builds `Mesh`, `Material[]`, and a ready-to-use `GameObject`.
- **No dependencies**: Works in URP or Standard; no extra packages required.

---

## Quick Start

### Install (drop-in)

1. Copy the `VertexNeutralV2` namespace file(s) into your Unity project (Editor or runtime assembly).
2. Use the **Simple Facade API**:

```csharp
// Import .vnb bytes to a ready GameObject (MeshFilter + MeshRenderer)
var go = VNB.Import(File.ReadAllBytes(path), addColliders: false);

// Export a GameObject (MeshFilter+MeshRenderer or SkinnedMeshRenderer) to bytes
var bytes = VNB.Export(go);
File.WriteAllBytes(path, bytes);
```

> Tip: `addColliders: true` adds a non-convex `MeshCollider`.

---

## What gets stored?

Controlled by flags:

- **Vertex streams**: positions (required), normals, tangents, colors (RGBA), UV0, UV1  
- **Indices**: `uint` (default) or `ushort` (`IndicesU16`)  
- **Submeshes**: topology (triangles/lines), index/vertex windows, material index  
- **Bounds**: optional AABB  
- **Materials (PBR-ish)**: baseColor/metallic/roughness/emissive/alpha/double-sided + up to 5 texture slots  
- **Textures**: embedded (PNG/JPG/KTX2 payload) or external by name/URI

---

## Simple Facade API

### `GameObject VNB.Import(byte[] bytes, bool addColliders = false)`
- Parses VN v2. If magic/version mismatch, tries a **legacy v1** best-effort reader.
- Builds a `GameObject` with `MeshFilter` + `MeshRenderer` and assigns materials.
- Optionally adds a `MeshCollider`.

### `byte[] VNB.Export(GameObject go)`
- Accepts:
  - `MeshFilter` + `MeshRenderer`, or
  - `SkinnedMeshRenderer`
- Auto-collects vertex streams (normals/tangents/colors if present), **UV0 & UV1**, submeshes, and material data.
- Textures are **embedded** when readable (`Texture2D.EncodeToPNG()`); else they fall back to external references (texture name).
- Submeshes are assigned **round-robin** material indices if there are more submeshes than materials.

---

## Unity Integration Details

### Importer mapping
- **Mesh**
  - Sets `indexFormat` to `UInt32` if `VertexCount > 65535`.
  - Populates streams based on flags.  
  - Uses submesh descriptors (`SubMeshDescriptor`) and respects original topology.
  - Uses provided `Bounds` when present; else calls `RecalculateBounds()`.
- **Materials**
  - Prefers **URP/Lit**; falls back to **Standard**.
  - Properties:
    - `_BaseColor` → base color factor  
    - `_Metallic`, `_Smoothness` (Unity smoothness = `1 - roughness`)  
    - `_EmissionColor` (enables `_EMISSION` if non-black)  
  - Alpha modes:
    - **Opaque** (`AlphaMode.Opaque`)
    - **Cutout** (`AlphaMode.Mask`, uses `_Cutoff`)
    - **Transparent** (`AlphaMode.Blend`)
  - Double-sided: sets `_Cull = Off`.
  - Texture slots → shader properties:
    - `BaseColor` → `_BaseMap` (URP; Standard remap works)
    - `MetalRough` → `_MetallicGlossMap`
    - `Normal` → `_BumpMap`
    - `Occlusion` → `_OcclusionMap`
    - `Emissive` → `_EmissionMap`
  - Applies per-texture tiling/offset if present.

### Exporter mapping
- Extracts streams with `VnUnityBridge.FromUnityMesh(...)`.
- Gathers submeshes from `mesh.GetSubMesh(i)` and `mesh.GetIndices(i)`.
- Converts texture tiling/offset into flags; embeds PNGs when possible.

---

## The Data Model (authoring API)

### `VnMeshData`
Container object used by the exporter/importer and suitable for custom pipelines:

- `Name`
- `Flags : GlobalFlags`
- Streams (optional by flag):
  - `Positions` (required): `float[v * 3]`
  - `Normals`: `float[v * 3]`
  - `Tangents`: `float[v * 4]`
  - `Colors`: `float[v * 4]` (RGBA)
  - `UV0`, `UV1`: `float[v * 2]`
- `BoundsMin`, `BoundsMax`: `float[3]` each
- `Indices`: `uint[]` or `ushort[]` depending on `IndicesU16`
- `SubMeshes: List<SubMeshDescVN>`
- `Materials: List<PbrMaterialVN>`
- `VertexCount`, `IndexCount` (filled by exporter/reader)

### `SubMeshDescVN`
- `Topology`: `Triangles` | `Lines`
- `MaterialIndex`: `ushort` (`0xFFFF` = none)
- `StartIndex`, `IndexCount` (index window)
- `BaseVertex`, `FirstVertex`, `VertexCount` (vertex window)

### `PbrMaterialVN`
- `Name`
- `Flags : PbrFlags` (what is present)
- Factors:
  - `BaseColorFactor : UnityColor (r,g,b,a)`
  - `MetallicFactor : float`
  - `RoughnessFactor : float` (Unity smoothness = `1 - roughness`)
  - `EmissiveFactor : UnityColor` (rgb only used)
  - `AlphaMode : Opaque | Mask | Blend`, `AlphaCutoff`
  - `DoubleSided : bool`
- `Textures : List<TexRefVN>`

### `TexRefVN`
- `Slot`: `BaseColor | MetalRough | Normal | Occlusion | Emissive`
- `UVSet`: 0 or 1
- `RefType`: `External | Embedded`
- `Sampler`: optional (`WrapU/WrapV/MinFilter/MagFilter`) – emitted if `PbrFlags.Sampler` is set
- **Embedded**: `EmbeddedMime (PNG/JPG/KTX2)`, `EmbeddedData`
- **External**: `ExternalUri` (string)
- Optional transform: `HasOffset/Scale/Rotation` + values

---

## Binary File Format (VN v2)

All values **little-endian**.

### Header (fixed)
```
uint32 Magic         // 'VNB2' = 0x564E4232
uint16 Version       // 2
uint8  Endianness    // 0 = little
uint8  CoordSys      // 0 = Unity Y-up
float  UnitScale     // 1.0
uint32 GlobalFlags   // bitfield, see below
uint32 VertexCount
uint32 IndexCount
uint32 SubMeshCount
uint32 MaterialCount
byte[16] Reserved
```

### Name
```
uint16 nameLen
byte[nameLen] UTF-8
```

### Vertex streams (in this order, present if flag is set)
```
Positions      : float[VertexCount * 3]
Normals        : float[VertexCount * 3]
Tangents       : float[VertexCount * 4]
Colors         : float[VertexCount * 4]
UV0            : float[VertexCount * 2]
UV1            : float[VertexCount * 2]
BoundsMin      : float[3]   // if HasBounds
BoundsMax      : float[3]
```

### Indices
```
IndicesU16 ?  uint16[IndexCount] : uint32[IndexCount]
```

### Submeshes (repeat `SubMeshCount`)
```
uint8  Topology     // 0=Triangles,1=Lines
uint16 MaterialIndex // 0xFFFF = none
uint32 StartIndex
uint32 IndexCount
int32  BaseVertex
uint32 FirstVertex
uint32 VertexCount
```

### Materials (repeat `MaterialCount`)
```
uint16 nameLen; byte[nameLen]
uint32 PbrFlags

// optional blocks gated by PbrFlags:
BaseColorFactor:  float r,g,b,a
MetallicFactor:   float
RoughnessFactor:  float
EmissiveFactor:   float r,g,b
AlphaMode:        uint8 (0 Opaque,1 Mask,2 Blend) [+ float cutoff if Mask]
DoubleSided:      uint8 (0/1)

uint8 texCount

// per texture (repeat texCount):
uint8  Slot        // 0..4
uint8  UVSet       // 0 or 1
uint8  RefType     // 0 External, 1 Embedded
uint8  TO_Flags    // bit0=HasOffset, bit1=HasScale, bit2=HasRotation
[OffsetX,OffsetY]  // float x2 if HasOffset
[ScaleX,ScaleY]    // float x2 if HasScale
[Rotation]         // float (radians) if HasRotation

// optional sampler when PbrFlags.Sampler is set:
uint8 WrapU; uint8 WrapV; uint8 MinFilter; uint8 MagFilter

// payload:
if External:
    uint16 uriLen; byte[uriLen] UTF-8
else Embedded:
    uint8  MimeKind   // 0 PNG,1 JPG,2 KTX2
    uint32 DataLen
    byte[DataLen] Data
```

---

## Flags Reference

### `GlobalFlags`
- `HasPositions` *(required)*
- `HasNormals`
- `HasTangents`
- `HasVertexColors`
- `HasUV0`
- `HasUV1`
- `HasBounds`
- `IndicesU16` *(uses 16-bit indices)*
- `EmbedTextures` *(informational; set when any texture is embedded)*

### `PbrFlags`
- `BaseColorFactor`
- `MetallicFactor`
- `RoughnessFactor`
- `EmissiveFactor`
- `AlphaMode`
- `DoubleSided`
- `BaseColorTex`
- `MetalRoughTex`
- `NormalTex`
- `OcclusionTex`
- `EmissiveTex`
- `TilingOffset` *(any per-texture scale/offset present)*
- `Sampler` *(per-texture sampler block present)*

---

## Examples

### Import from disk
```csharp
var bytes = File.ReadAllBytes("Assets/Models/ship.vnb");
var go = VNB.Import(bytes, addColliders: true);
go.transform.position = Vector3.zero;
```

### Export a selected GameObject
```csharp
var sel = Selection.activeGameObject;
if (sel != null)
{
    var bytes = VNB.Export(sel);
    File.WriteAllBytes("Assets/Exports/" + sel.name + ".vnb", bytes);
    AssetDatabase.Refresh();
}
```

### Build `VnMeshData` from a `Mesh` (advanced)
```csharp
var mesh = GetComponent<MeshFilter>().sharedMesh;
var data = VnUnityBridge.FromUnityMesh(mesh, subDescs: null,
    withNormals: true, withTangents: false, withColors: false,
    withUV1: true, indicesU16: mesh.indexFormat == IndexFormat.UInt16);
data.Name = "MyMesh";
var vnb = VnExporter.Export(data);
```

### Import with an external texture resolver
```csharp
byte[] bytes = File.ReadAllBytes(path);
var res = VertexNeutralV2.VnImporter.Import(bytes, resolveExternalTexture: uri =>
{
    // Resolve by texture name to PNG/JPG bytes, or return null to keep it external.
    var texPath = Path.Combine(Application.streamingAssetsPath, "Textures", uri + ".png");
    return File.Exists(texPath) ? File.ReadAllBytes(texPath) : null;
});

// Build a GO yourself
var go = new GameObject(res.Name ?? "VN_Imported");
var mf = go.AddComponent<MeshFilter>();
var mr = go.AddComponent<MeshRenderer>();
mf.sharedMesh = res.Mesh;
mr.sharedMaterials = res.Materials;
```

---

## Legacy v1 Fallback

If the file isn’t VN v2 (magic/version mismatch), the importer tries a **best-effort v1** parser:

- Reads multiple “submeshes” with flat color and UVs
- Produces a single `Mesh` with **vertex colors** filled by sub-color
- No materials are created (`Materials` is empty)

This is mainly to keep old assets usable; prefer v2 for new content.

---

## Performance Notes

- Writing/reading uses streaming `BinaryWriter`/`BinaryReader`.
- Large meshes: the importer automatically switches to `IndexFormat.UInt32`.
- Embedding big textures increases file size; consider external references for iteration.

---

## Limitations & Gotchas

- **One mesh container per file** (but many submeshes/materials).
- Only **UV0** and **UV1** are supported.
- Normal map import assumes standard Unity normal map usage (no special unpacking).
- Texture **rotation** is serialized but **not applied** to Unity samplers (Unity doesn’t expose per-sampler rotation). Use authored textures or shader support if needed.
- When exporting, textures must be **readable** to embed. Otherwise, they are stored as **external** (by texture name).
- Material property mapping targets **URP/Lit** first; Standard fallback relies on Unity’s property remap.

---

## FAQ

**Q: Can I store multiple LODs or multiple meshes?**  
A: Not in a single file. Save multiple `.vnb` files or pack them yourself.

**Q: Does it support skinning/bones?**  
A: Not in v2. You can export a `SkinnedMeshRenderer`’s **mesh** and PBR materials, but skinning data is not serialized.

**Q: How do I keep textures external?**  
A: Make your textures non-readable or intercept in export by clearing `EmbeddedData`. At import, pass `resolveExternalTexture` that **returns null** to keep externals unresolved.

**Q: What shaders are supported?**  
A: URP/Lit (preferred) and Standard (fallback). Custom shaders can work if they respect the common property names.

---

## Versioning

- **VN File Format:** v2  
- **CoordSys:** hardcoded to Unity Y-up  
- **UnitScale:** 1.0

Future versions will keep the header/magic so you can branch logic safely.

---

## License

MIT (or your preferred OSS license). Add your license file here.

---

## Contributing

Issues and PRs welcome. Please include:
- A minimal `.vnb` to reproduce problems
- Unity version, render pipeline, and platform
- Whether the problem involves **embedded** or **external** textures

---

## Credits

- Designed for compact, Unity-friendly pipelines.  
- Made to be copy-pasteable and easy to audit.
