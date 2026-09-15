using UnityEngine;
using UnityEngine.InputSystem;

namespace Building
{
    public sealed class BuildGrid : MonoBehaviour
    {
        public static BuildGrid Instance { get; private set; }

        [Header("Grid")]
        [SerializeField, Min(.1f)] float cellSize = 2f;
        [SerializeField] float gridHeight = 0.51f;

        [Header("Visualization")]
        [SerializeField] Key toggleKey = Key.G;
        [SerializeField] Transform followTarget;
        [SerializeField, Min(4f)] float visualSize = 60f;
        [SerializeField] Material gridMaterial;

        Transform gridLines;
        MeshFilter gridMeshFilter;
        MeshRenderer gridRenderer;
        bool visible;
        int forceVisibleCount;
        float builtCellSize;
        float builtVisualSize;

        public float CellSize => cellSize;
        public bool IsVisible => visible;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            if (followTarget == null && Camera.main != null) followTarget = Camera.main.transform;
            CreateGridLines();
            SetVisible(false);
        }

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current[toggleKey].wasPressedThisFrame)
                SetVisible(!visible);

            if (!Mathf.Approximately(cellSize, builtCellSize) || !Mathf.Approximately(visualSize, builtVisualSize))
                RebuildGridMesh();

            if (gridLines != null && followTarget != null)
            {
                Vector3 pos = followTarget.position;
                Vector3 snapped = SnapToGrid(pos);
                gridLines.position = new Vector3(snapped.x, gridHeight, snapped.z);
            }
        }

        void RebuildGridMesh()
        {
            if (gridMeshFilter == null) return;
            builtCellSize = cellSize;
            builtVisualSize = visualSize;
            gridMeshFilter.sharedMesh = BuildLineMesh(visualSize, cellSize);
        }

        public void SetVisible(bool value)
        {
            visible = value;
            if (gridRenderer != null) gridRenderer.enabled = visible || forceVisibleCount > 0;
        }

        /// <summary>배치 도구 등에서 임시로 그리드를 강제 표시할 때 사용. 반드시 짝을 맞춰 EndForceVisible을 호출할 것.</summary>
        public void BeginForceVisible()
        {
            forceVisibleCount++;
            if (gridRenderer != null) gridRenderer.enabled = true;
        }

        public void EndForceVisible()
        {
            forceVisibleCount = Mathf.Max(0, forceVisibleCount - 1);
            if (gridRenderer != null) gridRenderer.enabled = visible || forceVisibleCount > 0;
        }

        public Vector3 SnapToGrid(Vector3 worldPosition)
        {
            float x = Mathf.Round(worldPosition.x / cellSize) * cellSize;
            float z = Mathf.Round(worldPosition.z / cellSize) * cellSize;
            return new Vector3(x, worldPosition.y, z);
        }

        public Vector3 SnapToGridFootprint(Vector3 worldPosition, Vector2 footprintXZ)
        {
            float x = SnapAxis(worldPosition.x, footprintXZ.x);
            float z = SnapAxis(worldPosition.z, footprintXZ.y);
            return new Vector3(x, worldPosition.y, z);
        }

        float SnapAxis(float value, float size)
        {
            int cellCount = Mathf.Max(1, Mathf.RoundToInt(size / cellSize));
            float offset = (cellCount % 2 != 0) ? cellSize * .5f : 0f;
            return Mathf.Round((value - offset) / cellSize) * cellSize + offset;
        }

        public Quaternion SnapRotation(Quaternion rotation, float step = 90f)
        {
            Vector3 euler = rotation.eulerAngles;
            euler.y = Mathf.Round(euler.y / step) * step;
            return Quaternion.Euler(euler);
        }

        void CreateGridLines()
        {
            var go = new GameObject("GridVisual");
            go.transform.SetParent(transform, false);

            gridMeshFilter = go.AddComponent<MeshFilter>();
            builtCellSize = cellSize;
            builtVisualSize = visualSize;
            gridMeshFilter.sharedMesh = BuildLineMesh(visualSize, cellSize);

            gridRenderer = go.AddComponent<MeshRenderer>();
            gridRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            gridRenderer.receiveShadows = false;

            if (gridMaterial == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader != null)
                {
                    gridMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    gridMaterial.SetColor("_BaseColor", new Color(.2f, 1f, .6f, .9f));
                    gridMaterial.SetFloat("_Surface", 1f);
                    gridMaterial.SetFloat("_Blend", 0f);
                    gridMaterial.SetFloat("_ZWrite", 0f);
                    gridMaterial.SetOverrideTag("RenderType", "Transparent");
                    gridMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    gridMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    gridMaterial.SetShaderPassEnabled("Universal Forward", true);
                }
                else
                {
                    Debug.LogError("Can't find Universal Render Pipeline/Unlit shader", this);
                }
            }
            gridRenderer.sharedMaterial = gridMaterial;
            gridLines = go.transform;
        }

        static Mesh BuildLineMesh(float size, float cellSize)
        {
            var mesh = new Mesh { name = "BuildGridLines" };
            int lineCount = Mathf.Max(1, Mathf.RoundToInt(size / cellSize));
            float half = lineCount * cellSize * .5f;

            var vertices = new System.Collections.Generic.List<Vector3>();
            var indices = new System.Collections.Generic.List<int>();

            for (int i = 0; i <= lineCount; i++)
            {
                float offset = -half + i * cellSize;
                vertices.Add(new Vector3(offset, 0f, -half));
                vertices.Add(new Vector3(offset, 0f, half));
                indices.Add(vertices.Count - 2);
                indices.Add(vertices.Count - 1);

                vertices.Add(new Vector3(-half, 0f, offset));
                vertices.Add(new Vector3(half, 0f, offset));
                indices.Add(vertices.Count - 2);
                indices.Add(vertices.Count - 1);
            }

            mesh.SetVertices(vertices);
            mesh.SetIndices(indices.ToArray(), MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
