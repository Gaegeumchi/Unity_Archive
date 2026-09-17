using System;
using System.Collections.Generic;
using UnityEngine;

namespace TrackSystem
{
    [Serializable]
    public sealed class TrackNode
    {
        public int id;
        /// <summary>TrackNetwork 로컬 좌표.</summary>
        public Vector3 position;
    }

    [Serializable]
    public sealed class TrackSegment
    {
        public int id;
        public int nodeA;
        public int nodeB;

        public int Other(int nodeId) => nodeId == nodeA ? nodeB : nodeA;
    }

    /// <summary>레일 위의 한 지점. distance는 nodeA에서부터 잰 월드 거리.</summary>
    public struct TrackLocation
    {
        public int segmentId;
        public float distance;
        public Vector3 position;
        public Vector3 tangent;
    }

    /// <summary>
    /// 노드(점)와 구간(두 노드를 잇는 레일)으로 이루어진 레일 그래프.
    /// 구간은 3차 베지어 곡선이며, 접선은 노드에 붙은 다른 구간을 보고 자동으로 정한다.
    ///  - 노드에서 서로 마주 보는 두 구간은 매끄럽게 이어지는 직통 선로가 된다.
    ///  - 나머지 구간은 직통 선로의 접선 방향으로 빠져나가는 분기(선로전환기)가 된다.
    /// 좌표는 이 오브젝트 로컬 공간에 저장하므로 네트워크 통째로 옮기거나 회전해도 된다(스케일은 균일하게).
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed partial class TrackNetwork : MonoBehaviour
    {
        /// <summary>직통 선로로 인정할 최대 꺾임(두 구간 바깥 방향의 내적 상한). 0.5 = 120°까지.</summary>
        const float ThroughMaxDot = .5f;

        [SerializeField] List<TrackNode> nodes = new List<TrackNode>();
        [SerializeField] List<TrackSegment> segments = new List<TrackSegment>();
        [SerializeField, HideInInspector] int nextId = 1;

        [Header("Curve")]
        [Tooltip("구간 길이 대비 베지어 핸들 길이. 클수록 코너가 크게 휜다.")]
        [SerializeField, Range(.05f, .5f)] float handleScale = .35f;
        [Tooltip("핸들 최대 길이(로컬 단위). 긴 직선이 코너 근처에서만 휘게 한다.")]
        [SerializeField, Min(.01f)] float maxHandleLength = 1.5f;
        [Tooltip("분기선이 본선 접선을 따라 빠져나가려면 필요한 최소 정렬도(0~1). 이보다 수직에 가까우면 꺾인 채로 붙는다.")]
        [SerializeField, Range(0f, 1f)] float branchMinAlignment = .2f;

        readonly Dictionary<int, TrackNode> nodeById = new Dictionary<int, TrackNode>();
        readonly Dictionary<int, TrackSegment> segmentById = new Dictionary<int, TrackSegment>();
        readonly Dictionary<int, List<int>> segmentsAtNode = new Dictionary<int, List<int>>();
        readonly Dictionary<int, Curve> curves = new Dictionary<int, Curve>();
        static readonly List<int> EmptyList = new List<int>();
        bool cacheValid;

        /// <summary>네트워크가 바뀔 때마다 증가. 따라가는 쪽이 캐시 무효화에 쓴다.</summary>
        public int Version { get; private set; }
        public event Action Changed;

        public IReadOnlyList<TrackNode> Nodes => nodes;
        public IReadOnlyList<TrackSegment> Segments => segments;
        float WorldScale => Mathf.Max(1e-6f, Mathf.Abs(transform.lossyScale.x));

        sealed class Curve
        {
            public Vector3 p0, p1, p2, p3;
            public Vector3 tangentA, tangentB; // 각 끝에서 구간 안쪽을 향하는 단위 접선(로컬)
            public Vector3[] points;
            public float[] cumulative;
            public float Length => cumulative[cumulative.Length - 1];

            public Vector3 Point(float t)
            {
                float u = 1f - t;
                return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
            }

            public Vector3 Derivative(float t)
            {
                float u = 1f - t;
                return 3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2);
            }

            public float DistanceToT(float d)
            {
                int last = cumulative.Length - 1;
                if (Length <= 1e-6f) return 0f;
                d = Mathf.Clamp(d, 0f, Length);
                int lo = 0, hi = last;
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) / 2;
                    if (cumulative[mid] < d) lo = mid; else hi = mid;
                }
                float span = cumulative[hi] - cumulative[lo];
                float f = span > 1e-6f ? (d - cumulative[lo]) / span : 0f;
                return (lo + f) / last;
            }

            public Vector3 TangentAt(float t)
            {
                Vector3 v = Derivative(t);
                return v.sqrMagnitude > 1e-10f ? v.normalized : (p3 - p0).normalized;
            }
        }

        #region Unity

        void OnEnable()
        {
            Invalidate();
            RebuildMeshNow();
        }

        void OnDisable() => ReleaseMesh();

        void OnValidate()
        {
            Invalidate();
            meshDirty = true;
#if UNITY_EDITOR
            // OnValidate 안에서는 MeshFilter를 건드리면 경고가 나므로 한 틱 미룬다.
            if (!Application.isPlaying)
                UnityEditor.EditorApplication.delayCall += () => { if (this != null && meshDirty) RebuildMeshNow(); };
#endif
        }

        void LateUpdate()
        {
            if (meshDirty) RebuildMeshNow();
        }

        #endregion

        #region Editing API

        /// <summary>월드 좌표에 노드를 추가하고 id를 돌려준다.</summary>
        public int AddNode(Vector3 worldPosition)
        {
            var node = new TrackNode { id = nextId++, position = transform.InverseTransformPoint(worldPosition) };
            nodes.Add(node);
            MarkDirty();
            return node.id;
        }

        public void MoveNode(int nodeId, Vector3 worldPosition)
        {
            EnsureCache();
            if (!nodeById.TryGetValue(nodeId, out TrackNode node)) return;
            node.position = transform.InverseTransformPoint(worldPosition);
            MarkDirty();
        }

        /// <summary>노드와 거기 붙은 구간을 모두 지운다.</summary>
        public bool RemoveNode(int nodeId)
        {
            int removed = nodes.RemoveAll(n => n.id == nodeId);
            if (removed == 0) return false;
            segments.RemoveAll(s => s.nodeA == nodeId || s.nodeB == nodeId);
            MarkDirty();
            return true;
        }

        /// <summary>두 노드를 잇는다. 이미 이어져 있거나 잘못된 노드면 -1.</summary>
        public int Connect(int nodeA, int nodeB)
        {
            EnsureCache();
            if (nodeA == nodeB || !nodeById.ContainsKey(nodeA) || !nodeById.ContainsKey(nodeB)) return -1;
            if (FindSegmentBetween(nodeA, nodeB) >= 0) return -1;
            var segment = new TrackSegment { id = nextId++, nodeA = nodeA, nodeB = nodeB };
            segments.Add(segment);
            MarkDirty();
            return segment.id;
        }

        /// <summary>구간을 지운다. removeOrphanNodes면 연결이 하나도 안 남은 끝 노드도 지운다.</summary>
        public bool RemoveSegment(int segmentId, bool removeOrphanNodes = true)
        {
            EnsureCache();
            if (!segmentById.TryGetValue(segmentId, out TrackSegment segment)) return false;
            segments.Remove(segment);
            if (removeOrphanNodes)
            {
                if (segmentsAtNode[segment.nodeA].Count <= 1) nodes.RemoveAll(n => n.id == segment.nodeA);
                if (segmentsAtNode[segment.nodeB].Count <= 1) nodes.RemoveAll(n => n.id == segment.nodeB);
            }
            MarkDirty();
            return true;
        }

        /// <summary>구간 중간(nodeA에서 distance 지점)에 노드를 끼워 넣어 둘로 나눈다. 새 노드 id, 실패 시 -1.</summary>
        public int SplitSegment(int segmentId, float distance)
        {
            EnsureCache();
            if (!segmentById.TryGetValue(segmentId, out TrackSegment segment)) return -1;
            Evaluate(segmentId, distance, out Vector3 position, out _);
            int a = segment.nodeA, b = segment.nodeB;
            segments.Remove(segment);
            int mid = AddNode(position);
            Connect(a, mid);
            Connect(mid, b);
            return mid;
        }

        public void Clear()
        {
            nodes.Clear();
            segments.Clear();
            MarkDirty();
        }

        /// <summary>직렬화 데이터를 직접 고친 뒤(Undo 등) 호출해 캐시와 메시를 갱신한다.</summary>
        public void MarkDirty()
        {
            Invalidate();
            meshDirty = true;
            if (!Application.isPlaying) RebuildMeshNow();
            Changed?.Invoke();
        }

        void Invalidate()
        {
            cacheValid = false;
            Version++;
        }

        #endregion

        #region Queries

        public bool HasNode(int nodeId) { EnsureCache(); return nodeById.ContainsKey(nodeId); }
        public bool HasSegment(int segmentId) { EnsureCache(); return segmentById.ContainsKey(segmentId); }

        public bool TryGetSegment(int segmentId, out TrackSegment segment)
        {
            EnsureCache();
            return segmentById.TryGetValue(segmentId, out segment);
        }

        public Vector3 GetNodePosition(int nodeId)
        {
            EnsureCache();
            return nodeById.TryGetValue(nodeId, out TrackNode n) ? transform.TransformPoint(n.position) : Vector3.zero;
        }

        public IReadOnlyList<int> GetSegmentsAtNode(int nodeId)
        {
            EnsureCache();
            return segmentsAtNode.TryGetValue(nodeId, out List<int> list) ? list : EmptyList;
        }

        public int FindSegmentBetween(int nodeA, int nodeB)
        {
            EnsureCache();
            if (!segmentsAtNode.TryGetValue(nodeA, out List<int> list)) return -1;
            foreach (int s in list)
                if (segmentById[s].Other(nodeA) == nodeB) return s;
            return -1;
        }

        /// <summary>구간의 월드 길이.</summary>
        public float GetSegmentLength(int segmentId)
        {
            EnsureCache();
            return curves.TryGetValue(segmentId, out Curve c) ? c.Length * WorldScale : 0f;
        }

        /// <summary>nodeA에서 distance(월드) 만큼 간 위치와 A→B 방향 접선(월드).</summary>
        public bool Evaluate(int segmentId, float distance, out Vector3 position, out Vector3 tangent)
        {
            EnsureCache();
            if (!curves.TryGetValue(segmentId, out Curve c))
            {
                position = Vector3.zero;
                tangent = Vector3.forward;
                return false;
            }
            float t = c.DistanceToT(distance / WorldScale);
            position = transform.TransformPoint(c.Point(t));
            tangent = transform.TransformDirection(c.TangentAt(t)).normalized;
            return true;
        }

        /// <summary>nodeId에서 segmentId를 따라 빠져나가는 방향(월드 단위 벡터).</summary>
        public Vector3 GetExitDirection(int segmentId, int nodeId)
        {
            EnsureCache();
            return transform.TransformDirection(LocalExitTangent(segmentId, nodeId)).normalized;
        }

        Vector3 LocalExitTangent(int segmentId, int nodeId)
        {
            if (!curves.TryGetValue(segmentId, out Curve c)) return Vector3.forward;
            return segmentById[segmentId].nodeA == nodeId ? c.tangentA : c.tangentB;
        }

        /// <summary>반경 안에서 가장 가까운 노드 id, 없으면 -1.</summary>
        public int FindNearestNode(Vector3 worldPoint, float maxDistance)
        {
            EnsureCache();
            int best = -1;
            float bestSqr = maxDistance * maxDistance;
            foreach (TrackNode n in nodeById.Values)
            {
                float d = (transform.TransformPoint(n.position) - worldPoint).sqrMagnitude;
                if (d <= bestSqr) { bestSqr = d; best = n.id; }
            }
            return best;
        }

        /// <summary>반경 안에서 레일 위 가장 가까운 지점.</summary>
        public bool TryGetNearestPoint(Vector3 worldPoint, float maxDistance, out TrackLocation location)
        {
            EnsureCache();
            location = default;
            float scale = WorldScale;
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            float bestSqr = maxDistance / scale * (maxDistance / scale);
            int bestSegment = -1;
            float bestDistance = 0f;

            foreach (KeyValuePair<int, Curve> kv in curves)
            {
                Vector3[] pts = kv.Value.points;
                float[] cum = kv.Value.cumulative;
                for (int i = 1; i < pts.Length; i++)
                {
                    Vector3 a = pts[i - 1], ab = pts[i] - a;
                    float len2 = ab.sqrMagnitude;
                    float f = len2 > 1e-10f ? Mathf.Clamp01(Vector3.Dot(local - a, ab) / len2) : 0f;
                    float d = (local - (a + ab * f)).sqrMagnitude;
                    if (d < bestSqr)
                    {
                        bestSqr = d;
                        bestSegment = kv.Key;
                        bestDistance = Mathf.Lerp(cum[i - 1], cum[i], f);
                    }
                }
            }

            if (bestSegment < 0) return false;
            location.segmentId = bestSegment;
            location.distance = bestDistance * scale;
            Evaluate(bestSegment, location.distance, out location.position, out location.tangent);
            return true;
        }

        /// <summary>월드 좌표 곡선 샘플(에디터 그리기, 미리보기용).</summary>
        public void GetSegmentPoints(int segmentId, List<Vector3> worldPoints)
        {
            EnsureCache();
            worldPoints.Clear();
            if (!curves.TryGetValue(segmentId, out Curve c)) return;
            foreach (Vector3 p in c.points) worldPoints.Add(transform.TransformPoint(p));
        }

        /// <summary>
        /// arrivalSegment를 타고 node에 도착했을 때 갈 수 있는 구간들.
        /// 진행 방향과 나가는 방향의 각도가 maxTurnAngle 이하인 구간만 허용한다(열차는 급커브/후진 불가).
        /// arrivalSegment가 -1이면 모든 구간.
        /// </summary>
        public void GetAllowedExits(int arrivalSegment, int nodeId, float maxTurnAngle, List<int> results)
        {
            EnsureCache();
            results.Clear();
            if (!segmentsAtNode.TryGetValue(nodeId, out List<int> list)) return;
            bool hasArrival = curves.ContainsKey(arrivalSegment);
            Vector3 travel = hasArrival ? -LocalExitTangent(arrivalSegment, nodeId) : Vector3.zero;
            float minDot = Mathf.Cos(maxTurnAngle * Mathf.Deg2Rad);
            foreach (int s in list)
            {
                if (s == arrivalSegment) continue;
                if (!hasArrival || Vector3.Dot(travel, LocalExitTangent(s, nodeId)) >= minDot)
                    results.Add(s);
            }
        }

        /// <summary>
        /// 방향/회전 제약을 지키며 targetNode까지 가는 최단 경로를 찾고, node에서 처음 탈 구간을 돌려준다. 없으면 -1.
        /// </summary>
        public int FindRouteExit(int arrivalSegment, int nodeId, int targetNode, float maxTurnAngle)
        {
            EnsureCache();
            if (!nodeById.ContainsKey(targetNode)) return -1;

            // 상태 = (구간, 진행방향). key = segmentId*2 + (A→B ? 1 : 0)
            var cost = new Dictionary<int, float>();
            var firstExit = new Dictionary<int, int>();
            var closed = new HashSet<int>();
            var open = new List<int>();
            var exits = new List<int>();

            GetAllowedExits(arrivalSegment, nodeId, maxTurnAngle, exits);
            foreach (int e in exits)
            {
                int key = e * 2 + (segmentById[e].nodeA == nodeId ? 1 : 0);
                cost[key] = curves[e].Length;
                firstExit[key] = e;
                open.Add(key);
            }

            while (open.Count > 0)
            {
                int bestIndex = 0;
                for (int i = 1; i < open.Count; i++)
                    if (cost[open[i]] < cost[open[bestIndex]]) bestIndex = i;
                int state = open[bestIndex];
                open.RemoveAt(bestIndex);
                if (!closed.Add(state)) continue;

                int seg = state >> 1;
                TrackSegment s = segmentById[seg];
                int end = (state & 1) == 1 ? s.nodeB : s.nodeA;
                if (end == targetNode) return firstExit[state];

                GetAllowedExits(seg, end, maxTurnAngle, exits);
                foreach (int e in exits)
                {
                    int key = e * 2 + (segmentById[e].nodeA == end ? 1 : 0);
                    float c = cost[state] + curves[e].Length;
                    if (closed.Contains(key) || (cost.TryGetValue(key, out float old) && old <= c)) continue;
                    cost[key] = c;
                    firstExit[key] = firstExit[state];
                    open.Add(key);
                }
            }
            return -1;
        }

        #endregion

        #region Save / Load

        [Serializable]
        sealed class SaveData
        {
            public List<TrackNode> nodes;
            public List<TrackSegment> segments;
            public int nextId;
        }

        public string ToJson(bool pretty = false) =>
            JsonUtility.ToJson(new SaveData { nodes = nodes, segments = segments, nextId = nextId }, pretty);

        public void LoadJson(string json)
        {
            var data = JsonUtility.FromJson<SaveData>(json);
            if (data == null) return;
            nodes = data.nodes ?? new List<TrackNode>();
            segments = data.segments ?? new List<TrackSegment>();
            nextId = data.nextId;
            foreach (TrackNode n in nodes) nextId = Mathf.Max(nextId, n.id + 1);
            foreach (TrackSegment s in segments) nextId = Mathf.Max(nextId, s.id + 1);
            MarkDirty();
        }

        #endregion

        #region Cache

        void EnsureCache()
        {
            if (cacheValid) return;
            nodeById.Clear();
            segmentById.Clear();
            segmentsAtNode.Clear();
            curves.Clear();

            foreach (TrackNode n in nodes)
            {
                if (n == null || nodeById.ContainsKey(n.id)) continue;
                nodeById[n.id] = n;
                segmentsAtNode[n.id] = new List<int>();
            }
            foreach (TrackSegment s in segments)
            {
                if (s == null || s.nodeA == s.nodeB || segmentById.ContainsKey(s.id)) continue;
                if (!nodeById.ContainsKey(s.nodeA) || !nodeById.ContainsKey(s.nodeB)) continue;
                segmentById[s.id] = s;
                segmentsAtNode[s.nodeA].Add(s.id);
                segmentsAtNode[s.nodeB].Add(s.id);
            }

            cacheValid = true;
            foreach (TrackSegment s in segmentById.Values) curves[s.id] = BuildCurve(s);
        }

        Curve BuildCurve(TrackSegment s)
        {
            Vector3 a = nodeById[s.nodeA].position, b = nodeById[s.nodeB].position;
            float chord = Vector3.Distance(a, b);
            float handle = Mathf.Min(chord * handleScale, maxHandleLength);
            var c = new Curve
            {
                tangentA = ComputeExitTangent(s.id, s.nodeA),
                tangentB = ComputeExitTangent(s.id, s.nodeB),
                p0 = a,
                p3 = b,
            };
            c.p1 = a + c.tangentA * handle;
            c.p2 = b + c.tangentB * handle;

            int samples = Mathf.Clamp(Mathf.CeilToInt(chord * 6f) + 8, 12, 256);
            c.points = new Vector3[samples + 1];
            c.cumulative = new float[samples + 1];
            c.points[0] = a;
            for (int i = 1; i <= samples; i++)
            {
                c.points[i] = c.Point((float)i / samples);
                c.cumulative[i] = c.cumulative[i - 1] + Vector3.Distance(c.points[i - 1], c.points[i]);
            }
            return c;
        }

        Vector3 ChordDirection(int segmentId, int nodeId)
        {
            TrackSegment s = segmentById[segmentId];
            Vector3 d = nodeById[s.Other(nodeId)].position - nodeById[nodeId].position;
            return d.sqrMagnitude > 1e-10f ? d.normalized : Vector3.forward;
        }

        /// <summary>node에 붙은 구간 중 exclude를 뺀, dir과 가장 반대 방향인 구간.</summary>
        int MostOpposite(int nodeId, int exclude, Vector3 dir)
        {
            int best = -1;
            float bestDot = float.MaxValue;
            foreach (int other in segmentsAtNode[nodeId])
            {
                if (other == exclude) continue;
                float d = Vector3.Dot(dir, ChordDirection(other, nodeId));
                if (d < bestDot) { bestDot = d; best = other; }
            }
            return best;
        }

        Vector3 ComputeExitTangent(int segmentId, int nodeId)
        {
            Vector3 d = ChordDirection(segmentId, nodeId);
            int partner = MostOpposite(nodeId, segmentId, d);
            if (partner < 0) return d;

            Vector3 dp = ChordDirection(partner, nodeId);
            int partnersPartner = MostOpposite(nodeId, partner, dp);

            if (partnersPartner == segmentId)
            {
                // 서로 마주 보는 쌍 → 직통 선로: 들어오는 방향과 나가는 방향의 평균.
                if (Vector3.Dot(d, dp) > ThroughMaxDot) return d;
                Vector3 t = d - dp;
                return t.sqrMagnitude > 1e-8f ? t.normalized : d;
            }

            // 분기: 본선(partner ↔ partnersPartner)의 접선을 따라 빠져나간다.
            Vector3 dq = ChordDirection(partnersPartner, nodeId);
            if (Vector3.Dot(dq, dp) > ThroughMaxDot) return d;
            Vector3 main = dq - dp;
            if (main.sqrMagnitude < 1e-8f) return d;
            main.Normalize();
            float align = Vector3.Dot(main, d);
            if (align < 0f) { main = -main; align = -align; }
            return align >= branchMinAlignment ? main : d;
        }

        #endregion
    }
}
