using Source.Common;
using Source.Common.Mathematics;

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Source.StudioRender;

public struct CachedPosNormTan
{
	public Vector3 Position;
	public Vector3 Normal;
	public Vector4 TangentS;
}

public struct CachedPosNorm
{
	public Vector4 Position;
	public Vector4 Normal;
}

public class CachedRenderData
{
	struct CacheIndex
	{
		public ushort Tag;
		public ushort VertexIndex;
	}

	struct CacheDict
	{
		public ushort FirstIndex;
		public ushort IndexCount;
		public ushort Tag;
		public ushort FlexTag;
	}

	int FlexVertexCount;
	readonly CachedPosNormTan[] FlexVerts = new CachedPosNormTan[Studio.MAXSTUDIOFLEXVERTS + 1];

	int ThinFlexVertexCount;
	readonly CachedPosNorm[] ThinFlexVerts = new CachedPosNorm[Studio.MAXSTUDIOFLEXVERTS + 1];

	int WorldVertexCount;
	readonly CachedPosNorm[] WorldVerts = new CachedPosNorm[Studio.MAXSTUDIOVERTS + 1];

	int IndexCount;
	readonly CacheIndex[] FlexIndex = new CacheIndex[Studio.MAXSTUDIOVERTS + 1];
	readonly CacheIndex[] ThinFlexIndex = new CacheIndex[Studio.MAXSTUDIOVERTS + 1];
	readonly CacheIndex[] WorldIndex = new CacheIndex[Studio.MAXSTUDIOVERTS + 1];

	readonly List<List<List<CacheDict>>> CacheDictionary = [];

	ushort CurrentTag;

	int Body;
	int Model;
	int Mesh;

	int FirstFlexIndex = -1;
	int FirstThinFlexIndex = -1;
	int FirstWorldIndex = -1;

	public void StartModel() {
		++CurrentTag;
		IndexCount = 0;
		FlexVertexCount = 0;
		ThinFlexVertexCount = 0;
		WorldVertexCount = 0;
		FirstFlexIndex = -1;
		FirstThinFlexIndex = -1;
		FirstWorldIndex = -1;
	}

	public void SetBodyPart(int bodypart) {
		Body = bodypart;
		CacheDictionary.EnsureCount(Body + 1);
		Model = Mesh = -1;
		FirstFlexIndex = -1;
		FirstThinFlexIndex = -1;
		FirstWorldIndex = -1;
	}

	public void SetModel(int model) {
		Assert(Body >= 0);
#if GMOD_DLL
		if (Body < 0 || Body >= CacheDictionary.Count) {
			Warning($"CCachedRenderData::SetModel: invalid body part {Body} (of {CacheDictionary.Count})\n");
			return;
		}
#endif
		Model = model;
		CacheDictionary[Body].EnsureCount(Model + 1);
		Mesh = -1;
		FirstFlexIndex = -1;
		FirstThinFlexIndex = -1;
		FirstWorldIndex = -1;
	}

	public void SetMesh(int mesh) {
		Assert((Model >= 0) && (Body >= 0));

#if GMOD_DLL
		if (Body < 0 || Body >= CacheDictionary.Count) {
			Warning($"CCachedRenderData::SetModel: invalid body part {Body} (of {CacheDictionary.Count})\n");
			return;
		}
		if (Model < 0 || Model >= CacheDictionary[Body].Count) {
			Warning($"CCachedRenderData::SetModel: invalid model {Model} (of {CacheDictionary[Body].Count})\n");
			return;
		}
#endif

		Mesh = mesh;
		CacheDictionary[Body][Model].EnsureCountDefault(Mesh + 1);

		ref CacheDict dict = ref CollectionsMarshal.AsSpan(CacheDictionary[Body][Model])[Mesh];

		if (dict.Tag == CurrentTag) {
			FirstFlexIndex = dict.FirstIndex;
			FirstThinFlexIndex = dict.FirstIndex;
			FirstWorldIndex = dict.FirstIndex;
		}
		else {
			FirstFlexIndex = -1;
			FirstThinFlexIndex = -1;
			FirstWorldIndex = -1;
		}
	}

	public void SetBodyModelMesh(int body, int model, int mesh) {
		Body = body;
		Model = model;
		Mesh = mesh;

#if GMOD_DLL
		if (IsCacheDictValid()) {
			ref CacheDict validDict = ref CollectionsMarshal.AsSpan(CacheDictionary[Body][Model])[Mesh];
			if (validDict.Tag == CurrentTag) {
				FirstFlexIndex = validDict.FirstIndex;
				FirstThinFlexIndex = validDict.FirstIndex;
				FirstWorldIndex = validDict.FirstIndex;
				return;
			}
		}

		FirstFlexIndex = -1;
		FirstThinFlexIndex = -1;
		FirstWorldIndex = -1;
#else
		Assert((Model >= 0) && (Body >= 0));
		CacheDictionary[Body][Model].EnsureCountDefault(Mesh + 1);

		ref CacheDict dict = ref CollectionsMarshal.AsSpan(CacheDictionary[Body][Model])[Mesh];

		if (dict.Tag == CurrentTag) {
			FirstFlexIndex = dict.FirstIndex;
			FirstThinFlexIndex = dict.FirstIndex;
			FirstWorldIndex = dict.FirstIndex;
		}
		else {
			FirstFlexIndex = -1;
			FirstThinFlexIndex = -1;
			FirstWorldIndex = -1;
		}
#endif
	}

#if GMOD_DLL
	public bool IsCacheDictValid() {
		if (Body < 0 || Body >= CacheDictionary.Count) {
			Warning($"CCachedRenderData: invalid body part {Body} (of {CacheDictionary.Count})\n");
			return false;
		}

		List<List<CacheDict>> models = CacheDictionary[Body];
		if (Model < 0 || Model >= models.Count) {
			Warning($"CCachedRenderData: invalid model {Model} (of {models.Count})\n");
			return false;
		}

		List<CacheDict> meshes = models[Model];
		if (Mesh < 0 || Mesh >= meshes.Count) {
			Warning($"CCachedRenderData: invalid mesh {Mesh} (of {meshes.Count})\n");
			return false;
		}

		return true;
	}
#endif

	public bool IsVertexPositionCached(int vertex) => FirstWorldIndex != -1 && WorldIndex[FirstWorldIndex + vertex].Tag == CurrentTag;

	public bool IsFlexComputationDone() {
		Assert((Model >= 0) && (Body >= 0) && (Mesh >= 0));

#if GMOD_DLL
		if (!IsCacheDictValid())
			return false;
#endif

		ref CacheDict dict = ref CollectionsMarshal.AsSpan(CacheDictionary[Body][Model])[Mesh];
		return dict.FlexTag == CurrentTag;
	}

	public bool SetupComputation(MStudioMesh mesh, bool flexComputation = false) {
		Assert((Model >= 0) && (Body >= 0) && (Mesh >= 0));

#if GMOD_DLL
		if (!IsCacheDictValid())
			return false;
#endif

		ref CacheDict dict = ref CollectionsMarshal.AsSpan(CacheDictionary[Body][Model])[Mesh];
		if (dict.Tag != CurrentTag) {
#if GMOD_DLL
			if (IndexCount + mesh.NumVertices > 0x10000)
				return false;
#endif
			dict.FirstIndex = (ushort)IndexCount;
			dict.IndexCount = (ushort)mesh.NumVertices;
			dict.Tag = CurrentTag;
			IndexCount += dict.IndexCount;
		}

		if (flexComputation)
			dict.FlexTag = CurrentTag;

		FirstFlexIndex = dict.FirstIndex;
		FirstThinFlexIndex = dict.FirstIndex;
		FirstWorldIndex = dict.FirstIndex;
		return true;
	}

	public bool IsVertexFlexed(int vertex) => FirstFlexIndex != -1 && FlexIndex[FirstFlexIndex + vertex].Tag == CurrentTag;

	public bool IsThinVertexFlexed(int vertex) => FirstThinFlexIndex != -1 && ThinFlexIndex[FirstThinFlexIndex + vertex].Tag == CurrentTag;

	public ref CachedPosNormTan GetFlexVertex(int vertex) {
		Assert(FirstFlexIndex != -1);
		Assert(FlexIndex[FirstFlexIndex + vertex].Tag == CurrentTag);
		return ref FlexVerts[FlexIndex[FirstFlexIndex + vertex].VertexIndex];
	}

	public ref CachedPosNormTan CreateFlexVertex(int vertex) {
		Assert(FirstFlexIndex != -1);
		Assert(FlexIndex[FirstFlexIndex + vertex].Tag != CurrentTag);

		Assert(FlexVertexCount < Studio.MAXSTUDIOFLEXVERTS);
		if (FlexVertexCount >= Studio.MAXSTUDIOFLEXVERTS)
			return ref Unsafe.NullRef<CachedPosNormTan>();

		FlexIndex[FirstFlexIndex + vertex].Tag = CurrentTag;
		FlexIndex[FirstFlexIndex + vertex].VertexIndex = (ushort)FlexVertexCount;

		++FlexVertexCount;

		return ref GetFlexVertex(vertex);
	}

	public ref CachedPosNorm GetThinFlexVertex(int vertex) {
		Assert(FirstThinFlexIndex != -1);
		Assert(ThinFlexIndex[FirstThinFlexIndex + vertex].Tag == CurrentTag);
		return ref ThinFlexVerts[ThinFlexIndex[FirstThinFlexIndex + vertex].VertexIndex];
	}

	public ref CachedPosNorm CreateThinFlexVertex(int vertex) {
		Assert(FirstThinFlexIndex != -1);
		Assert(ThinFlexIndex[FirstThinFlexIndex + vertex].Tag != CurrentTag);

		Assert(ThinFlexVertexCount < Studio.MAXSTUDIOFLEXVERTS);
		if (ThinFlexVertexCount >= Studio.MAXSTUDIOFLEXVERTS)
			return ref Unsafe.NullRef<CachedPosNorm>();

		ThinFlexIndex[FirstThinFlexIndex + vertex].Tag = CurrentTag;
		ThinFlexIndex[FirstThinFlexIndex + vertex].VertexIndex = (ushort)ThinFlexVertexCount;

		++ThinFlexVertexCount;

		return ref GetThinFlexVertex(vertex);
	}

	public void RenormalizeFlexVertices(bool hasTangentData) {
		for (int i = 0; i < FlexVertexCount; i++) {
			FlexVerts[i].Normal.NormalizeInPlace();
			if (hasTangentData)
				FlexVerts[i].TangentS.AsVector3D().NormalizeInPlace();
		}
	}

	public ref CachedPosNorm GetWorldVertex(int vertex) {
		Assert(FirstWorldIndex != -1);
		Assert(WorldIndex[FirstWorldIndex + vertex].Tag == CurrentTag);
		return ref WorldVerts[WorldIndex[FirstWorldIndex + vertex].VertexIndex];
	}

	public ref CachedPosNorm CreateWorldVertex(int vertex) {
		Assert(FirstWorldIndex != -1);
		if (WorldIndex[FirstWorldIndex + vertex].Tag != CurrentTag) {
			Assert(WorldVertexCount < Studio.MAXSTUDIOVERTS);
			WorldIndex[FirstWorldIndex + vertex].Tag = CurrentTag;
			WorldIndex[FirstWorldIndex + vertex].VertexIndex = (ushort)WorldVertexCount;

			++WorldVertexCount;
		}
		return ref GetWorldVertex(vertex);
	}
}
