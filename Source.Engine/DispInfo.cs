using CommunityToolkit.HighPerformance;

using Source.Common;
using Source.Common.Commands;
using Source.Common.Engine;
using Source.Common.Formats.BSP;
using Source.Common.MaterialSystem;
using Source.Common.Mathematics;

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Source.Engine;

public class DispInfo : DispUtilsHelper, IDispInfo
{
	public Vector3 BBoxMin;
	public Vector3 BBoxMax;

	public int NumIndices;
	public int IndexOffset;
	public GroupMesh? Mesh;
	public int VertOffset;
	public float BumpSTexCoordOffset;

	public readonly MeshReader MeshReader = new();

	public ushort[] Indices = [];
	public DispRenderVert[] Verts = [];

	public MaxDispVertsBitSet ActiveVerts;
	public MaxDispVertsBitSet AllowedVerts;

	int LMPageID;

	nint ParentSurfPointer;
	readonly WeakReference<WorldBrushData> brushData = new(null!);
	public ref BSPMSurface2 ParentSurfID => ref GetParent();

	public int PointStart;

	int LightmapAlphaStart;

	int Contents;

	bool Touched;

	int SurfProp;

	int Power;

	ushort[]? Tags;

	Vector3[] BaseSurfacePositions = new Vector3[4];
	Vector2[] BaseSurfaceTexCoords = new Vector2[4];

	public PowerInfo? PowerInfo;

	DispNeighbor[] EdgeNeighbors = new DispNeighbor[4];
	DispCornerNeighbors[] CornerNeighbors = new DispCornerNeighbors[4];

	public int LightmapSamplePositionStart;

	public int WalkIndexCount;
	public ushort[]? WalkIndices;

	public int BuildIndexCount;
	public ushort[]? BuildIndices;

	DispNodeInfo[]? NodeInfo;

	Vector3 ViewerSphereCenter;

	bool InUse;

	DispDecalHandle FirstDecal = DISP_DECAL_HANDLE_INVALID;
	DispShadowHandle FirstShadowDecal = DISP_SHADOW_HANDLE_INVALID;

	public ushort Index;

	ushort Tag;

	public DispArray? DispArray;

	static readonly MatSysInterface MatSys = Singleton<MatSysInterface>();

	static readonly PooledLinkedList<DispShadowDecal> s_DispShadowDecals = new();
	static readonly PooledLinkedList<DispShadowFragment> s_DispShadowFragments = new();
	static readonly PooledLinkedList<DispDecal> s_DispDecals = new();
	static readonly PooledLinkedList<DispDecalFragment> s_DispDecalFragments = new();

	static readonly DecalNodeSetupCache s_DecalNodeSetupCache = new();

	public void GetIntersectingSurfaces(GetIntersectingSurfaces_Struct pStruct) => throw new NotImplementedException();
	public void RenderWireframeInLightmapPage(int pageId) => throw new NotImplementedException();

	public void GetBoundingBox(out Vector3 bbMin, out Vector3 bbMax) {
		bbMin = BBoxMin;
		bbMax = BBoxMax;
	}

	internal void SetParent(ref BSPMSurface2 surfID, WorldBrushData brushData) {
		this.brushData.SetTarget(brushData);
		ParentSurfPointer = surfID.SurfNum;
	}

	public ref BSPMSurface2 GetParent() {
		if (!brushData.TryGetTarget(out WorldBrushData? brush))
			throw new NullReferenceException("The world brush data has gone out of GC scope, and the pointer to a BSPMSurface2 cannot be retrieved.");
		return ref brush.Surfaces2![ParentSurfPointer];
	}

	public void AddDynamicLights(DLight[] lights, uint lightMask) => throw new NotImplementedException();
	public uint ComputeDynamicLightMask(DLight[] lights) => throw new NotImplementedException();

	public DispDecalHandle NotifyAddDecal(WorldDecalHandle_t decal, float size) {
		DispDecalHandle h = unchecked((DispDecalHandle)s_DispDecals.Alloc());
		if (h != DISP_DECAL_HANDLE_INVALID) {
			int decalCount = 0;
			DispDecalHandle iDecal = FirstDecal;
			DispDecalHandle lastDecal = DISP_DECAL_HANDLE_INVALID;
			while (iDecal != DISP_DECAL_HANDLE_INVALID) {
				lastDecal = iDecal;
				iDecal = unchecked((DispDecalHandle)s_DispDecals.Next(iDecal));
				++decalCount;
			}

#if !SWDS
			if (decalCount >= MAX_DISP_DECALS)
				Engine.Render.DecalUnlink(s_DispDecals[lastDecal].Decal, host_state.WorldBrush);
#endif

			if (FirstDecal != DISP_DECAL_HANDLE_INVALID)
				s_DispDecals.LinkBefore(FirstDecal, h);
			FirstDecal = h;

			ref DispDecal dispDecal = ref s_DispDecals[h];
			dispDecal.Decal = Engine.Render.s_DecalPool[decal];
			dispDecal.FirstFragment = DISP_DECAL_FRAGMENT_HANDLE_INVALID;
			dispDecal.Base.NVerts = 0;
			dispDecal.Base.NTris = 0;
			dispDecal.Size = size;

			Span<DecalVert> outVerts = default;
			Engine.Render.SetupDecalClip(outVerts, dispDecal.Decal!, ref ModelLoader.MSurf_Plane(ref ParentSurfID).Normal, dispDecal.Decal!.Material!, dispDecal.TextureSpaceBasis, dispDecal.DecalWorldScale);

			SetupDecalNodeIntersect(PowerInfo!.RootNode, 0, ref dispDecal.Base, ref dispDecal, ref Unsafe.NullRef<ShadowInfo_t>());
		}

		return h;
	}

	public void NotifyRemoveDecal(DispDecalHandle h) {
		ClearDecalFragments(h);

		if (FirstDecal == h)
			FirstDecal = unchecked((DispDecalHandle)s_DispDecals.Next(h));

		s_DispDecals.Remove(h);
	}

	public DispShadowHandle AddShadowDecal(ShadowHandle_t shadowHandle) {
		DispShadowHandle h = unchecked((DispShadowHandle)s_DispShadowDecals.Alloc());
		if (FirstShadowDecal != DISP_SHADOW_HANDLE_INVALID)
			s_DispShadowDecals.LinkBefore(FirstShadowDecal, h);
		FirstShadowDecal = h;

		ref DispShadowDecal shadowDecal = ref s_DispShadowDecals[h];
		shadowDecal.Base.Flags = DecalFlags.DecalShadow;
		shadowDecal.Base.NTris = 0;
		shadowDecal.Base.NVerts = 0;
		shadowDecal.Shadow = shadowHandle;
		shadowDecal.FirstFragment = DISP_SHADOW_FRAGMENT_HANDLE_INVALID;

		return h;
	}
	public void RemoveShadowDecal(DispShadowHandle handle) {
		ClearShadowDecalFragments(handle);

		if (FirstShadowDecal == handle)
			FirstShadowDecal = unchecked((DispShadowHandle)s_DispShadowDecals.Next(handle));

		s_DispShadowDecals.Remove(handle);
	}

	public bool ComputeShadowFragments(DispShadowHandle h, out int vertexCount, out int indexCount) {
		ref DispShadowDecal shadowDecal = ref s_DispShadowDecals[h];

		if ((shadowDecal.Base.Flags & DecalFlags.FragmentsComputed) != 0) {
			vertexCount = shadowDecal.Base.NVerts;
			indexCount = 3 * shadowDecal.Base.NTris;
			return true;
		}

#if !SWDS
		if ((shadowDecal.Base.Flags & DecalFlags.NodeBitfieldComputed) == 0) {
			ref readonly ShadowInfo_t info = ref g_ShadowMgr.GetInfo(shadowDecal.Shadow);
			SetupDecalNodeIntersect(PowerInfo!.RootNode, 0, ref shadowDecal.Base, ref Unsafe.NullRef<DispDecal>(), in info);
		}
#endif

		if ((shadowDecal.Base.Flags & DecalFlags.NoIntersection) != 0) {
			vertexCount = 0;
			indexCount = 0;
			return false;
		}

		Assert(shadowDecal.Base.NTris == 0);
		Assert(shadowDecal.FirstFragment == DISP_SHADOW_FRAGMENT_HANDLE_INVALID);

		GenerateDecalFragments(PowerInfo!.RootNode, 0, h, ref shadowDecal.Base);

		vertexCount = shadowDecal.Base.NVerts;
		indexCount = 3 * shadowDecal.Base.NTris;

		return true;
	}

	public bool GetTag() => Tag == DispArray!.CurTag;
	public void SetTag() => Tag = (ushort)DispArray!.CurTag;

	public DispInfo? GetDispByIndex(int index) => index == 0xFFFF ? null : DispArray!.DispInfos[index];

	public nint GetDispIndex() => throw new NotImplementedException();

	public void SetTouched(bool touched) => Touched = touched;
	public bool IsTouched() => Touched;

	public void ClearLOD() => throw new NotImplementedException();

	public void DrawDispAxes() => throw new NotImplementedException();
	public bool Render(GroupMesh group, bool allowDebugModes) {
#if !SWDS
		if (Mesh == null) {
			AssertMsg(false, "CDispInfo::Render: Mesh == null");
			return false;
		}

		if (MathLib.R_CullBox(BBoxMin, BBoxMax, g_Frustum))
			return false;

		bool normalRender = true;
		if (allowDebugModes) {
			using MatRenderContextPtr renderContext = new(materials);

			if (ShouldDrawInWireFrameMode()) {
				renderContext.Bind(MatSys.MaterialWireframe!, null);
				SpecifyDynamicMesh();
				normalRender = false;
			}

			// todo: mat_luxels, r_DispWalkable/r_DispBuildable, mat_surfaceid, mat_surfacemat
		}

		if (normalRender) {
			if (group.NumVisible < group.Visible.Count) {
				if (NumIndices != 0) {
					group.Visible[group.NumVisible] = new PrimList(IndexOffset, NumIndices);
					group.VisibleDisps[group.NumVisible] = this;
					group.NumVisible++;
					group.Group!.Visible++;
				}
			}
			else
				AssertMsg(false, "Overflowed visible mesh list");
		}
#endif

		return true;
	}

	// public void AddSingleDynamicLight(ref DLight dl) => throw new NotImplementedException();
	// public void AddSingleDynamicLightBumped(ref DLight dl) => throw new NotImplementedException();

	// public void AddSingleDynamicAlphaLight(ref DLight dl) => throw new NotImplementedException();

	public bool TestRay(in Ray ray, float start, float end, ref float dist, Vector2[]? lightmapUV, Vector2[]? textureUV) => throw new NotImplementedException();

	public override PowerInfo GetPowerInfo() => PowerInfo!;
	public override DispNeighbor GetEdgeNeighbor(int index) => throw new NotImplementedException();
	public override DispCornerNeighbors GetCornerNeighbors(int index) => throw new NotImplementedException();
	public override DispUtilsHelper GetDispUtilsByIndex(int index) => GetDispByIndex(index)!;

	public int VertIndex(int x, int y) {
		Assert(x >= 0 && x < GetSideLength() && y >= 0 && y < GetSideLength());
		return y * GetSideLength() + x;
	}
	public int VertIndex(in VertIndex vert) {
		Assert(vert.X >= 0 && vert.X < GetSideLength() && vert.Y >= 0 && vert.Y < GetSideLength());
		return vert.Y * GetSideLength() + vert.X;
	}
	public VertIndex IndexToVert(int index) {
		if (index == -1)
			return new VertIndex(-1, -1);
		else
			return new VertIndex((short)(index % GetSideLength()), (short)(index / GetSideLength()));
	}

	public void CopyMapDispData(in BSPDDispInfo buildDisp) {
		LightmapAlphaStart = buildDisp.LightmapAlphaStart;
		Power = buildDisp.Power;

		Assert(Power >= 2 && Power <= BSPFileCommon.MAX_MAP_DISP_POWER + 1);
		PowerInfo = PowerInfo.GetPowerInfo(Power);

		int size = GetSideLength();
		Array.Resize(ref Indices, 6 * (size - 1) * (size - 1));

		NodeInfo = new DispNodeInfo[PowerInfo.NodeCount];
	}

	public bool CopyCoreDispData(Model world, MaterialSystem_SortInfo[] sortInfos, CoreDispInfo coreDisp, bool restoring) {
		LMPageID = sortInfos[ModelLoader.MSurf_MaterialSortID(ref GetParent())].LightmapPageID;

#if !SWDS
		MatSysInterface.SurfaceCtx ctx = default;
		MatSys.SurfSetupSurfaceContext(ref ctx, ref GetParent());
#endif

		if (restoring) {
#if !SWDS
			if (NumLightmaps() > 1)
				BumpSTexCoordOffset = ctx.BumpSTexCoordOffset;
			else
				BumpSTexCoordOffset = 0.0f;

			for (int i = 0; i < NumVerts(); i++)
				coreDisp.GetLuxelCoord(0, i, out GetVertex(i).LMCoords);
#endif
			return true;
		}

		CoreDispSurface surface = coreDisp.GetSurface();
		for (int index = 0; index < 4; index++) {
			surface.GetTexCoord(index, out BaseSurfaceTexCoords[index]);
			BaseSurfacePositions[index] = surface.GetPoint(index);
		}

#if !SWDS
		CopyCoreDispVertData(coreDisp, ctx.BumpSTexCoordOffset);
#endif

		for (int iEdge = 0; iEdge < 4; iEdge++) {
			EdgeNeighbors[iEdge] = coreDisp.GetEdgeNeighbor(iEdge);
			CornerNeighbors[iEdge] = coreDisp.GetCornerNeighbors(iEdge);
		}

		AllowedVerts = coreDisp.GetAllowedVerts();

		NumIndices = 0;
		return true;
	}

	internal void CopyCoreDispVertData(CoreDispInfo coreDisp, float bumpSTexCoordOffset) {
#if !SWDS
		if (NumLightmaps() <= 1)
			bumpSTexCoordOffset = 0.0f;

		Array.Resize(ref Verts, PowerInfo!.MaxVerts);
		BumpSTexCoordOffset = bumpSTexCoordOffset;
		for (int i = 0; i < NumVerts(); i++) {
			ref DispRenderVert vert = ref GetVertex(i);

			coreDisp.GetVert(i, out vert.Pos);

			coreDisp.GetTexCoord(i, out vert.TexCoord);
			coreDisp.GetLuxelCoord(0, i, out vert.LMCoords);

			coreDisp.GetNormal(i, out vert.Normal);
			coreDisp.GetTangentS(i, out vert.SVector);
			coreDisp.GetTangentT(i, out vert.TVector);
		}
#endif
	}

	public int NumLightmaps() => (ModelLoader.MSurf_Flags(ref ParentSurfID) & SurfDraw.BumpLight) != 0 ? Constants.NUM_BUMP_VECTS + 1 : 1;

	public Vector3 GetFlatVert(int vertex) => throw new NotImplementedException();

	internal void UpdateBoundingBox() {
		BBoxMin.Init(1e24f, 1e24f, 1e24f);
		BBoxMax.Init(-1e24f, -1e24f, -1e24f);

		for (int i = 0; i < NumVerts(); i++) {
			ref Vector3 pos = ref Verts[i].Pos;
			MathLib.VectorMin(pos, BBoxMin, out BBoxMin);
			MathLib.VectorMax(pos, BBoxMax, out BBoxMax);
		}
	}

	public new int GetSideLength() => PowerInfo!.GetSideLength();

	public int NumVerts() => PowerInfo!.GetNumVerts();

	public void DecalProjectVert(in Vector3 pos, ref DispDecal dispDecal, ref readonly ShadowInfo_t info, out Vector3 outVec) {
		if (Unsafe.IsNullRef(in info)) {
			outVec.X = Vector3.Dot(pos, dispDecal.TextureSpaceBasis[0]) - dispDecal.Decal!.Dx + .5f;
			outVec.Y = Vector3.Dot(pos, dispDecal.TextureSpaceBasis[1]) - dispDecal.Decal.Dy + .5f;
			outVec.Z = 0;
		}
		else
			MathLib.Vector3DMultiplyPosition(in info.WorldToShadow, in pos, out outVec);
	}

	// note: dead code in GMod
	public void CullDecals(int nodeBit, ReadOnlySpan<DispDecalHandle> decals, int nDecals, Span<DispDecalHandle> childDecals, out int nChildDecals) {
		nChildDecals = 0;
		for (int iDecal = 0; iDecal < nDecals; iDecal++) {
			if (s_DispDecals[decals[iDecal]].Base.NodeIntersect.IsBitSet(nodeBit)) {
				childDecals[nChildDecals] = decals[iDecal];
				++nChildDecals;
			}
		}
	}

	internal ref DispNodeInfo GetNodeInfoRef(int nodeBit) => ref NodeInfo![nodeBit];

	internal void TesselateDisplacement() {
		ClearAllDecalFragments();

		ClearAllShadowDecalFragments();

		int maxIndices = MathLib.Square(GetSideLength() - 1) * 6;

		EngineTesselateHelper helper = new();
		helper.Disp = this;
		helper.IndexMesh.BeginModify(Mesh!.Mesh!, 0, 0, IndexOffset, maxIndices);
		helper.ActiveVerts = ActiveVerts;
		helper.PowerInfo = GetPowerInfo();

		DispTesselate.TesselateDisplacement(helper);

		helper.IndexMesh.EndModify();
		NumIndices = helper.NIndices;
	}

	public void SpecifyDynamicMesh() {
		using MatRenderContextPtr renderContext = new(materials);

		IMesh mesh = renderContext.GetDynamicMesh(true);
		MeshBuilder builder = new();
		builder.Begin(mesh, MaterialPrimitiveType.Triangles, NumVerts(), NumIndices);

		int verts = NumVerts();
		for (int iVert = 0; iVert < verts; iVert++) {
			ref DispRenderVert vert = ref GetVertex(iVert);

			builder.Position3fv(vert.Pos);

			builder.TexCoord2fv(0, vert.TexCoord);
			builder.TexCoord2fv(1, vert.LMCoords);
			builder.TexCoord2f(2, BumpSTexCoordOffset, 0);

			builder.Normal3fv(vert.Normal);
			builder.TangentS3fv(vert.SVector);
			builder.TangentT3fv(vert.TVector);

			builder.AdvanceVertex();
		}

		for (int index = 0; index < NumIndices; index++) {
			builder.Index((ushort)(Indices[index] - VertOffset));
			builder.AdvanceIndex();
		}

		builder.End(false, true);
	}
	public void SpecifyWalkableDynamicMesh() => throw new NotImplementedException();
	public void SpecifyBuildableDynamicMesh() => throw new NotImplementedException();

	public void InitializeActiveVerts() {
		ActiveVerts.ClearAll();

		ActiveVerts.Set(VertIndex(0, 0));
		ActiveVerts.Set(VertIndex(GetSideLength() - 1, 0));
		ActiveVerts.Set(VertIndex(GetSideLength() - 1, GetSideLength() - 1));
		ActiveVerts.Set(VertIndex(0, GetSideLength() - 1));
		ActiveVerts.Set(VertIndex(PowerInfo!.RootNode));

		for (int side = 0; side < 4; side++) {
			ref DispNeighbor pSide = ref EdgeNeighbors[side];

			if ((pSide.SubNeighbors[0].IsValid() && pSide.SubNeighbors[0].GetSpan() != NeighborSpan.CORNER_TO_CORNER) ||
				(pSide.SubNeighbors[1].IsValid() && pSide.SubNeighbors[1].GetSpan() != NeighborSpan.CORNER_TO_CORNER)) {
				int edgeDim = g_EdgeDims[side];

				VertIndex nodeIndex = new();
				nodeIndex[edgeDim] = (short)(g_EdgeSideLenMul[side] * PowerInfo.SideLengthM1);
				nodeIndex[edgeDim ^ 1] = (short)PowerInfo.MidPoint;
				ActiveVerts.Set(VertIndex(nodeIndex));
			}
		}
	}

	public ref DispRenderVert GetVertex(int i) {
		Assert(i < NumVerts());
		return ref Verts[i];
	}

	// public void ComputeLightmapAndTextureCoordinate(in RayDispOutput output, Vector2[]? luv, Vector2[]? tuv) => throw new NotImplementedException();

	public void GenerateDecalFragments(in VertIndex nodeIndex, int nodeBitIndex, ushort decalHandle, ref DispDecalBase dispDecal) {
		GenerateDecalFragments_R(nodeIndex, nodeBitIndex, decalHandle, ref dispDecal, 0);
		dispDecal.Flags |= DecalFlags.FragmentsComputed;
	}

	void TestAddDecalTri(int indexStart, ushort decalHandle, ref DispDecal dispDecal) {
		Decal decal = dispDecal.Decal!;

		Span<ushort> tempIndices = [
			(ushort)(Indices[indexStart + 0] - VertOffset),
			(ushort)(Indices[indexStart + 1] - VertOffset),
			(ushort)(Indices[indexStart + 2] - VertOffset)
		];

		ref Vector3 v0 = ref Verts[tempIndices[0]].Pos;
		ref Vector3 v1 = ref Verts[tempIndices[1]].Pos;
		ref Vector3 v2 = ref Verts[tempIndices[2]].Pos;

		Vector3 normal = Vector3.Cross(v2 - v0, v1 - v0);
		MathLib.VectorNormalize(ref normal);
		if (Vector3.Dot(normal, decal.Position - v0) >= dispDecal.Size)
			return;

		Span<DecalVert> verts = stackalloc DecalVert[3];
		int iVert;
		for (iVert = 0; iVert < 3; iVert++) {
			ref DecalVert outVert = ref verts[iVert];

			outVert.Pos = Verts[tempIndices[iVert]].Pos;
			outVert.CLMCoords = Verts[tempIndices[iVert]].LMCoords;

			DecalProjectVert(outVert.Pos, ref dispDecal, ref Unsafe.NullRef<ShadowInfo_t>(), out Vector3 tmp);
			outVert.CtCoords.X = tmp.X;
			outVert.CtCoords.Y = tmp.Y;
		}

		Span<DecalVert> clipped = Engine.Render.DoDecalSHClip(verts, default, decal, 3, vec3_origin);
		int outCount = decal.ClippedVertCount;

		if (outCount > 2) {
			outCount = Math.Min(outCount, DispDecalFragment.MAX_VERTS);

			ref DispDecalFragment fragment = ref AllocateDispDecalFragment(unchecked((DispDecalHandle)decalHandle), outCount);

			for (iVert = 0; iVert < outCount; iVert++) {
				fragment.Verts![iVert].Pos = clipped[iVert].Pos;
				fragment.Verts[iVert].CtCoords = clipped[iVert].CtCoords;
				fragment.Verts[iVert].CLMCoords = clipped[iVert].CLMCoords;
			}

			fragment.Decal = decal;
			fragment.NVerts = (byte)outCount;
			dispDecal.Base.NVerts += fragment.NVerts;
			dispDecal.Base.NTris += (ushort)(fragment.NVerts - 2);
		}
	}

	void TestAddDecalTri(int indexStart, ushort decalHandle, ref DispShadowDecal decal) {
		Span<ushort> tempIndices = [
			(ushort)(Indices[indexStart + 0] - VertOffset),
			(ushort)(Indices[indexStart + 1] - VertOffset),
			(ushort)(Indices[indexStart + 2] - VertOffset)
		];
#if !SWDS
		Span<Vector3> positions = [
			GetOverlayPos(tempIndices[0]),
			GetOverlayPos(tempIndices[1]),
			GetOverlayPos(tempIndices[2])
		];

		int count = g_ShadowMgr.ProjectAndClipVertices(decal.Shadow, positions, out ShadowVertex[]? clipVertex);
		if (count < 3)
			return;

		Assert(count <= DispShadowFragment.MAX_VERTS);

		ref DispShadowFragment fragment = ref AllocateShadowDecalFragment(decalHandle, count);

		fragment.NVerts = count;

		for (int i = 0; i < count; ++i) {
			fragment.ShadowVerts![i].Position = clipVertex![i].Position;
			fragment.ShadowVerts[i].ShadowSpaceTexCoord = clipVertex[i].ShadowSpaceTexCoord;

			Assert(fragment.ShadowVerts[i].ShadowSpaceTexCoord[0] >= -1e-3f);
			Assert(fragment.ShadowVerts[i].ShadowSpaceTexCoord[0] - 1.0f <= 1e-3f);
			Assert(fragment.ShadowVerts[i].ShadowSpaceTexCoord[1] >= -1e-3f);
			Assert(fragment.ShadowVerts[i].ShadowSpaceTexCoord[1] - 1.0f <= 1e-3f);
		}

		decal.Base.NVerts += (ushort)fragment.NVerts;
		decal.Base.NTris += (ushort)(fragment.NVerts - 2);
		Assert(decal.Base.NTris != 0);
#endif
	}

	Vector3 GetOverlayPos(int vertIndex) => Verts[vertIndex].Pos + Verts[vertIndex].Normal * OVERLAY_AVOID_FLICKER_NORMAL_OFFSET;

	ref DispDecalFragment AllocateDispDecalFragment(DispDecalHandle h, int nVerts = 6) {
		DispDecalFragmentHandle f = unchecked((DispDecalFragmentHandle)s_DispDecalFragments.Alloc());
		if (s_DispDecals[h].FirstFragment != DISP_DECAL_FRAGMENT_HANDLE_INVALID)
			s_DispDecalFragments.LinkBefore(s_DispDecals[h].FirstFragment, f);
		s_DispDecals[h].FirstFragment = f;
		ref DispDecalFragment pf = ref s_DispDecalFragments[f];

		pf.NVerts = (byte)nVerts;
		pf.Verts = new DecalVert[nVerts];

		return ref pf;
	}

	void ClearDecalFragments(DispDecalHandle h) {
		ref DispDecal decal = ref s_DispDecals[h];
		DispDecalFragmentHandle f = decal.FirstFragment;
		DispDecalFragmentHandle next;

		while (f != DISP_DECAL_FRAGMENT_HANDLE_INVALID) {
			next = unchecked((DispDecalFragmentHandle)s_DispDecalFragments.Next(f));
			s_DispDecalFragments.Remove(f);
			f = next;
		}

		decal.FirstFragment = DISP_DECAL_FRAGMENT_HANDLE_INVALID;

		decal.Base.Flags &= ~DecalFlags.FragmentsComputed;

		decal.Base.NTris = 0;
		decal.Base.NVerts = 0;
	}

	void ClearAllDecalFragments() {
		DispDecalHandle h = FirstDecal;
		while (h != DISP_DECAL_HANDLE_INVALID) {
			ClearDecalFragments(h);
			h = unchecked((DispDecalHandle)s_DispDecals.Next(h));
		}
	}

	ref DispShadowFragment AllocateShadowDecalFragment(DispShadowHandle h, int count) {
		DispShadowFragmentHandle f = unchecked((DispShadowFragmentHandle)s_DispShadowFragments.Alloc());
		if (s_DispShadowDecals[h].FirstFragment != DISP_SHADOW_FRAGMENT_HANDLE_INVALID)
			s_DispShadowFragments.LinkBefore(s_DispShadowDecals[h].FirstFragment, f);
		s_DispShadowDecals[h].FirstFragment = f;
		ref DispShadowFragment pf = ref s_DispShadowFragments[f];
		pf.NVerts = count;
		pf.ShadowVerts = new ShadowVertex[count];
		return ref pf;
	}

	void ClearShadowDecalFragments(DispShadowHandle h) {
		ref DispShadowDecal decal = ref s_DispShadowDecals[h];
		DispShadowFragmentHandle f = decal.FirstFragment;
		DispShadowFragmentHandle next;
		while (f != DISP_SHADOW_FRAGMENT_HANDLE_INVALID) {
			next = unchecked((DispShadowFragmentHandle)s_DispShadowFragments.Next(f));
			s_DispShadowFragments.Remove(f);
			f = next;
		}

		decal.FirstFragment = DISP_SHADOW_FRAGMENT_HANDLE_INVALID;

		decal.Base.Flags &= ~DecalFlags.FragmentsComputed;

		decal.Base.NTris = 0;
		decal.Base.NVerts = 0;
	}

	void ClearAllShadowDecalFragments() {
		DispShadowHandle h = FirstShadowDecal;
		while (h != DISP_SHADOW_HANDLE_INVALID) {
			ClearShadowDecalFragments(h);
			h = unchecked((DispShadowHandle)s_DispShadowDecals.Next(h));
		}
	}

	void GenerateDecalFragments_R(in VertIndex nodeIndex, int nodeBitIndex, ushort decalHandle, ref DispDecalBase dispDecal, int level) {
		Assert(nodeBitIndex < PowerInfo!.NodeCount);
		ref DispNodeInfo nodeInfo = ref NodeInfo![nodeBitIndex];

		int nodeIndexInt = VertIndex(nodeIndex);

		if (!dispDecal.NodeIntersect.IsBitSet(nodeBitIndex))
			return;

		if ((level + 1 < Power) && (nodeInfo.Flags & DispNodeInfo.CHILDREN_HAVE_TRIANGLES) != 0) {
			int childNodeBit = nodeBitIndex + 1;
			for (int child = 0; child < 4; child++) {
				ref VertIndex childNode = ref PowerInfo.ChildVerts[nodeIndexInt].Verts[child];

				bool activeChild = ActiveVerts.Get(VertIndex(childNode)) != 0;
				if (activeChild)
					GenerateDecalFragments_R(childNode, childNodeBit, decalHandle, ref dispDecal, level + 1);
				childNodeBit += PowerInfo.NodeIndexIncrements[level];
			}
		}

		bool isShadow = (dispDecal.Flags & DecalFlags.DecalShadow) != 0;

		int index = nodeInfo.FirstTesselationIndex;
		for (int i = 0; i < nodeInfo.Count; i += 3) {
			if (isShadow)
				TestAddDecalTri(index + i, decalHandle, ref s_DispShadowDecals[decalHandle]);
			else
				TestAddDecalTri(index + i, decalHandle, ref s_DispDecals[unchecked((DispDecalHandle)decalHandle)]);
		}
	}

	void SetupDecalNodeIntersect(in VertIndex nodeIndex, int nodeBitIndex, ref DispDecalBase decalBase, ref DispDecal dispDecal, ref readonly ShadowInfo_t info) {
		decalBase.NodeIntersect.ClearAll();

		DecalNodeSetupCache cache = s_DecalNodeSetupCache;
		cache.ResetCache();

		bool anyIntersection = SetupDecalNodeIntersect_R(nodeIndex, nodeBitIndex, ref decalBase, ref dispDecal, in info, 0, cache);

		decalBase.Flags |= DecalFlags.NodeBitfieldComputed;
		if (anyIntersection)
			decalBase.Flags &= ~DecalFlags.NoIntersection;
		else
			decalBase.Flags |= DecalFlags.NoIntersection;
	}

	bool SetupDecalNodeIntersect_R(in VertIndex nodeIndex, int nodeBitIndex, ref DispDecalBase decalBase, ref DispDecal dispDecal, ref readonly ShadowInfo_t info, int level, DecalNodeSetupCache cache) {
		int nodeIndexInt = VertIndex(nodeIndex);

		if (level + 1 < Power) {
			bool anyChildIntersected = false;
			int childNodeBit = nodeBitIndex + 1;
			for (int child = 0; child < 4; child++) {
				ref VertIndex childNode = ref PowerInfo!.ChildVerts[nodeIndexInt].Verts[child];

				if (SetupDecalNodeIntersect_R(childNode, childNodeBit, ref decalBase, ref dispDecal, in info, level + 1, cache))
					anyChildIntersected = true;
				childNodeBit += PowerInfo.NodeIndexIncrements[level];
			}

			if (anyChildIntersected) {
				decalBase.NodeIntersect.Set(nodeBitIndex);
				return true;
			}

			return false;
		}

		Vector3 min, max;
		if (!cache.IsCached(nodeIndexInt)) {
			DecalProjectVert(Verts[nodeIndexInt].Pos, ref dispDecal, in info, out cache.ProjectedVert[nodeIndexInt]);
			cache.MarkCached(nodeIndexInt);
		}
		min = cache.ProjectedVert[nodeIndexInt];
		max = cache.ProjectedVert[nodeIndexInt];

		for (int i = 0; i < 4; i++) {
			ref VertIndex sideVert = ref PowerInfo!.SideVerts[nodeIndexInt].Verts[i];
			ref VertIndex cornerVert = ref PowerInfo.SideVertCorners[nodeIndexInt].Verts[i];

			int sideIndex = VertIndex(sideVert);
			if (!cache.IsCached(sideIndex)) {
				DecalProjectVert(Verts[sideIndex].Pos, ref dispDecal, in info, out cache.ProjectedVert[sideIndex]);
				cache.MarkCached(sideIndex);
			}

			MathLib.VectorMin(cache.ProjectedVert[sideIndex], min, out min);
			MathLib.VectorMax(cache.ProjectedVert[sideIndex], max, out max);

			int cornerIndex = VertIndex(cornerVert);
			if (!cache.IsCached(cornerIndex)) {
				DecalProjectVert(Verts[cornerIndex].Pos, ref dispDecal, in info, out cache.ProjectedVert[cornerIndex]);
				cache.MarkCached(cornerIndex);
			}

			MathLib.VectorMin(cache.ProjectedVert[cornerIndex], min, out min);
			MathLib.VectorMax(cache.ProjectedVert[cornerIndex], max, out max);
		}

		if (min.X <= 1 && max.X >= 0 && min.Y <= 1 && max.Y >= 0) {
			if (!Unsafe.IsNullRef(in info)) {
				if ((max.Z < 0) || (min.Z > info.MaxDist))
					return false;
			}

			decalBase.NodeIntersect.Set(nodeBitIndex);
			return true;
		}

		return false;
	}

	public static DispInfo? GetModelDisp(Model world, int i) {
		return DispInfo_IndexArray(world.Brush.Shared!.DispInfos, i);
	}

	public static readonly ConVar r_DrawDisp = new("r_DrawDisp", "1", FCvar.Cheat, "Toggles rendering of displacment maps");
	static InlineArrayMaxMapDispInfo<DispInfo> s_visibleDisps;
	public static void DispInfo_RenderList(int sortGroup, Span<SurfaceHandle_t> list, int listCount, bool ortho, uint flags, RenderDepthMode depthMode) {
		if (r_DrawDisp.GetInt() == 0 || listCount == 0)
			return;

		Span<DispInfo?> visibleDisps = s_visibleDisps;

		DispInfo_BuildPrimLists(sortGroup, list, listCount, depthMode != RenderDepthMode.Normal, visibleDisps, out int visibleDispCount);

		DispInfo_DrawPrimLists(depthMode);

		if (depthMode != RenderDepthMode.Normal)
			return;

		for (int i = 0; i < listCount; i++) {
			SurfaceHandle_t cur = list[i];
			ref BSPMSurface2 surf = ref ModelLoader.SurfaceHandleFromIndex(cur);
			ShadowDecalHandle_t decalHandle = ModelLoader.MSurf_ShadowDecals(ref surf);
			if (decalHandle != SHADOW_DECAL_HANDLE_INVALID) {
				g_ShadowMgr.AddShadowsOnSurfaceToRenderList(decalHandle);
			}
		}

		bool flashlightMask = !(((DrawWorldListFlags)flags & DrawWorldListFlags.Refraction) != 0 || ((DrawWorldListFlags)flags & DrawWorldListFlags.Reflection) != 0);

		// todo

		g_ShadowMgr.RenderFlashlights(flashlightMask);
		// OverlayMgr().RenderOverlays(sortGroup)
		g_ShadowMgr.DrawFlashlightOverlays(sortGroup, flashlightMask);
		// OverlayMgr().ClearRenderLists(sortGroup)

		DispInfo_BatchDecals(visibleDisps, visibleDispCount);
		DispInfo_DrawDecals(visibleDisps, visibleDispCount);

		g_ShadowMgr.DrawFlashlightDecalsOnDisplacements(sortGroup, visibleDisps, visibleDispCount, flashlightMask);
		g_ShadowMgr.RenderShadows();
		g_ShadowMgr.ClearShadowRenderList();

		DispInfo_DrawDebugInformation(list, listCount);
	}

	static bool DispInfoRenderDebugModes() {
		if (ShouldDrawInWireFrameMode() || mat_luxels.GetInt() != 0
			// || r_DispWalkable.GetInt() || r_DispBuildable.GetInt() || mat_surfaceid.GetInt() || mat_surfacemat.GetInt() // todo
			)
			return true;

		return false;
	}

	static void DispInfo_BuildPrimLists(int sortGroup, Span<SurfaceHandle_t> list, int listCount, bool depthOnly, Span<DispInfo?> visibleDisps, out int visibleDispCount) {
		visibleDispCount = 0;
		bool debugConvars = !depthOnly ? DispInfoRenderDebugModes() : false;
		for (int i = 0; i < listCount; i++) {
			DispInfo disp = (DispInfo)ModelLoader.SurfaceHandleFromIndex(list[i]).DispInfo!;
			if (!disp.Render(disp.Mesh!, debugConvars))
				continue;

			if (visibleDispCount < BSPFileCommon.MAX_MAP_DISPINFO)
				visibleDisps[visibleDispCount++] = disp;

			if (depthOnly)
				continue;
#if !SWDS
			// OverlayMgr().AddFragmentListToRenderList(sortGroup, MSurf_OverlayFragmentList(list[i]), true) // todo
#endif
		}
	}

	static readonly ConVar disp_dynamic = new("disp_dynamic", "0", 0);

	static void DispInfo_DrawPrimLists(RenderDepthMode depthMode) {
		int dispGroupsSize = g_DispGroups.Count;

		int fullbright = MatSysInterface.MaterialSystemConfig.Fullbright;

		using MatRenderContextPtr renderContext = new(materials);

		for (int iGroup = 0; iGroup < dispGroupsSize; iGroup++) {
			DispGroup group = g_DispGroups[iGroup];
			if (group.Visible == 0)
				continue;

			if (depthMode != RenderDepthMode.Normal) {
				// todo!!!!!!
			}
			else
				renderContext.Bind(group.Material!, null);

			if (fullbright != 1 && depthMode == RenderDepthMode.Normal)
				renderContext.BindLightmapPage(group.LightmapPageID);
			else {
				if (group.Material!.GetPropertyFlag(MaterialPropertyTypes.NeedsBumpedLightmaps))
					renderContext.BindLightmapPage(StandardLightmap.WhiteBump);
				else
					renderContext.BindLightmapPage(StandardLightmap.White);
			}

			int meshesSize = group.Meshes.Count;

			for (int iMesh = 0; iMesh < meshesSize; iMesh++) {
				GroupMesh mesh = group.Meshes[iMesh];
				if (mesh.NumVisible == 0)
					continue;

				if (disp_dynamic.GetInt() != 0) {
					for (int iVisible = 0; iVisible < mesh.NumVisible; iVisible++)
						mesh.VisibleDisps[iVisible]!.SpecifyDynamicMesh();
				}
				else
					mesh.Mesh!.Draw(CollectionsMarshal.AsSpan(mesh.Visible), mesh.NumVisible);

				mesh.NumVisible = 0;
			}
		}
	}
	public static void DispInfo_ClearAllTags(object? hArray) {
		DispArray? array = (DispArray?)hArray;
		if (array == null)
			return;

		++array.CurTag;
		if (array.CurTag == 0xFFFF) {
			array.CurTag = 1;
			for (int i = 0; i < array.DispInfos.Length; i++)
				array.DispInfos[i].Tag = 0;
		}
	}

	public static int DispInfo_AddShadowsToMeshBuilder(ref MeshBuilder meshBuilder, DispShadowHandle h, int baseIndex) {
#if SWDS
		return 0;
#else
		ShadowDecalRenderInfo info = default;
		ref DispShadowDecal shadowDecal = ref s_DispShadowDecals[h];
		g_ShadowMgr.ComputeRenderInfo(ref info, shadowDecal.Shadow);

		Assert((shadowDecal.Base.Flags & DecalFlags.FragmentsComputed) != 0);

#if DEBUG
		int triCount = 0;
		int vertCount = 0;
#endif

		Vector2 texCoord;
		byte c;
		DispShadowFragmentHandle f = shadowDecal.FirstFragment;
		while (f != DISP_SHADOW_FRAGMENT_HANDLE_INVALID) {
			ref DispShadowFragment fragment = ref s_DispShadowFragments[f];
			Span<ShadowVertex> shadowVerts = fragment.ShadowVerts;

			int i;
			for (i = 0; i < fragment.NVerts - 2; ++i) {
				ref ShadowVertex shadowVert = ref shadowVerts[i];

				MathLib.Vector2DMultiply(shadowVert.ShadowSpaceTexCoord.AsVector2D(), info.TexSize, out texCoord);
				texCoord += info.TexOrigin;
				c = ((IShadowMgrInternal)g_ShadowMgr).ComputeDarkness(shadowVert.ShadowSpaceTexCoord.Z, in info);

				meshBuilder.Position3fv(shadowVert.Position);
				meshBuilder.Color4ub(c, c, c, c);
				meshBuilder.TexCoord2fv(0, texCoord);
				meshBuilder.AdvanceVertex();

				meshBuilder.FastIndex((ushort)baseIndex);
				meshBuilder.FastIndex((ushort)(i + baseIndex + 1));
				meshBuilder.FastIndex((ushort)(i + baseIndex + 2));
			}

			MathLib.Vector2DMultiply(shadowVerts[i].ShadowSpaceTexCoord.AsVector2D(), info.TexSize, out texCoord);
			texCoord += info.TexOrigin;
			c = ((IShadowMgrInternal)g_ShadowMgr).ComputeDarkness(shadowVerts[i].ShadowSpaceTexCoord.Z, in info);
			meshBuilder.Position3fv(shadowVerts[i].Position);
			meshBuilder.Color4ub(c, c, c, c);
			meshBuilder.TexCoord2fv(0, texCoord);
			meshBuilder.AdvanceVertex();
			++i;

			MathLib.Vector2DMultiply(shadowVerts[i].ShadowSpaceTexCoord.AsVector2D(), info.TexSize, out texCoord);
			texCoord += info.TexOrigin;
			c = ((IShadowMgrInternal)g_ShadowMgr).ComputeDarkness(shadowVerts[i].ShadowSpaceTexCoord.Z, in info);
			meshBuilder.Position3fv(shadowVerts[i].Position);
			meshBuilder.Color4ub(c, c, c, c);
			meshBuilder.TexCoord2fv(0, texCoord);
			meshBuilder.AdvanceVertex();

			baseIndex += fragment.NVerts;
			f = (DispShadowFragmentHandle)s_DispShadowFragments.Next(f);

#if DEBUG
			triCount += fragment.NVerts - 2;
			vertCount += fragment.NVerts;
#endif
		}

#if DEBUG
		Assert(triCount == shadowDecal.Base.NTris);
		Assert(vertCount == shadowDecal.Base.NVerts);
#endif

		return baseIndex;
#endif
	}

	static void DecalDispSurfacesInit() {
#if !SWDS
		Engine.Render.g_DispDecalSortPool.Clear();
		++Engine.Render.g_DispDecalSortCheckCount;
#endif
	}

	static void DispInfo_BatchDecals(ReadOnlySpan<DispInfo?> visibleDisps, int visibleDispCount) {
#if !SWDS
		DecalDispSurfacesInit();

		if (visibleDispCount == 0)
			return;

		for (int iDisp = 0; iDisp < visibleDispCount; ++iDisp) {
			DispInfo disp = visibleDisps[iDisp]!;
			if (disp.FirstDecal == DISP_DECAL_HANDLE_INVALID)
				continue;

			DispDecalHandle hDecal = disp.FirstDecal;
			while (hDecal != DISP_DECAL_HANDLE_INVALID) {
				ref DispDecal decal = ref s_DispDecals[hDecal];

				if ((decal.Base.Flags & DecalFlags.FragmentsComputed) == 0)
					disp.GenerateDecalFragments(disp.PowerInfo!.RootNode, 0, unchecked((ushort)hDecal), ref decal.Base);

				if (decal.Base.NTris == 0) {
					hDecal = unchecked((DispDecalHandle)s_DispDecals.Next(hDecal));
					continue;
				}

				IMaterial? material = decal.Decal!.Material;
				if (material == null) {
					DevMsg($"DispInfo_BatchDecals: material is NULL, decal {hDecal}.\n");
					hDecal = unchecked((DispDecalHandle)s_DispDecals.Next(hDecal));
					continue;
				}

				int treeType;
				if (material.GetPropertyFlag(MaterialPropertyTypes.NeedsLightmap)) {
					if ((decal.Decal.Flags & FDecal.Permanent) != 0)
						treeType = (int)DecalSortType.PermanentLightmap;
					else
						treeType = (int)DecalSortType.Lightmap;
				}
				else
					treeType = (int)DecalSortType.NonLightmap;

				int group = 0;

				int pool = Engine.Render.g_DispDecalSortPool.Alloc();
				Engine.Render.g_DispDecalSortPool[pool] = decal.Decal.DecalPool;
#if GMOD_DLL
				decal.Decal.Flags &= ~FDecal.HasUpdated;
#endif

				nint sortTree = decal.Decal.SortTree;
				nint sortMaterial = decal.Decal.SortMaterial;

				List<DecalMaterialBucket> materialBuckets = Engine.Render.g_DispDecalSortTrees[(int)sortTree].DecalSortBuckets[group][treeType];
#if GMOD_DLL
				if (materialBuckets.Count <= 0) {
					hDecal = unchecked((DispDecalHandle)s_DispDecals.Next(hDecal));
					continue;
				}
#endif

				DecalMaterialBucket materialBucket = materialBuckets[(int)sortMaterial];
				if (materialBucket.CheckCount == Engine.Render.g_DispDecalSortCheckCount) {
					nint head = materialBucket.Head;
					Engine.Render.g_DispDecalSortPool.LinkBefore((int)head, pool);
				}

				materialBucket.Head = pool;
				materialBucket.CheckCount = Engine.Render.g_DispDecalSortCheckCount;
				materialBuckets[(int)sortMaterial] = materialBucket;

				hDecal = unchecked((DispDecalHandle)s_DispDecals.Next(hDecal));
			}
		}
#endif
	}

	static void DispInfo_DrawDecalMeshList(ref DecalMeshList meshList) {
		using MatRenderContextPtr renderContext = new(materials);

		bool matFullbright = MatSysInterface.MaterialSystemConfig.Fullbright == 1;

		int batchCount = meshList.BatchCount;
		for (int iBatch = 0; iBatch < batchCount; ++iBatch) {
			ref DecalBatchList batch = ref meshList.Batches[iBatch];

			if (!matFullbright)
				renderContext.BindLightmapPage(batch.LightmapPage);
			else
				renderContext.BindLightmapPage(StandardLightmap.White);

			renderContext.Bind(batch.Material, batch.Proxy);
			meshList.Mesh!.Draw(batch.StartIndex, batch.IndexCount);
		}
	}

	static void DispInfo_DrawDecalsGroup(int group, int treeType) {
#if !SWDS
		using MatRenderContextPtr renderContext = new(materials);

		DecalMeshList meshList = new();
		MeshBuilder meshBuilder = new();

		int vertCount = 0;
		int indexCount = 0;

		Engine.Render.DecalsGetMaxMesh(renderContext.Context, out int decalSortMaxVerts, out int decalSortMaxIndices);

		bool matWireframe = ShouldDrawInWireFrameMode();

		Span<Decal?> decalPool = Engine.Render.s_DecalPool.AsSpan();
		PooledLinkedList<int> sortPool = Engine.Render.g_DispDecalSortPool;

		nint sortTreeCount = Engine.Render.g_DispDecalSortTrees.Count;
		for (nint iSortTree = 0; iSortTree < sortTreeCount; ++iSortTree) {
			bool meshInit = true;

			List<DecalMaterialBucket> materialBucketList = Engine.Render.g_DispDecalSortTrees[(int)iSortTree].DecalSortBuckets[group][treeType];
			nint bucketCount = materialBucketList.Count;
			for (nint iBucket = 0; iBucket < bucketCount; ++iBucket) {
				if (materialBucketList[(int)iBucket].CheckCount != Engine.Render.g_DispDecalSortCheckCount)
					continue;

				nint head = materialBucketList[(int)iBucket].Head;
				if (!sortPool.IsValidIndex((int)head))
					continue;

				Decal? decalHead = decalPool[sortPool[(int)head]];
				Assert(decalHead!.Material != null);
				if (decalHead.Material == null)
					continue;

#if GMOD_DLL
				if (decalHead.SurfID == -1) {
					Warning($"Displacement Decal @ {decalHead.Position.X:F2} {decalHead.Position.Y:F2} {decalHead.Position.Z:F2} has invalid surface ID!\n");
					continue;
				}
#endif

				VertexFormat vertexFormat = decalHead.Material.GetVertexFormat();
				if (vertexFormat == 0)
					continue;

				int batch = -1;
				bool batchInit = true;

				int count;
				int element = (int)head;
				while (element != PooledLinkedList<int>.INVALID_INDEX) {
					Decal decalElement = decalPool[sortPool[element]]!;
					element = sortPool.Next(element);

					ref DispDecal decal = ref s_DispDecals[decalElement.DispDecal];

					IMaterial? material = decal.Decal!.Material;
					if (material == null) {
						DevMsg($"DispInfo_DrawDecalsGroup: material is NULL decal {decalElement.DispDecal}.\n");
						continue;
					}

#if GMOD_DLL
					float fade = 1.0f;
					if ((decal.Decal.Flags & FDecal.Dynamic) != 0) {
						if (decal.Decal.FadeDuration < 0)
							fade = (float)(-(cl.GetTime() - decal.Decal.FadeStartTime) / decal.Decal.FadeDuration);
						else
							fade = (float)(1.0 - (cl.GetTime() - decal.Decal.FadeStartTime) / decal.Decal.FadeDuration);

						fade = Math.Clamp(fade, 0.0f, 1.0f);
					}
					byte alpha = (byte)(int)(fade * 255.0f);
#endif

					DispDecalFragmentHandle hFrag = decal.FirstFragment;
					while (hFrag != DISP_DECAL_FRAGMENT_HANDLE_INVALID) {
						ref DispDecalFragment fragment = ref s_DispDecalFragments[hFrag];
						hFrag = unchecked((DispDecalFragmentHandle)s_DispDecalFragments.Next(hFrag));
						count = fragment.NVerts;

						if ((vertCount + count >= decalSortMaxVerts) || (indexCount + (count - 2) >= decalSortMaxIndices)) {
							if (batch != -1)
								meshList.Batches[batch].IndexCount = (ushort)(indexCount - meshList.Batches[batch].StartIndex);

							if (!meshInit) { // note: GMod calls End here without Begin (fatal Error) when the previous sort tree left the counters full
								meshBuilder.End();
								DispInfo_DrawDecalMeshList(ref meshList);
							}

							meshInit = true;
							batch = -1;
							batchInit = true;
						}

						if (meshInit) {
							meshList.Mesh = null;
							meshList.BatchCount = 0;

							if (!matWireframe)
								meshList.Mesh = renderContext.GetDynamicMesh(false, null, null, decalHead.Material);
							else
								meshList.Mesh = renderContext.GetDynamicMesh(false, null, null, MatSys.MaterialDecalWireframe);

							meshBuilder.Begin(meshList.Mesh!, MaterialPrimitiveType.Triangles, decalSortMaxVerts, decalSortMaxIndices);

							vertCount = 0;
							indexCount = 0;

							meshInit = false;
						}

						if (batchInit) {
							batch = meshList.BatchCount++;
							ref DecalBatchList newBatch = ref meshList.Batches[batch];
							newBatch = default;
							newBatch.StartIndex = (ushort)indexCount;

							if (!matWireframe) {
								newBatch.Material = decalHead.Material;
								newBatch.Proxy = decalHead.UserData;
								newBatch.LightmapPage = MatSys.MaterialSortInfoArray![ModelLoader.MSurf_MaterialSortID(ref ModelLoader.SurfaceHandleFromIndex(decalHead.SurfID))].LightmapPageID;
							}
							else
								newBatch.Material = MatSys.MaterialDecalWireframe;

							batchInit = false;
						}
						Assert(batch != -1);

						Decal fragmentDecal = fragment.Decal!;
						float offset = fragmentDecal.LightmapOffset;
						for (int iVert = 0; iVert < fragment.NVerts; ++iVert) {
							ref DecalVert vert = ref fragment.Verts![iVert];

							meshBuilder.Position3fv(vert.Pos);
							ref Vector3 normal = ref ModelLoader.MSurf_Plane(ref ModelLoader.SurfaceHandleFromIndex(fragmentDecal.SurfID)).Normal;
							meshBuilder.Normal3fv(normal);
#if GMOD_DLL
							meshBuilder.Color4ub(255, 255, 255, alpha);
#else
							meshBuilder.Color4ub(fragmentDecal.Color.R, fragmentDecal.Color.G, fragmentDecal.Color.B, fragmentDecal.Color.A);
#endif
							meshBuilder.TexCoord2f(0, vert.CtCoords.X, vert.CtCoords.Y);
							meshBuilder.TexCoord2f(1, vert.CLMCoords.X, vert.CLMCoords.Y);
							meshBuilder.TexCoord1f(2, offset);
							meshBuilder.AdvanceVertex();
						}

						int triCount = count - 2;
						meshBuilder.FastPolygon(vertCount, triCount);

						vertCount += count;
						indexCount += triCount * 3;
					}

					if (batch != -1)
						meshList.Batches[batch].IndexCount = (ushort)(indexCount - meshList.Batches[batch].StartIndex);
				}
			}

			if (!meshInit) {
				meshBuilder.End();
				DispInfo_DrawDecalMeshList(ref meshList);
			}
		}
#endif
	}

	static void DispInfo_DrawDecals(ReadOnlySpan<DispInfo?> visibleDisps, int visibleDispCount) {
#if !SWDS
		int group = 0;

		DispInfo_DrawDecalsGroup(group, (int)DecalSortType.PermanentLightmap);

		DispInfo_DrawDecalsGroup(group, (int)DecalSortType.Lightmap);

		DispInfo_DrawDecalsGroup(group, (int)DecalSortType.NonLightmap);
#endif
	}
	static void DispInfo_DrawDebugInformation(Span<SurfaceHandle_t> list, int listCount) {
		// => throw new NotImplementedException();
	}

	public static IDispInfo? MLeaf_Disaplcement(BSPMLeaf leaf, int index, WorldBrushData? data = null) {
		data ??= host_state.WorldBrush;
		Assert(index < leaf.DispCount);
		int dispIndex = data!.DispInfoReferences![leaf.DispListStart + index];
		return DispInfo_IndexArray(data.DispInfos, dispIndex);
	}

	private static DispInfo? DispInfo_IndexArray(object? oArray, int i) {
		DispArray? array = (DispArray?)oArray;
		if (array == null)
			return null;
		return array.DispInfos[i];
	}
}

public class GetIntersectingSurfaces_Struct
{
	public Model? Model;
	public Vector3 Center;
	public byte[]? CenterPVS;
	public float Radius;
	public bool OnlyVisible;
	public SurfInfo[]? Infos;
	public int MaxInfos;
	public int SetInfos;
}
