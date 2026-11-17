using Source.Common;
using Source.Common.MaterialSystem;
using Source.Common.Mathematics;

using System.Buffers;
using System.Numerics;

namespace Source.StudioRender;

public struct DecalVertex
{
	public Vector3 Position;
	public Vector3 Normal;
	public Vector2 TexCoord;

	public ushort MeshVertexIndex;
	public ushort Body;
	public ushort Model;
	public ushort Mesh;
	public ushort GroupIndex;
	public ushort Group;

	public DecalVertex() {
		MeshVertexIndex = 0xFFFF;
		Body = 0xFFFF;
		Model = 0xFFFF;
		Mesh = 0xFFFF;
		GroupIndex = 0xFFFF;
		Group = 0xFFFF;
	}

	public readonly MStudioMesh? GetMesh(StudioHeader hdr) {
		if ((Body == 0xFFFF) || (Model == 0xFFFF) || (Mesh == 0xFFFF))
			return null;

		MStudioBodyParts body = hdr.BodyPart(Body);
		MStudioModel model = body.Model(Model);
		return model.Mesh(Mesh);
	}

	// todo: GetMorph, StudioMeshGroup has no IMorph yet
}

public struct MeshVertexInfo
{
	public MStudioMesh? Mesh;
	public int Index;
}

public unsafe partial class StudioRender
{
	const int DECAL_CLIP_MINUSU = 0x1;
	const int DECAL_CLIP_MINUSV = 0x2;
	const int DECAL_CLIP_PLUSU = 0x4;
	const int DECAL_CLIP_PLUSV = 0x8;

	const int MAX_DECAL_INDICES_PER_MODEL = 2048;

	const float FRONTFACING_EPS = 0.1f;

	const StudioDecalHandle_t STUDIORENDER_DECAL_INVALID = IStudioRender.STUDIORENDER_DECAL_INVALID;
	const int ADDDECAL_TO_ALL_LODS = IStudioRender.ADDDECAL_TO_ALL_LODS;

	static int g_nTotalDecalVerts;
#if GMOD_DLL
	static int s_MultiBoneDecalWarnings;
	static int s_MultiBoneFlexedDecalWarnings;
#endif

	sealed class DecalClipState
	{
		public int VertCount;
		public readonly int[][] Indices = [new int[7], new int[7]];
		public int Pass;
		public int ClipVertCount;
		public readonly DecalVertex[] ClipVerts = new DecalVertex[16];
		public readonly int[] ClipFlags = new int[16];
	}

	sealed class Decal
	{
		public int IndexCount;
		public int VertexCount;
		public float FadeStartTime;
		public float FadeDuration;
		public int Flags;
	}

	struct DecalHistory
	{
		public DecalMaterial Material;
		public LinkedListNode<Decal> Decal;
		public ushort Id;
	}

	sealed class DecalMaterial
	{
		public IMaterial? Material;
		public readonly List<ushort> Indices = [];
		public readonly List<DecalVertex> Vertices = [];
		public readonly LinkedList<Decal> Decals = [];
	}

	sealed class DecalLod
	{
		public readonly LinkedList<DecalMaterial> Materials = [];
		public readonly LinkedList<DecalHistory> DecalHistory = [];
	}

	sealed class DecalModelList
	{
		public StudioHWData HardwareData = null!;
		public DecalLod[] Lod = null!;
		public int Lods;
	}

	struct DecalBuildVertexInfo
	{
		public const byte FRONT_FACING = 0x1;
		public const byte VALID_AREA = 0x2;

		public Vector2 UV;
		public ushort VertexIndex;
		public byte UniqueID;
		public byte Flags;
	}

	struct DecalBuildInfo
	{
		public IMaterial[]? Materials;
		public StudioHeader StudioHdr;
		public MStudioMesh Mesh;
		public StudioMeshData MeshData;
		public DecalMaterial DecalMaterial;
		public MeshVertexInfo[] MeshVertices;
		public MStudioMeshVertexData? MeshVertexData;
		public int GlobalMeshIndex;
		public DecalBuildVertexInfo[] VertexBuffer;
		public float Radius;
		public int VertexInfo;
		public int Body;
		public int Model;
		public int MeshIdx;
		public int Group;
		public int FirstVertex;
		public ushort VertexCount;
		public bool UseClipVert;
		public bool NoPokeThru;
	}

	struct DecalLRU
	{
		public StudioDecalHandle_t DecalHandle;
		public ushort DecalId;
	}

	readonly List<DecalModelList?> DecalList = [null];
	readonly Stack<int> FreeDecalLists = [];
	readonly Lock DecalMutex = new();
	readonly LinkedList<DecalLRU> DecalLRUList = [];
	readonly DecalClipState ClipState = new();
	ushort DecalId;

	void RemoveDecalListFromLRU(StudioDecalHandle_t h) {
		LinkedListNode<DecalLRU>? next;
		for (LinkedListNode<DecalLRU>? i = DecalLRUList.First; i != null; i = next) {
			next = i.Next;
			if (i.Value.DecalHandle == h)
				DecalLRUList.Remove(i);
		}
	}

	internal StudioDecalHandle_t CreateDecalList(StudioHWData? hardwareData) {
		if (hardwareData == null || hardwareData.NumLODs <= 0)
			return STUDIORENDER_DECAL_INVALID;

		DecalModelList decal = new();

		int handle;
		lock (DecalMutex) {
			if (FreeDecalLists.TryPop(out handle))
				DecalList[handle] = decal;
			else {
				handle = DecalList.Count;
				DecalList.Add(decal);
			}
		}

		decal.HardwareData = hardwareData;
		decal.Lod = new DecalLod[hardwareData.NumLODs];
		decal.Lods = hardwareData.NumLODs;

		for (int i = 0; i < hardwareData.NumLODs; i++)
			decal.Lod[i] = new();

		return (StudioDecalHandle_t)handle;
	}

	internal void DestroyDecalList(StudioDecalHandle_t hDecal) {
		if (hDecal == STUDIORENDER_DECAL_INVALID)
			return;

		RemoveDecalListFromLRU(hDecal);

		int h = hDecal;
		DecalModelList decal = DecalList[h]!;

		// Clean up
		for (int i = 0; i < decal.Lods; i++) {
			foreach (DecalMaterial mat in decal.Lod[i].Materials) {
				g_nTotalDecalVerts -= mat.Vertices.Count;
#if GMOD_DLL
				mat.Material?.DecrementReferenceCount();
#endif
			}

			decal.Lod[i].Materials.Clear();
		}

		decal.Lod = null!;
		lock (DecalMutex) {
			DecalList[h] = null;
			FreeDecalLists.Push(h);
		}
	}

	bool IsFrontFacing(in Vector3 norm, in MStudioBoneWeight boneweight) {
		float z;
		if (boneweight.NumBones == 1) {
			ref Matrix3x4 m = ref PoseToDecal[boneweight.Bone[0]];
			z = norm.X * m.M20 + norm.Y * m.M21 + norm.Z * m.M22;
		}
		else {
			float zbone;

			z = 0;
			for (int i = 0; i < boneweight.NumBones; i++) {
				ref Matrix3x4 m = ref PoseToDecal[boneweight.Bone[i]];
				zbone = norm.X * m.M20 + norm.Y * m.M21 + norm.Z * m.M22;
				z += zbone * boneweight.Weight[i];
			}
		}

		return z >= FRONTFACING_EPS;
	}

	bool TransformToDecalSpace(ref DecalBuildInfo build, in Vector3 pos, in MStudioBoneWeight boneweight, out Vector2 uv) {
		if (boneweight.NumBones == 1) {
			ref Matrix3x4 m = ref PoseToDecal[boneweight.Bone[0]];
			uv.X = pos.X * m.M00 + pos.Y * m.M01 + pos.Z * m.M02 + m.M03;
			uv.Y = pos.X * m.M10 + pos.Y * m.M11 + pos.Z * m.M12 + m.M13;
		}
		else {
			uv.X = uv.Y = 0;
			float ubone, vbone;
			for (int i = 0; i < boneweight.NumBones; i++) {
				ref Matrix3x4 m = ref PoseToDecal[boneweight.Bone[i]];
				ubone = pos.X * m.M00 + pos.Y * m.M01 + pos.Z * m.M02 + m.M03;
				vbone = pos.X * m.M10 + pos.Y * m.M11 + pos.Z * m.M12 + m.M13;

				uv.X += ubone * boneweight.Weight[i];
				uv.Y += vbone * boneweight.Weight[i];
			}
		}

		if (!build.NoPokeThru)
			return true;

		float z;
		if (boneweight.NumBones == 1) {
			ref Matrix3x4 m = ref PoseToDecal[boneweight.Bone[0]];
			z = pos.X * m.M20 + pos.Y * m.M21 + pos.Z * m.M22 + m.M23;
		}
		else {
			z = 0;
			float zbone;
			for (int i = 0; i < boneweight.NumBones; i++) {
				ref Matrix3x4 m = ref PoseToDecal[boneweight.Bone[i]];
				zbone = pos.X * m.M20 + pos.Y * m.M21 + pos.Z * m.M22 + m.M23;
				z += zbone * boneweight.Weight[i];
			}
		}

		return MathF.Abs(z) < build.Radius;
	}

	bool ProjectDecalOntoMesh(ref DecalBuildInfo build, Span<DecalBuildVertexInfo> vertexInfo, MStudioMesh mesh, float w, float h) {
		float invRadius = (build.Radius != 0.0f) ? 1.0f / build.Radius : 1.0f;

		MStudioMeshVertexData? vertData = mesh.GetVertexData(studioDataCache, build.StudioHdr);
		// todo: thin vertex data (GetThinVertexData), vertices are never thinned on load here
		if (vertData == null)
			return false;

		for (int j = 0; j < mesh.NumVertices; ++j) {
			ref MStudioVertex vert = ref vertData.Vertex(j);

			vertexInfo[j].VertexIndex = 0xFFFF;
			vertexInfo[j].UniqueID = 0xFF;
			vertexInfo[j].Flags = 0;

			if (!IsFrontFacing(in vert.Normal, in vert.BoneWeights))
				continue;

			vertexInfo[j].Flags |= DecalBuildVertexInfo.FRONT_FACING;

			bool inValidArea = TransformToDecalSpace(ref build, in vert.Position, in vert.BoneWeights, out vertexInfo[j].UV);
			vertexInfo[j].Flags |= (byte)((inValidArea ? 1 : 0) << 1);

			vertexInfo[j].UV *= invRadius * 0.5f;
#if GMOD_DLL
			if (w > 0.0f)
				vertexInfo[j].UV.X /= w;
			if (h > 0.0f)
				vertexInfo[j].UV.Y /= h;
#endif
			vertexInfo[j].UV.X += 0.5f;
			vertexInfo[j].UV.Y += 0.5f;
		}
		return true;
	}

	static int ComputeClipFlags(in Vector2 uv) {
		int flags = 0;

		if (uv.X < 0.0f)
			flags |= DECAL_CLIP_MINUSU;
		else if (uv.X > 1.0f)
			flags |= DECAL_CLIP_PLUSU;

		if (uv.Y < 0.0f)
			flags |= DECAL_CLIP_MINUSV;
		else if (uv.Y > 1.0f)
			flags |= DECAL_CLIP_PLUSV;

		return flags;
	}

	static int IntersectPlane(DecalClipState state, int start, int end, int normalInd, float val) {
		ref DecalVertex startVert = ref state.ClipVerts[start];
		ref DecalVertex endVert = ref state.ClipVerts[end];

		Vector2 dir = endVert.TexCoord - startVert.TexCoord;
		Assert(dir[normalInd] != 0.0f);
		float t = (val - startVert.TexCoord[normalInd]) / dir[normalInd];

		ref DecalVertex outVert = ref state.ClipVerts[state.ClipVertCount];
		int newVert = state.ClipVertCount++;

		outVert.MeshVertexIndex = 0xFFFF;
		outVert.Mesh = 0xFFFF;
		outVert.Model = 0xFFFF;
		outVert.Body = 0xFFFF;

#if GMOD_DLL
		double oneMinusT = 1.0 - t;
		outVert.Position.X = (float)(endVert.Position.X * t + startVert.Position.X * oneMinusT);
		outVert.Position.Y = (float)(endVert.Position.Y * t + startVert.Position.Y * oneMinusT);
		outVert.Position.Z = (float)(endVert.Position.Z * t + startVert.Position.Z * oneMinusT);

		Vector3 normal;
		normal.X = (float)(endVert.Position.X * t + startVert.Position.X * oneMinusT);
		normal.Y = (float)(endVert.Position.Y * t + startVert.Position.Y * oneMinusT);
		normal.Z = (float)(endVert.Position.Z * t + startVert.Position.Z * oneMinusT);
#else
		outVert.Position.X = startVert.Position.X * (1.0F - t) + endVert.Position.X * t;
		outVert.Position.Y = startVert.Position.Y * (1.0F - t) + endVert.Position.Y * t;
		outVert.Position.Z = startVert.Position.Z * (1.0F - t) + endVert.Position.Z * t;

		Vector3 normal;
		normal.X = startVert.Position.X * (1.0F - t) + endVert.Position.X * t;
		normal.Y = startVert.Position.Y * (1.0F - t) + endVert.Position.Y * t;
		normal.Z = startVert.Position.Z * (1.0F - t) + endVert.Position.Z * t;
#endif
		MathLib.VectorNormalize(ref normal);
		outVert.Normal = normal;

		MathLib.Vector2DLerp(in startVert.TexCoord, in endVert.TexCoord, t, out Vector2 texCoord);
		outVert.TexCoord = texCoord;

		state.ClipFlags[newVert] = ComputeClipFlags(in outVert.TexCoord);

		return newVert;
	}

	static void ClipTriangleAgainstPlane(DecalClipState state, int normalInd, int flag, float val) {
		int outVertCount = 0;
		int start = state.Indices[state.Pass][state.VertCount - 1];
		bool startInside = (state.ClipFlags[start] & flag) == 0;
		for (int i = 0; i < state.VertCount; ++i) {
			int end = state.Indices[state.Pass][i];

			bool endInside = (state.ClipFlags[end] & flag) == 0;
			if (endInside) {
				if (!startInside) {
					int clipVert = IntersectPlane(state, start, end, normalInd, val);
					state.Indices[state.Pass ^ 1][outVertCount++] = clipVert;
				}
				state.Indices[state.Pass ^ 1][outVertCount++] = end;
			}
			else {
				if (startInside) {
					int clipVert = IntersectPlane(state, start, end, normalInd, val);
					state.Indices[state.Pass ^ 1][outVertCount++] = clipVert;
				}
			}
			start = end;
			startInside = endInside;
		}

		state.Pass ^= 1;
		state.VertCount = outVertCount;
	}

	void ConvertMeshVertexToDecalVertex(ref DecalBuildInfo build, int meshIndex, ref DecalVertex decalVertex, int groupIndex = 0xFFFF) {
		Assert(meshIndex < Studio.MAXSTUDIOVERTS);

		// todo: thin vertex data (GetMeshPosition / GetMeshNormal)
		decalVertex.Position = build.MeshVertexData!.Position(meshIndex);
		decalVertex.Normal = build.MeshVertexData.Normal(meshIndex);

		decalVertex.TexCoord = build.VertexBuffer[build.VertexInfo + meshIndex].UV;
		decalVertex.MeshVertexIndex = (ushort)meshIndex;
		decalVertex.Mesh = (ushort)build.MeshIdx;
		Assert(decalVertex.Mesh < 100);
		decalVertex.Model = (ushort)build.Model;
		decalVertex.Body = (ushort)build.Body;
		decalVertex.Group = (ushort)build.Group;
		decalVertex.GroupIndex = (ushort)groupIndex;
	}

	ushort AddVertexToDecal(ref DecalBuildInfo build, int meshIndex, int groupIndex = 0xFFFF) {
		ref DecalBuildVertexInfo vertexInfo = ref build.VertexBuffer[build.VertexInfo + meshIndex];

		if (vertexInfo.UniqueID != build.GlobalMeshIndex) {
			vertexInfo.UniqueID = (byte)build.GlobalMeshIndex;
			List<DecalVertex> decalVertexList = build.DecalMaterial.Vertices;

			int v = decalVertexList.Count;
			g_nTotalDecalVerts++;

			DecalVertex decalVertex = new();
			ConvertMeshVertexToDecalVertex(ref build, meshIndex, ref decalVertex, groupIndex);
			decalVertexList.Add(decalVertex);

			vertexInfo.VertexIndex = build.VertexCount;
			++build.VertexCount;
			if (build.FirstVertex == -1)
				build.FirstVertex = v;
		}

		return vertexInfo.VertexIndex;
	}

	ushort AddVertexToDecal(ref DecalBuildInfo build, ref DecalVertex vert) {
		List<DecalVertex> decalVertexList = build.DecalMaterial.Vertices;

		ushort vertexCount = 0;
		if (build.FirstVertex != -1) {
			for (int i = build.FirstVertex; i < decalVertexList.Count; ++i, ++vertexCount) {
				DecalVertex other = decalVertexList[i];
				if (other.GetMesh(build.StudioHdr) != null)
					continue;

				Vector3 temp = other.Position - vert.Position;
				if ((MathF.Abs(temp.X) > 1e-3) || (MathF.Abs(temp.Y) > 1e-3) || (MathF.Abs(temp.Z) > 1e-3))
					continue;

				temp = other.Normal - vert.Normal;
				if ((MathF.Abs(temp.X) > 1e-3) || (MathF.Abs(temp.Y) > 1e-3) || (MathF.Abs(temp.Z) > 1e-3))
					continue;

				return vertexCount;
			}
		}

		Assert((vert.TexCoord.X >= -1e-3f) && (vert.TexCoord.X - 1.0f < 1e-3f));
		Assert((vert.TexCoord.Y >= -1e-3f) && (vert.TexCoord.Y - 1.0f < 1e-3f));

		int idx = decalVertexList.Count;
		decalVertexList.Add(vert);
		g_nTotalDecalVerts++;
		if (build.FirstVertex == -1)
			build.FirstVertex = idx;
		Assert(vertexCount == build.VertexCount);
		return build.VertexCount++;
	}

	void AddClippedDecalToTriangle(ref DecalBuildInfo build, DecalClipState clipState) {
		Assert(clipState.VertCount <= 7);

		int i;
		Span<ushort> indices = stackalloc ushort[7];
		for (i = 0; i < clipState.VertCount; ++i) {
			int vertIdx = clipState.Indices[clipState.Pass][i];
			if (vertIdx < 3)
				indices[i] = AddVertexToDecal(ref build, clipState.ClipVerts[vertIdx].MeshVertexIndex);
			else
				indices[i] = AddVertexToDecal(ref build, ref clipState.ClipVerts[vertIdx]);
		}

		for (i = 1; i < clipState.VertCount - 1; ++i) {
			build.DecalMaterial.Indices.Add(indices[0]);
			build.DecalMaterial.Indices.Add(indices[i]);
			build.DecalMaterial.Indices.Add(indices[i + 1]);
		}
	}

	bool ClipDecal(ref DecalBuildInfo build, int i1, int i2, int i3, ReadOnlySpan<int> clipFlags) {
		int i;

		DecalClipState clipState = ClipState;
		clipState.VertCount = 3;
		clipState.ClipVerts[0] = new();
		clipState.ClipVerts[1] = new();
		clipState.ClipVerts[2] = new();
		ConvertMeshVertexToDecalVertex(ref build, i1, ref clipState.ClipVerts[0]);
		ConvertMeshVertexToDecalVertex(ref build, i2, ref clipState.ClipVerts[1]);
		ConvertMeshVertexToDecalVertex(ref build, i3, ref clipState.ClipVerts[2]);
		for (i = 3; i < clipState.ClipVerts.Length; ++i)
			clipState.ClipVerts[i] = new();
		clipState.ClipVertCount = 3;

		for (i = 0; i < 3; ++i) {
			clipState.ClipFlags[i] = clipFlags[i];
			clipState.Indices[0][i] = i;
		}
		clipState.Pass = 0;

		ClipTriangleAgainstPlane(clipState, 0, DECAL_CLIP_MINUSU, 0.0f);
		if (clipState.VertCount < 3)
			return false;

		ClipTriangleAgainstPlane(clipState, 0, DECAL_CLIP_PLUSU, 1.0f);
		if (clipState.VertCount < 3)
			return false;

		ClipTriangleAgainstPlane(clipState, 1, DECAL_CLIP_MINUSV, 0.0f);
		if (clipState.VertCount < 3)
			return false;

		ClipTriangleAgainstPlane(clipState, 1, DECAL_CLIP_PLUSV, 1.0f);
		if (clipState.VertCount < 3)
			return false;

		if (build.UseClipVert)
			AddClippedDecalToTriangle(ref build, clipState);

		return true;
	}

	void AddTriangleToDecal(ref DecalBuildInfo build, int i1, int i2, int i3, int gi1, int gi2, int gi3) {
		Span<DecalBuildVertexInfo> vertexInfo = build.VertexBuffer.AsSpan(build.VertexInfo);

		int allFrontFacing = vertexInfo[i1].Flags & vertexInfo[i2].Flags & vertexInfo[i3].Flags;
		if ((allFrontFacing & DecalBuildVertexInfo.FRONT_FACING) == 0)
			return;

		int allNotInValidArea = vertexInfo[i1].Flags | vertexInfo[i2].Flags | vertexInfo[i3].Flags;
		if ((allNotInValidArea & DecalBuildVertexInfo.VALID_AREA) == 0)
			return;

		Span<int> clipFlags = stackalloc int[3];

		clipFlags[0] = ComputeClipFlags(in vertexInfo[i1].UV);
		clipFlags[1] = ComputeClipFlags(in vertexInfo[i2].UV);
		clipFlags[2] = ComputeClipFlags(in vertexInfo[i3].UV);

		if ((clipFlags[0] & (clipFlags[1] & clipFlags[2])) != 0)
			return;

		bool doClip = true;

		if ((!build.UseClipVert) && (clipFlags[0] == 0 || clipFlags[1] == 0 || clipFlags[2] == 0))
			doClip = false;

		if (doClip && (clipFlags[0] != 0 || clipFlags[1] != 0 || clipFlags[2] != 0)) {
			bool validTri = ClipDecal(ref build, i1, i2, i3, clipFlags);

			if (build.UseClipVert || (!validTri))
				return;
		}

		i1 = AddVertexToDecal(ref build, i1, gi1);
		i2 = AddVertexToDecal(ref build, i2, gi2);
		i3 = AddVertexToDecal(ref build, i3, gi3);

		build.DecalMaterial.Indices.Add((ushort)i1);
		build.DecalMaterial.Indices.Add((ushort)i2);
		build.DecalMaterial.Indices.Add((ushort)i3);
	}

	void AddDecalToMesh(ref DecalBuildInfo build) {
		ref MeshVertexInfo vertexInfo = ref build.MeshVertices[build.GlobalMeshIndex];
		if (vertexInfo.Index < 0)
			return;

		build.VertexInfo = vertexInfo.Index;

		for (int j = 0; j < build.MeshData.NumGroup; ++j) {
			build.Group = j;
			StudioMeshGroup group = build.MeshData.MeshGroup![j];

			for (int k = 0; k < group.NumStrips; ++k) {
				ref OptimizedModel.StripHeader strip = ref group.StripData![k];
				if ((strip.Flags & OptimizedModel.StripHeaderFlags.IsTriStrip) != 0) {
					for (int i = 0; i < strip.NumIndices - 2; ++i) {
						int ccw = (i & 0x1) == 0 ? 1 : 0;
						int ti1 = strip.IndexOffset + i;
						int ti2 = ti1 + 1 + ccw;
						int ti3 = ti1 + 2 - ccw;
						int i1 = group.MeshIndex(ti1);
						int i2 = group.MeshIndex(ti2);
						int i3 = group.MeshIndex(ti3);

						AddTriangleToDecal(ref build, i1, i2, i3, group.Indices![ti1], group.Indices[ti2], group.Indices[ti3]);
					}
				}
				else {
					Assert((strip.Flags & OptimizedModel.StripHeaderFlags.IsTriList) != 0);
					for (int i = 0; i < strip.NumIndices; i += 3) {
						int idx = strip.IndexOffset + i;

						int i1 = group.MeshIndex(idx);
						int i2 = group.MeshIndex(idx + 1);
						int i3 = group.MeshIndex(idx + 2);

						AddTriangleToDecal(ref build, i1, i2, i3, group.Indices![idx], group.Indices[idx + 1], group.Indices[idx + 2]);
					}
				}
			}
		}
	}

	bool AddDecalToModel(ref DecalBuildInfo buildInfo) {
		for (int i = 0; i < SubModel!.NumMeshes; ++i) {
			buildInfo.MeshIdx = i;
			buildInfo.Mesh = SubModel.Mesh(i);
			buildInfo.MeshData = StudioMeshes![buildInfo.Mesh.MeshID];
			Assert(buildInfo.MeshData != null);

			buildInfo.MeshVertexData = buildInfo.Mesh.GetVertexData(studioDataCache, buildInfo.StudioHdr);
			// todo: thin vertex data (GetThinVertexData)
			if (buildInfo.MeshVertexData == null)
				return false;

			AddDecalToMesh(ref buildInfo);
			++buildInfo.GlobalMeshIndex;
		}
		return true;
	}

	bool ComputePoseToDecal(in Vector3 rayStart, in Vector3 rayDelta, in Vector3 up) {
		Matrix3x4 worldToDecal = default;
		Vector3 decalU, decalV, decalN;

		decalN = rayDelta * -1.0f;
		if (MathLib.VectorNormalize(ref decalN) == 0.0f)
			return false;

		decalU = Vector3.Cross(up, decalN);
		if (MathLib.VectorNormalize(ref decalU) < 1e-3) {
			Vector3 fixup = new(up.Y, up.Z, up.X);
			decalU = Vector3.Cross(fixup, decalN);
			if (MathLib.VectorNormalize(ref decalU) < 1e-3)
				return false;
		}

		decalV = Vector3.Cross(decalN, decalU);

		worldToDecal.M00 = decalU.X; worldToDecal.M01 = decalU.Y; worldToDecal.M02 = decalU.Z;
		worldToDecal.M10 = decalV.X; worldToDecal.M11 = decalV.Y; worldToDecal.M12 = decalV.Z;
		worldToDecal.M20 = decalN.X; worldToDecal.M21 = decalN.Y; worldToDecal.M22 = decalN.Z;

		worldToDecal.M03 = -Vector3.Dot(rayStart, decalU);
		worldToDecal.M13 = -Vector3.Dot(rayStart, decalV);
		worldToDecal.M23 = -Vector3.Dot(rayStart, decalN);

		for (int i = 0; i < StudioHdr!.NumBones; i++)
			MathLib.ConcatTransforms(in worldToDecal, in PoseToWorld[i], out PoseToDecal[i]);

		return true;
	}

	static DecalMaterial GetDecalMaterial(DecalLod decalLod, IMaterial decalMaterial) {
		foreach (DecalMaterial j in decalLod.Materials) {
			if (j.Material == decalMaterial)
				return j;
		}

		DecalMaterial material = new();
		decalLod.Materials.AddFirst(material);
		material.Material = decalMaterial;
#if GMOD_DLL
		decalMaterial.IncrementReferenceCount();
#endif

		return material;
	}

	void RetireDecal(DecalModelList list, ushort retireID, int lod, int maxLOD) {
		LinkedListNode<DecalLRU>? i;
		for (i = DecalLRUList.First; i != null; i = i.Next) {
			if (retireID == i.Value.DecalId) {
				DecalLRUList.Remove(i);
				break;
			}
		}
		Assert(i != null);

		LinkedList<DecalHistory> historyList = list.Lod[lod].DecalHistory;
		Assert(historyList.Count != 0);
		if (historyList.Count == 0)
			return;

		for (int iLod = (maxLOD - 1); iLod >= list.HardwareData.RootLOD; --iLod) {
			historyList = list.Lod[iLod].DecalHistory;

			LinkedListNode<DecalHistory>? node = historyList.First;
			LinkedListNode<DecalHistory>? next;

			while (node != null) {
				next = node.Next;

				ref DecalHistory decalHistory = ref node.ValueRef;
				if (decalHistory.Id != retireID) {
					node = next;
					continue;
				}

				DecalMaterial material = decalHistory.Material;
				{
					List<DecalVertex> vertices = material.Vertices;
					Decal decalToRemove = decalHistory.Decal.Value;

					Assert(vertices.Count >= decalToRemove.VertexCount);
					int vertsToRemove = decalToRemove.VertexCount;
					vertices.RemoveRange(0, vertsToRemove);
					g_nTotalDecalVerts -= vertsToRemove;

					if (vertices.Count == 0)
						vertices.TrimExcess();

					material.Indices.RemoveRange(0, decalToRemove.IndexCount);
					if (material.Indices.Count == 0)
						material.Indices.TrimExcess();

					material.Decals.Remove(decalHistory.Decal);
				}

				historyList.Remove(node);

				node = next;
			}
		}
	}

	int ComputeTotalMeshCount(int rootLOD, int maxLOD, int body) {
		int meshCount = 0;
		for (int k = 0; k < StudioHdr!.NumBodyParts; k++) {
			R_StudioSetupModel(k, body, out MStudioModel? subModel, StudioHdr);
			meshCount += subModel!.NumMeshes;
		}

		meshCount *= maxLOD - rootLOD + 1;

		return meshCount;
	}

	int ComputeVertexAllocation(int maxLOD, int body, StudioHWData hardwareData, Span<MeshVertexInfo> meshVertices) {
		bool suppressTlucDecal = (StudioHdr!.Flags & StudioHdrFlags.TranslucentTwoPass) != 0;

		int currMesh = 0;
		int vertexCount = 0;
		for (int i = maxLOD - 1; i >= hardwareData.RootLOD; i--) {
			IMaterial[] materials = hardwareData.LODs![i].Materials!;

			for (int k = 0; k < StudioHdr.NumBodyParts; k++) {
				R_StudioSetupModel(k, body, out MStudioModel? subModel, StudioHdr);

				for (int meshID = 0; meshID < subModel!.NumMeshes; ++meshID, ++currMesh) {
					MStudioMesh mesh = subModel.Mesh(meshID);

					meshVertices[currMesh].Mesh = mesh;

					int n;
					for (n = currMesh; --n >= 0;) {
						if (meshVertices[n].Mesh == mesh) {
							meshVertices[currMesh].Index = meshVertices[n].Index;
							break;
						}
					}
					if (n >= 0)
						continue;

					Span<short> skinRef = StudioHdr.SkinRef(0);
					IMaterial material = materials[skinRef[mesh.Material]];
					if (suppressTlucDecal) {
						if (material.IsTranslucent()) {
							meshVertices[currMesh].Index = -1;
							continue;
						}
					}

					if (material.GetMaterialVarFlag(MaterialVarFlags.SuppressDecals)) {
						meshVertices[currMesh].Index = -1;
						continue;
					}

#if GMOD_DLL
					if (mesh.NumVertices == 0) {
						meshVertices[currMesh].Index = -1;
						continue;
					}
#endif

					meshVertices[currMesh].Index = vertexCount;
					vertexCount += mesh.NumVertices;
				}
			}
		}

		return vertexCount;
	}

	void ProjectDecalsOntoMeshes(ref DecalBuildInfo build, int meshCount, float w, float h) {
		int maxVertexIndex = -1;

		for (int i = 0; i < meshCount; ++i) {
			int index = build.MeshVertices[i].Index;

			if ((index < 0) || (index <= maxVertexIndex))
				continue;

			maxVertexIndex = index;

			ProjectDecalOntoMesh(ref build, build.VertexBuffer.AsSpan(index), build.MeshVertices[i].Mesh!, w, h);
		}
	}

	internal void AddDecal(StudioDecalHandle_t hDecal, StudioRenderCtx rc, Span<Matrix3x4> boneToWorld, StudioHeader studioHdr, in Vector3 rayStart, in Vector3 rayDelta, in Vector3 decalUp, IMaterial decalMaterial, float radius, int body, bool noPokethru, int maxLODToDecal, float w, float h) {
		if (hDecal == STUDIORENDER_DECAL_INVALID)
			return;

		DecalModelList list = DecalList[hDecal]!;

		if (list.HardwareData.NumStudioMeshes == 0)
			return;

		fixed (Matrix3x4* pBtW = boneToWorld) {
			pRC = rc;
			StudioHdr = studioHdr;
			pBoneToWorld = pBtW;
			nBoneToWorld = boneToWorld.Length;

			AddDecalToList(hDecal, list, in rayStart, in rayDelta, in decalUp, decalMaterial, radius, body, noPokethru, maxLODToDecal, w, h);

			StudioHdr = null;
			pRC = null;
			pBoneToWorld = null;
		}
	}

	void AddDecalToList(StudioDecalHandle_t hDecal, DecalModelList list, in Vector3 rayStart, in Vector3 rayDelta, in Vector3 decalUp, IMaterial decalMaterial, float radius, int body, bool noPokethru, int maxLODToDecal, float w, float h) {
		StudioHeader studioHdr = StudioHdr!;

		ComputePoseToWorld(PoseToWorld, studioHdr, Studio.BONE_USED_BY_ANYTHING, in pRC!.ViewOrigin, pBoneToWorld);

		if (!ComputePoseToDecal(in rayStart, in rayDelta, in decalUp))
			return;

		IMaterialVar modelMaterialVar = decalMaterial.FindVar("$modelmaterial", out bool found, false);
		if (found) {
			IMaterial? modelMaterial = materialSystem.FindMaterial(modelMaterialVar.GetStringValue(), TEXTURE_GROUP_DECAL, false);
			if (!modelMaterial.IsErrorMaterial())
				decalMaterial = modelMaterial;
		}

		float fadeStartTime = 0.0f;
		float fadeDuration = 0.0f;
		int flags = 0;

		DecalBuildInfo buildInfo = default;
		buildInfo.Radius = radius;
		buildInfo.NoPokeThru = noPokethru;
		buildInfo.StudioHdr = studioHdr;
		buildInfo.UseClipVert = (studioHdr.NumBones <= 1) && (studioHdr.NumFlexDesc == 0);
		buildInfo.GlobalMeshIndex = 0;
		buildInfo.MeshVertexData = null;

		int iMaxLOD;
		if (maxLODToDecal == ADDDECAL_TO_ALL_LODS)
			iMaxLOD = list.HardwareData.NumLODs;
		else
			iMaxLOD = Math.Min(list.HardwareData.NumLODs, maxLODToDecal);

		int meshCount = ComputeTotalMeshCount(list.HardwareData.RootLOD, iMaxLOD - 1, body);

		if (meshCount >= 255) {
			Warning($"Unable to apply decals to model ({studioHdr.GetName()}), it has more than 255 unique meshes!\n");
			return;
		}

		MeshVertexInfo[] meshVertices = ArrayPool<MeshVertexInfo>.Shared.Rent(Math.Max(meshCount, 1));
		buildInfo.MeshVertices = meshVertices;
		int vertexCount = ComputeVertexAllocation(iMaxLOD, body, list.HardwareData, meshVertices);
#if GMOD_DLL
		if (vertexCount * sizeof(DecalBuildVertexInfo) > 1048576) {
			DevWarning(1, $"Unable to apply decals to model ({studioHdr.GetName()}), crashes would occur! ({vertexCount})\n");
			ArrayPool<MeshVertexInfo>.Shared.Return(meshVertices, true);
			return;
		}
#endif
		DecalBuildVertexInfo[] vertexBuffer = ArrayPool<DecalBuildVertexInfo>.Shared.Rent(Math.Max(vertexCount, 1));
		buildInfo.VertexBuffer = vertexBuffer;

		ProjectDecalsOntoMeshes(ref buildInfo, meshCount, w, h);

		int rootLOD = list.HardwareData.RootLOD;
		int finalLOD = list.HardwareData.NumLODs;
		LinkedList<DecalHistory> historyList = list.Lod[list.HardwareData.RootLOD].DecalHistory;
#if GMOD_DLL
		if (DecalLRUList.Count > 0 && DecalLRUList.Count >= pRC.Config.MaxDecalsPerModel * 1.5) {
#else
		if (DecalLRUList.Count >= pRC.Config.MaxDecalsPerModel * 1.5) {
#endif
			DecalLRU head = DecalLRUList.First!.Value;
			DecalModelList modelList = DecalList[head.DecalHandle]!;
			RetireDecal(modelList, head.DecalId, modelList.HardwareData.RootLOD, modelList.HardwareData.NumLODs);
		}

#if GMOD_DLL
		if (historyList.Count != 0 && historyList.Count >= pRC.Config.MaxDecalsPerModel) {
#else
		if (historyList.Count >= pRC.Config.MaxDecalsPerModel) {
#endif
			ushort retireID = historyList.First!.Value.Id;
			RetireDecal(DecalList[hDecal]!, retireID, rootLOD, finalLOD);
		}

		for (int i = iMaxLOD - 1; i >= list.HardwareData.RootLOD; i--) {
			DecalMaterial dm = GetDecalMaterial(list.Lod[i], decalMaterial);

			while (dm.Indices.Count > MAX_DECAL_INDICES_PER_MODEL) {
				LinkedList<DecalHistory> l = list.Lod[i].DecalHistory;
				RetireDecal(list, l.First!.Value.Id, rootLOD, finalLOD);
			}
		}

		bool addedDecals = false;
		for (int i = iMaxLOD - 1; i >= list.HardwareData.RootLOD; i--) {
			DecalMaterial material = GetDecalMaterial(list.Lod[i], decalMaterial);
			buildInfo.DecalMaterial = material;

			StudioMeshes = list.HardwareData.LODs![i].MeshData;

			buildInfo.Materials = list.HardwareData.LODs[i].Materials;

			buildInfo.FirstVertex = -1;
			buildInfo.VertexCount = 0;

			int prevIndexCount = material.Indices.Count;

			int k;
			for (k = 0; k < studioHdr.NumBodyParts; k++) {
				int model = R_StudioSetupModel(k, body, out SubModel, studioHdr);
				buildInfo.Body = k;
				buildInfo.Model = model;
				if (!AddDecalToModel(ref buildInfo))
					break;
			}

			if (k != studioHdr.NumBodyParts)
				continue;

			if (buildInfo.VertexCount != 0) {
				addedDecals = true;

				int decalIndexCount = material.Indices.Count - prevIndexCount;
				Assert(decalIndexCount > 0);

				Decal decal = new();
				LinkedListNode<Decal> decalNode = material.Decals.AddLast(decal);
				decal.VertexCount = buildInfo.VertexCount;
				decal.IndexCount = decalIndexCount;
				decal.FadeStartTime = fadeStartTime;
				decal.FadeDuration = fadeDuration;
				decal.Flags = flags;

				DecalHistory d;
				d.Material = material;
				d.Decal = decalNode;
				d.Id = DecalId;
				list.Lod[i].DecalHistory.AddLast(d);
			}
		}

		if (addedDecals) {
			DecalLRU lru;
			lru.DecalId = DecalId;
			lru.DecalHandle = hDecal;
			DecalLRUList.AddLast(lru);

			++DecalId;
		}

		StudioMeshes = null;
		ArrayPool<MeshVertexInfo>.Shared.Return(meshVertices, true);
		ArrayPool<DecalBuildVertexInfo>.Shared.Return(vertexBuffer);
	}

	static void DecalBoneState(ref MeshBuilder meshBuilder) {
		if (meshBuilder.NumBoneWeights() > 0) {
			meshBuilder.BoneWeight(0, 1.0f);
			meshBuilder.BoneWeight(1, 0.0f);
			meshBuilder.BoneWeight(2, 0.0f);
			meshBuilder.BoneWeight(3, 0.0f);
		}

		meshBuilder.BoneMatrix(0, 0);
		meshBuilder.BoneMatrix(1, 0);
		meshBuilder.BoneMatrix(2, 0);
		meshBuilder.BoneMatrix(3, 0);
	}

	void DrawSingleBoneDecals(ref MeshBuilder meshBuilder, DecalMaterial decalMaterial) {
		List<DecalVertex> verts = decalMaterial.Vertices;
		for (int i = 0; i < verts.Count; ++i) {
			DecalVertex vertex = verts[i];

			meshBuilder.Position3fv(in vertex.Position);
			meshBuilder.Normal3fv(in vertex.Normal);
			meshBuilder.TexCoord2fv(0, in vertex.TexCoord);
			meshBuilder.Color4ub(255, 255, 255, 255);

			DecalBoneState(ref meshBuilder);

			meshBuilder.AdvanceVertex();
		}
	}

	void DrawSingleBoneFlexedDecals(IMatRenderContext renderContext, ref MeshBuilder meshBuilder, DecalMaterial decalMaterial) {
		List<DecalVertex> verts = decalMaterial.Vertices;
		for (int i = 0; i < verts.Count; ++i) {
			DecalVertex vertex = verts[i];

			VertexCache.SetBodyModelMesh(vertex.Body, vertex.Model, vertex.Mesh);
			if (VertexCache.IsVertexFlexed(vertex.MeshVertexIndex)) {
				ref CachedPosNormTan flexedVertex = ref VertexCache.GetFlexVertex(vertex.MeshVertexIndex);
				meshBuilder.Position3fv(in flexedVertex.Position);
				meshBuilder.Normal3fv(in flexedVertex.Normal);
			}
			else {
				meshBuilder.Position3fv(in vertex.Position);
				meshBuilder.Normal3fv(in vertex.Normal);
			}

			meshBuilder.TexCoord2fv(0, in vertex.TexCoord);

			meshBuilder.Color4ub(255, 255, 255, 255);

			DecalBoneState(ref meshBuilder);

			meshBuilder.AdvanceVertex();
		}
	}

	bool DrawMultiBoneDecals(ref MeshBuilder meshBuilder, DecalMaterial decalMaterial, StudioHeader studioHdr) {
		MStudioMeshVertexData? vertData = null;
		MStudioMesh? lastMesh = null;

		List<DecalVertex> verts = decalMaterial.Vertices;
		for (int i = 0; i < verts.Count; ++i) {
			DecalVertex vertex = verts[i];

			int n = vertex.MeshVertexIndex;

			Assert(n < Studio.MAXSTUDIOVERTS);

			MStudioMesh? mesh = vertex.GetMesh(studioHdr);
			Assert(mesh != null);
#if GMOD_DLL
			if (vertex.MeshVertexIndex == 0xFFFF || mesh == null)
				return false;
#endif

			VertexCache.SetBodyModelMesh(vertex.Body, vertex.Model, vertex.Mesh);
			if (VertexCache.IsVertexPositionCached(n)) {
				ref CachedPosNorm cachedVert = ref VertexCache.GetWorldVertex(n);
				meshBuilder.Position3fv(cachedVert.Position.AsVector3());
				meshBuilder.Normal3fv(cachedVert.Normal.AsVector3());
			}
			else {
#if GMOD_DLL
				if (!VertexCache.SetupComputation(mesh!)) {
					if (s_MultiBoneDecalWarnings <= 9) {
						Warning($"Could not render decals for model '{studioHdr.GetName()}' (it has > 65536 vertices)\n");
						++s_MultiBoneDecalWarnings;
					}
					return false;
				}
#else
				VertexCache.SetupComputation(mesh!);
#endif
				ref CachedPosNorm cachedVert = ref VertexCache.CreateWorldVertex(n);

				if (lastMesh != mesh) {
					lastMesh = mesh;
					vertData = mesh!.GetVertexData(studioDataCache, studioHdr);
				}

				// todo: thin vertex data (GetThinVertexData)
				if (vertData == null)
					return false;

				ref MStudioBoneWeight boneWeights = ref vertData.BoneWeights(n);
				R_StudioTransform(in vertData.Position(n), in boneWeights, out Vector3 position);
				R_StudioRotate(in vertData.Normal(n), in boneWeights, out Vector3 normal);

				MathLib.VectorMA(position, 0.1f, normal, out position);

				cachedVert.Position = new(position, cachedVert.Position.W);
				cachedVert.Normal = new(normal, cachedVert.Normal.W);

				meshBuilder.Position3fv(in position);
				meshBuilder.Normal3fv(in normal);
			}

			meshBuilder.TexCoord2fv(0, in vertex.TexCoord);

			meshBuilder.Color4ub(255, 255, 255, 255);

			DecalBoneState(ref meshBuilder);

			meshBuilder.AdvanceVertex();
		}
		return true;
	}

	bool DrawMultiBoneFlexedDecals(IMatRenderContext renderContext, ref MeshBuilder meshBuilder, DecalMaterial decalMaterial, StudioHeader studioHdr, StudioLODData? studioLOD) {
		MStudioMesh? lastMesh = null;
		MStudioMeshVertexData? vertData = null;

		List<DecalVertex> verts = decalMaterial.Vertices;
		for (int i = 0; i < verts.Count; ++i) {
			DecalVertex vertex = verts[i];

			int n = vertex.MeshVertexIndex;

			MStudioMesh? mesh = vertex.GetMesh(studioHdr);
			Assert(mesh != null);
#if GMOD_DLL
			if (vertex.MeshVertexIndex == 0xFFFF || mesh == null)
				return false;
#endif

			if (lastMesh != mesh) {
				lastMesh = mesh;
				vertData = mesh!.GetVertexData(studioDataCache, studioHdr);
			}

			if (vertData == null)
				return false;

			// todo: hardware morphed decals (m_pHWMorphDecalBoneRemap, GetMorphAccumulatorTexCoord), it needs hardware morphing ported first
			ref MStudioBoneWeight boneWeights = ref vertData.BoneWeights(n);
			VertexCache.SetBodyModelMesh(vertex.Body, vertex.Model, vertex.Mesh);

			if (VertexCache.IsVertexPositionCached(n)) {
				ref CachedPosNorm cachedVert = ref VertexCache.GetWorldVertex(n);
				meshBuilder.Position3fv(cachedVert.Position.AsVector3());
				meshBuilder.Normal3fv(cachedVert.Normal.AsVector3());
			}
			else {
#if GMOD_DLL
				if (!VertexCache.SetupComputation(mesh!)) {
					if (s_MultiBoneFlexedDecalWarnings <= 9) {
						Warning($"Could not render decals for model '{studioHdr.GetName()}' (it has > 65536 vertices)\n");
						++s_MultiBoneFlexedDecalWarnings;
					}
					return false;
				}
#else
				VertexCache.SetupComputation(mesh!);
#endif
				ref CachedPosNorm cachedVert = ref VertexCache.CreateWorldVertex(n);

				Vector3 position, normal;
				if (VertexCache.IsThinVertexFlexed(n)) {
					ref CachedPosNorm flexedVertex = ref VertexCache.GetThinFlexVertex(n);
					Vector3 vecPosition = vertData.Position(n) + flexedVertex.Position.AsVector3();
					Vector3 vecNormal = vertData.Normal(n) + flexedVertex.Normal.AsVector3();
					R_StudioTransform(in vecPosition, in boneWeights, out position);
					R_StudioRotate(in vecNormal, in boneWeights, out normal);
					MathLib.VectorNormalize(ref normal);
				}
				else if (VertexCache.IsVertexFlexed(n)) {
					ref CachedPosNormTan flexedVertex = ref VertexCache.GetFlexVertex(n);
					R_StudioTransform(in flexedVertex.Position, in boneWeights, out position);
					R_StudioRotate(in flexedVertex.Normal, in boneWeights, out normal);
				}
				else {
					Assert(mesh != null);
					R_StudioTransform(in vertData.Position(n), in boneWeights, out position);
					R_StudioRotate(in vertData.Normal(n), in boneWeights, out normal);
				}

				MathLib.VectorMA(position, 0.1f, normal, out position);

				cachedVert.Position = new(position, cachedVert.Position.W);
				cachedVert.Normal = new(normal, cachedVert.Normal.W);

				meshBuilder.Position3fv(in position);
				meshBuilder.Normal3fv(in normal);
			}

			meshBuilder.Color4ub(255, 255, 255, 255);
			meshBuilder.TexCoord2fv(0, in vertex.TexCoord);
			meshBuilder.TexCoord3f(2, 0.0f, 0.0f, 0.0f);

			DecalBoneState(ref meshBuilder);

			meshBuilder.AdvanceVertex();
		}
		return true;
	}

	void DrawDecalMaterial(IMatRenderContext renderContext, DecalMaterial decalMaterial, StudioHeader studioHdr, StudioLODData? studioLOD) {
		int indexCount = decalMaterial.Indices.Count;
		if (indexCount == 0)
			return;

#if GMOD_DLL
		if (indexCount > 32767)
			return;

		if (decalMaterial.Decals.First == null)
			return;
#endif

		if (!pRC!.Config.EnableHWMorph)
			studioLOD = null;

		// todo: BindMorph(MATERIAL_MORPH_DECAL) when the LOD has a hardware morph decal bone remap

		// todo: m_pMaterialMRMWireframe when pRC.Config.WireframeDecals
		renderContext.Bind(decalMaterial.Material!);

		IMesh mesh = renderContext.GetDynamicMesh()!;

		int vertexCount = decalMaterial.Vertices.Count;

		MeshBuilder meshBuilder = new();
		meshBuilder.Begin(mesh, MaterialPrimitiveType.Triangles, vertexCount, indexCount);

		bool draw = true;
		if (StudioHdr!.NumBones <= 1) {
			if (StudioHdr.NumFlexDesc != 0)
				DrawSingleBoneFlexedDecals(renderContext, ref meshBuilder, decalMaterial);
			else
				DrawSingleBoneDecals(ref meshBuilder, decalMaterial);
		}
		else {
			if (StudioHdr.NumFlexDesc != 0) {
				if (!DrawMultiBoneFlexedDecals(renderContext, ref meshBuilder, decalMaterial, studioHdr, studioLOD))
					draw = false;
			}
			else {
				if (!DrawMultiBoneDecals(ref meshBuilder, decalMaterial, studioHdr))
					draw = false;
			}
		}

		int i = 0;
#if GMOD_DLL
		if (draw) {
#endif
			LinkedListNode<Decal>? decal = decalMaterial.Decals.First;
			int indicesRemaining = decal!.Value.IndexCount;
			int vertexOffset = 0;
			for (; i < indexCount; ++i) {
				meshBuilder.Index((ushort)(decalMaterial.Indices[i] + vertexOffset));
				meshBuilder.AdvanceIndex();
				if (--indicesRemaining <= 0) {
					vertexOffset += decal!.Value.VertexCount;
					decal = decal.Next;
					if (decal != null)
						indicesRemaining = decal.Value.IndexCount;
					else
						Assert(i + 1 == indexCount);
				}
			}
#if GMOD_DLL
		}
#endif

		meshBuilder.End();
#if GMOD_DLL
		if (!draw && i >= indexCount)
			mesh.MarkAsDrawn();
		else
			mesh.Draw();
#else
		if (draw)
			mesh.Draw();
		else
			mesh.MarkAsDrawn();
#endif
	}

	static readonly Vector3[] s_pWhite = [
		new(1.0f, 1.0f, 1.0f),
		new(1.0f, 1.0f, 1.0f),
		new(1.0f, 1.0f, 1.0f),
		new(1.0f, 1.0f, 1.0f),
		new(1.0f, 1.0f, 1.0f),
		new(1.0f, 1.0f, 1.0f)
	];

	bool PreDrawDecal(IMatRenderContext renderContext, ref DrawModelInfo drawInfo) {
		if (!drawInfo.StaticLighting)
			return false;

		ReadOnlySpan<Vector3> ambient;
		if (pRC!.Config.FullBright != 0) {
			ambient = s_pWhite;
			pRC.NumLocalLights = 0;
		}
		else {
			ambient = drawInfo.AmbientCube;
			pRC.NumLocalLights = StudioRenderContext.CopyLocalLightingState(MAXLOCALLIGHTS, pRC.LocalLights, drawInfo.NumLocalLights, drawInfo.LocalLightDescs);
		}

		for (int i = 0; i < 6; i++)
			pRC.LightBoxColors[i] = new(ambient[i], 1.0f);

		SetLightingRenderState();
		return true;
	}

	void DrawDecal(ref DrawModelInfo drawInfo, int lod, int body) {
		StudioDecalHandle_t handle = drawInfo.Decals;
		if (handle == STUDIORENDER_DECAL_INVALID)
			return;

		using MatRenderContextPtr renderContext = new(materialSystem);
		PreDrawDecal(renderContext.Context, ref drawInfo);

		DecalModelList list = DecalList[handle]!;
		StudioHdr = drawInfo.StudioHdr;

		StudioLODData? studioLOD = null;
		if (StudioHdr.NumBones <= 1) {
			renderContext.SetNumBoneWeights(StudioHdr.NumBones);
			renderContext.MatrixMode(MaterialMatrixMode.Model);
			renderContext.LoadMatrix(PoseToWorld[0]);
		}
		else {
			studioLOD = drawInfo.HardwareData.LODs![lod];
			// todo: skinning setup for hardware morphed decals (m_pHWMorphDecalBoneRemap)
			renderContext.SetNumBoneWeights(0);
			renderContext.MatrixMode(MaterialMatrixMode.Model);
			renderContext.LoadIdentity();
		}

		foreach (DecalMaterial decalMaterial in list.Lod[lod].Materials)
			DrawDecalMaterial(renderContext.Context, decalMaterial, StudioHdr, studioLOD);
	}

	internal void DrawStaticPropDecals(ref DrawModelInfo drawInfo, StudioRenderCtx rc, in Matrix3x4 modelToWorld) {
		StudioDecalHandle_t handle = drawInfo.Decals;
		if (handle == STUDIORENDER_DECAL_INVALID)
			return;

		pRC = rc;
		// note: GMod keeps the header of the last prop drawn, ours is cleared after each draw
		StudioHdr = drawInfo.StudioHdr;

		using MatRenderContextPtr renderContext = new(materialSystem);
		PreDrawDecal(renderContext.Context, ref drawInfo);

		renderContext.MatrixMode(MaterialMatrixMode.Model);
		renderContext.LoadMatrix(modelToWorld);

		DecalModelList list = DecalList[handle]!;
		foreach (DecalMaterial decalMaterial in list.Lod[drawInfo.Lod].Materials)
			DrawDecalMaterial(renderContext.Context, decalMaterial, drawInfo.StudioHdr, null);

		pRC = null;
	}
}
