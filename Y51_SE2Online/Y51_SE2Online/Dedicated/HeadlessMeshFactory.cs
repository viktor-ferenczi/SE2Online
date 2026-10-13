using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Mathematics;

namespace Y51_SE2Online.Dedicated;

internal sealed class HeadlessMeshFactory : IMeshBuilderFactory, IMeshConsumer
{
    public MeshBuilder CreateMeshBuilder(MeshBuilder.Type type, long parentId = 0, int expectedVerticesCount = 100, string debugName = "")
        => new(this, type, parentId, expectedVerticesCount, debugName);
    public MeshBuilder2D CreateMeshBuilder2D(MeshBuilder2D.Type type, int expectedVerticesCount = 100, string debugName = "")
        => new(this, type, expectedVerticesCount, debugName);
    public int CommitBatch(MeshBuilderData meshBuilderData, PooledList<RelativeTransform> instances,
        IMeshConsumer.BatchTypes type, MeshBuilder.Type builderType, RenderRuntimeDataType runtimeDataType,
        long parentId = 0, WorldTransform parentTransform = default, int overwriteBatchId = -1, object customData = null) => -1;
    public void UpdateMeshTransform(int batchId, WorldTransform worldTransform) { }
    public void TryDisposeBatch(int batchId) { }
    public void DisposeBatch(int batchId) { }
    public void DisposeAllBatches() { }
    public void SubmitTexts(PooledList<IMeshConsumer.Text> texts, PooledList<RelativeTransform> instances = null,
        long parentId = 0, WorldTransform transform = default, Vector3D worldToLocalOffset = default, object customData = null) { }
}
