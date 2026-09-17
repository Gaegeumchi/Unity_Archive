using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TrackSystem
{
    /// <summary>레일(두 줄) + 침목 메시를 절차적으로 만든다. 메시는 씬에 저장되지 않고 매번 다시 생성된다.</summary>
    public sealed partial class TrackNetwork
    {
        [Header("Visual")]
        [SerializeField] bool generateMesh = true;
        [Tooltip("비워두면 현재 렌더 파이프라인 기본 머티리얼을 복제해 색만 바꿔 쓴다.")]
        [SerializeField] Material railMaterial;
        [SerializeField] Material sleeperMaterial;
        [SerializeField] Color railColor = new Color(.62f, .64f, .68f);
        [SerializeField] Color sleeperColor = new Color(.36f, .26f, .18f);
        [SerializeField, Min(.05f)] float gauge = .5f;
        [SerializeField, Min(.005f)] float railWidth = .05f;
        [SerializeField, Min(.005f)] float railHeight = .06f;
        [SerializeField, Min(.05f)] float sleeperSpacing = .35f;
        [SerializeField, Min(.05f)] float sleeperLength = .8f;
        [SerializeField, Min(.01f)] float sleeperWidth = .12f;
        [SerializeField, Min(.005f)] float sleeperHeight = .04f;
        [Tooltip("레일을 따라 메시를 자르는 간격.")]
        [SerializeField, Min(.05f)] float meshStep = .2f;

        /// <summary>레일 윗면 높이(로컬). 차량을 올릴 높이 계산용.</summary>
        public float RailTopHeight => generateMesh ? (sleeperHeight + railHeight) * WorldScale : 0f;

        Mesh mesh;
        Material defaultRail, defaultSleeper;
        bool meshDirty = true;

        readonly List<Vector3> vertices = new List<Vector3>();
        readonly List<Vector3> normals = new List<Vector3>();
        readonly List<int> railTriangles = new List<int>();
        readonly List<int> sleeperTriangles = new List<int>();

        public void RebuildMeshNow()
        {
            if (this == null) return;
            meshDirty = false;
            var filter = GetComponent<MeshFilter>();
            var meshRenderer = GetComponent<MeshRenderer>();
            if (!generateMesh)
            {
                filter.sharedMesh = null;
                return;
            }

            if (mesh == null)
            {
                mesh = new Mesh { name = "TrackMesh", hideFlags = HideFlags.DontSave };
                mesh.MarkDynamic();
            }

            EnsureCache();
            vertices.Clear();
            normals.Clear();
            railTriangles.Clear();
            sleeperTriangles.Clear();
            foreach (Curve c in curves.Values) AppendSegment(c);

            mesh.Clear();
            mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(railTriangles, 0);
            mesh.SetTriangles(sleeperTriangles, 1);
            mesh.RecalculateBounds();

            filter.sharedMesh = mesh;
            meshRenderer.sharedMaterials = new[]
            {
                railMaterial != null ? railMaterial : DefaultMaterial(ref defaultRail, railColor, "TrackRail"),
                sleeperMaterial != null ? sleeperMaterial : DefaultMaterial(ref defaultSleeper, sleeperColor, "TrackSleeper"),
            };
        }

        void ReleaseMesh()
        {
            SafeDestroy(mesh);
            SafeDestroy(defaultRail);
            SafeDestroy(defaultSleeper);
            mesh = null;
            defaultRail = defaultSleeper = null;
        }

        static void SafeDestroy(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        /// <summary>현재 렌더 파이프라인에서 보이는 단색 머티리얼을 만든다.</summary>
        public static Material CreateColorMaterial(Color color, string name)
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            Material source = pipeline != null ? pipeline.defaultMaterial : null;
            Material m;
            if (source != null) m = new Material(source);
            else
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
                m = new Material(shader);
            }
            m.name = name;
            m.hideFlags = HideFlags.DontSave;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            return m;
        }

        static Material DefaultMaterial(ref Material cache, Color color, string name)
        {
            if (cache == null) cache = CreateColorMaterial(color, name);
            return cache;
        }

        void AppendSegment(Curve c)
        {
            float length = c.Length;
            if (length < 1e-4f) return;
            int steps = Mathf.Max(2, Mathf.CeilToInt(length / meshStep));

            float half = gauge * .5f;
            float w = railWidth * .5f;
            float bottom = sleeperHeight, top = sleeperHeight + railHeight;

            Vector3 prevPos = default, prevRight = default, prevUp = default;
            for (int i = 0; i <= steps; i++)
            {
                GetFrame(c, length * i / steps, out Vector3 pos, out Vector3 right, out Vector3 up);
                if (i > 0)
                {
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Vector3 o0 = prevPos + prevRight * (half * side), o1 = pos + right * (half * side);
                        // 윗면
                        AddQuad(railTriangles,
                            o0 - prevRight * w + prevUp * top, o1 - right * w + up * top,
                            o1 + right * w + up * top, o0 + prevRight * w + prevUp * top,
                            prevUp, up);
                        // 오른쪽 면
                        AddQuad(railTriangles,
                            o0 + prevRight * w + prevUp * top, o1 + right * w + up * top,
                            o1 + right * w + up * bottom, o0 + prevRight * w + prevUp * bottom,
                            prevRight, right);
                        // 왼쪽 면
                        AddQuad(railTriangles,
                            o0 - prevRight * w + prevUp * bottom, o1 - right * w + up * bottom,
                            o1 - right * w + up * top, o0 - prevRight * w + prevUp * top,
                            -prevRight, -right);
                    }
                }
                prevPos = pos;
                prevRight = right;
                prevUp = up;
            }

            for (float d = sleeperSpacing * .5f; d < length; d += sleeperSpacing)
            {
                GetFrame(c, d, out Vector3 pos, out Vector3 right, out Vector3 up);
                Vector3 forward = Vector3.Cross(right, up);
                AddBox(sleeperTriangles, pos + up * (sleeperHeight * .5f),
                    right * (sleeperLength * .5f), up * (sleeperHeight * .5f), forward * (sleeperWidth * .5f));
            }
        }

        static void GetFrame(Curve c, float distance, out Vector3 pos, out Vector3 right, out Vector3 up)
        {
            float t = c.DistanceToT(distance);
            pos = c.Point(t);
            Vector3 forward = c.TangentAt(t);
            right = Vector3.Cross(Vector3.up, forward);
            right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;
            up = Vector3.Cross(forward, right).normalized;
        }

        /// <summary>바깥에서 볼 때 시계 방향 순서의 사각형.</summary>
        void AddQuad(List<int> triangles, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normalStart, Vector3 normalEnd)
        {
            int i = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            normals.Add(normalStart); normals.Add(normalEnd); normals.Add(normalEnd); normals.Add(normalStart);
            triangles.Add(i); triangles.Add(i + 1); triangles.Add(i + 2);
            triangles.Add(i); triangles.Add(i + 2); triangles.Add(i + 3);
        }

        void AddBox(List<int> triangles, Vector3 center, Vector3 r, Vector3 u, Vector3 f)
        {
            // 각 면: 법선 N, Cross(V, U) = N 이 되게 U/V를 고르면 (-U-V, -U+V, +U+V, +U-V)가 시계 방향.
            AddFace(triangles, center, u, r, f);   // +up
            AddFace(triangles, center, -u, f, r);  // -up
            AddFace(triangles, center, r, f, u);   // +right
            AddFace(triangles, center, -r, u, f);  // -right
            AddFace(triangles, center, f, u, r);   // +forward
            AddFace(triangles, center, -f, r, u);  // -forward
        }

        void AddFace(List<int> triangles, Vector3 center, Vector3 n, Vector3 uAxis, Vector3 vAxis)
        {
            Vector3 c = center + n, normal = n.normalized;
            AddQuad(triangles, c - uAxis - vAxis, c - uAxis + vAxis, c + uAxis + vAxis, c + uAxis - vAxis, normal, normal);
        }
    }
}
