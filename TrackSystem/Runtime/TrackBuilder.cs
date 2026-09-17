using System;
using System.Collections.Generic;
using UnityEngine;

namespace TrackSystem
{
    /// <summary>
    /// 플레이 중 유저가 마우스로 레일을 깔고 지우고 차량을 올리는 도구.
    ///  - 좌클릭: 레일 시작/이어 깔기 (기존 노드에 붙이면 연결, 레일 중간에 붙이면 분기 생성)
    ///  - 우클릭 / Esc: 이어 깔기 끊기
    ///  - X 누른 채 좌클릭: 노드 또는 구간 삭제
    ///  - V: 커서 근처 레일에 차량(큐브) 소환
    ///  - T: 빌드 모드 켜기/끄기
    /// </summary>
    public sealed class TrackBuilder : MonoBehaviour
    {
        [SerializeField] TrackNetwork network;
        [Tooltip("비워두면 Camera.main.")]
        [SerializeField] Camera targetCamera;
        [SerializeField] bool buildModeActive = true;

        [Header("Keys")]
        [SerializeField] KeyCode toggleKey = KeyCode.T;
        [SerializeField] KeyCode deleteHoldKey = KeyCode.X;
        [SerializeField] KeyCode spawnKey = KeyCode.V;
        [SerializeField] KeyCode cancelKey = KeyCode.Escape;

        [Header("Placement")]
        [Tooltip("바닥으로 인식할 레이어. 아무것도 안 맞으면 네트워크 높이의 평면을 쓴다.")]
        [SerializeField] LayerMask groundMask = ~0;
        [SerializeField, Min(1f)] float maxRayDistance = 500f;
        [Tooltip("0이면 스냅 없음.")]
        [SerializeField, Min(0f)] float gridSize = 1f;
        [SerializeField, Min(.05f)] float nodeSnapRadius = .6f;
        [SerializeField, Min(.05f)] float segmentSnapRadius = .45f;
        [SerializeField, Min(.1f)] float minSegmentLength = .75f;

        [Header("Vehicle")]
        [Tooltip("비워두면 큐브를 만든다. TrackFollower가 없으면 붙인다.")]
        [SerializeField] GameObject vehiclePrefab;
        [SerializeField] Vector3 cubeSize = new Vector3(.4f, .3f, .6f);
        [SerializeField, Min(0f)] float vehicleSpeed = 3f;
        [SerializeField] TrackFollower.RoutingMode vehicleRouting = TrackFollower.RoutingMode.Random;

        [Header("Preview")]
        [SerializeField] Color validColor = new Color(.3f, .9f, 1f, .9f);
        [SerializeField] Color invalidColor = new Color(1f, .3f, .2f, .9f);
        [SerializeField] Color deleteColor = new Color(1f, .15f, .1f, 1f);
        [SerializeField] bool showHelp = true;

        public event Action<TrackFollower> VehicleSpawned;

        public TrackNetwork Network => network;
        public bool BuildModeActive
        {
            get => buildModeActive;
            set
            {
                buildModeActive = value;
                CancelChain();
                if (!value) HidePreview();
            }
        }

        enum TargetKind { Node, Segment, Free }

        struct Target
        {
            public TargetKind kind;
            public int nodeId;
            public TrackLocation location;
            public Vector3 position;
        }

        // 체인 시작점은 두 번째 클릭 전까지 실제로 만들지 않는다(취소 시 찌꺼기 노드 방지).
        bool hasPendingStart;
        Target pendingStart;
        int chainNode = -1;

        LineRenderer line;
        Renderer marker;
        Material previewMaterial;
        readonly List<Vector3> points = new List<Vector3>();

        void Awake()
        {
            if (network == null)
#if UNITY_2023_1_OR_NEWER
                network = FindAnyObjectByType<TrackNetwork>();
#else
                network = FindObjectOfType<TrackNetwork>();
#endif
            CreatePreview();
        }

        void OnDestroy()
        {
            if (line != null) Destroy(line.gameObject);
            if (marker != null) Destroy(marker.gameObject);
            if (previewMaterial != null) Destroy(previewMaterial);
        }

        void Update()
        {
            if (TrackInput.GetKeyDown(toggleKey)) BuildModeActive = !buildModeActive;

            Camera cam = targetCamera != null ? targetCamera : Camera.main;
            if (!buildModeActive || network == null || cam == null)
            {
                HidePreview();
                return;
            }

            if (TrackInput.GetKeyDown(cancelKey) || TrackInput.GetMouseButtonDown(1)) CancelChain();
            if (chainNode >= 0 && !network.HasNode(chainNode)) chainNode = -1;

            if (!TryGetCursorPoint(cam, out Vector3 cursor))
            {
                HidePreview();
                return;
            }

            if (TrackInput.GetKey(deleteHoldKey))
            {
                CancelChain();
                UpdateDelete(cursor);
            }
            else UpdateBuild(cursor);

            if (TrackInput.GetKeyDown(spawnKey) &&
                network.TryGetNearestPoint(cursor, nodeSnapRadius * 3f, out TrackLocation loc))
                SpawnVehicle(loc.segmentId, loc.distance, Vector3.Dot(loc.tangent, cam.transform.forward) >= 0f);
        }

        #region Build

        void UpdateBuild(Vector3 cursor)
        {
            Target target = Resolve(cursor);
            bool hasAnchor = chainNode >= 0 || hasPendingStart;
            Vector3 anchor = chainNode >= 0 ? network.GetNodePosition(chainNode) : pendingStart.position;
            bool valid = !hasAnchor || IsValidTarget(target, anchor);

            ShowMarker(target.position, valid ? validColor : invalidColor, target.kind == TargetKind.Free ? .18f : .3f);
            if (hasAnchor) ShowLine(anchor, target.position, valid ? validColor : invalidColor);
            else HideLine();

            if (!TrackInput.GetMouseButtonDown(0)) return;

            if (!hasAnchor)
            {
                pendingStart = target;
                hasPendingStart = true;
                return;
            }
            if (!valid)
            {
                // 같은 자리를 다시 누르면 체인 종료.
                if (Vector3.Distance(anchor, target.position) < minSegmentLength) CancelChain();
                return;
            }

            if (hasPendingStart)
            {
                chainNode = Materialize(pendingStart);
                hasPendingStart = false;
                target = Resolve(cursor); // 시작점이 구간을 쪼갰을 수 있으니 다시 찾는다.
                if (!IsValidTarget(target, network.GetNodePosition(chainNode))) return;
            }

            int next = Materialize(target);
            network.Connect(chainNode, next);
            // 기존 노드에 연결했으면 체인을 끊는다(루프 닫기, 합류).
            chainNode = target.kind == TargetKind.Node ? -1 : next;
        }

        bool IsValidTarget(Target target, Vector3 anchor)
        {
            if (Vector3.Distance(anchor, target.position) < minSegmentLength) return false;
            if (chainNode < 0) return true;
            if (target.kind == TargetKind.Node)
                return target.nodeId != chainNode && network.FindSegmentBetween(chainNode, target.nodeId) < 0;
            if (target.kind == TargetKind.Segment && network.TryGetSegment(target.location.segmentId, out TrackSegment s))
                return s.nodeA != chainNode && s.nodeB != chainNode;
            return true;
        }

        Target Resolve(Vector3 cursor)
        {
            int node = network.FindNearestNode(cursor, nodeSnapRadius);
            if (node >= 0) return NodeTarget(node);

            if (network.TryGetNearestPoint(cursor, segmentSnapRadius, out TrackLocation loc))
            {
                float length = network.GetSegmentLength(loc.segmentId);
                float edge = minSegmentLength * .5f;
                if (loc.distance > edge && loc.distance < length - edge)
                    return new Target { kind = TargetKind.Segment, location = loc, position = loc.position, nodeId = -1 };
            }

            Vector3 snapped = Snap(cursor);
            node = network.FindNearestNode(snapped, Mathf.Max(.01f, gridSize * .25f));
            if (node >= 0) return NodeTarget(node);
            return new Target { kind = TargetKind.Free, position = snapped, nodeId = -1 };
        }

        Target NodeTarget(int node) =>
            new Target { kind = TargetKind.Node, nodeId = node, position = network.GetNodePosition(node) };

        int Materialize(Target t)
        {
            switch (t.kind)
            {
                case TargetKind.Node: return t.nodeId;
                case TargetKind.Segment: return network.SplitSegment(t.location.segmentId, t.location.distance);
                default: return network.AddNode(t.position);
            }
        }

        Vector3 Snap(Vector3 world)
        {
            if (gridSize <= 0f) return world;
            Transform nt = network.transform;
            Vector3 local = nt.InverseTransformPoint(world);
            local.x = Mathf.Round(local.x / gridSize) * gridSize;
            local.z = Mathf.Round(local.z / gridSize) * gridSize;
            return nt.TransformPoint(local);
        }

        void CancelChain()
        {
            chainNode = -1;
            hasPendingStart = false;
        }

        #endregion

        #region Delete

        void UpdateDelete(Vector3 cursor)
        {
            int node = network.FindNearestNode(cursor, nodeSnapRadius);
            if (node >= 0)
            {
                ShowMarker(network.GetNodePosition(node), deleteColor, .35f);
                HideLine();
                if (TrackInput.GetMouseButtonDown(0)) network.RemoveNode(node);
                return;
            }

            if (network.TryGetNearestPoint(cursor, segmentSnapRadius, out TrackLocation loc))
            {
                ShowMarker(loc.position, deleteColor, .2f);
                network.GetSegmentPoints(loc.segmentId, points);
                ShowPolyline(points, deleteColor);
                if (TrackInput.GetMouseButtonDown(0)) network.RemoveSegment(loc.segmentId);
                return;
            }

            HidePreview();
        }

        #endregion

        #region Vehicles

        /// <summary>구간 위에 차량을 만든다.</summary>
        public TrackFollower SpawnVehicle(int segmentId, float distance, bool towardB)
        {
            GameObject go;
            if (vehiclePrefab != null) go = Instantiate(vehiclePrefab);
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "TrackCube";
                // 콜라이더가 있으면 빌더 레이캐스트에 걸려 레일이 차량 위에 깔린다.
                Destroy(go.GetComponent<Collider>());
                go.transform.localScale = cubeSize;
                go.GetComponent<Renderer>().material.color = Color.HSVToRGB(UnityEngine.Random.value, .65f, .95f);
            }

            TrackFollower follower = go.GetComponent<TrackFollower>();
            if (follower == null) follower = go.AddComponent<TrackFollower>();
            follower.MaxSpeed = vehicleSpeed;
            follower.Routing = vehicleRouting;
            follower.PlaceAt(network, segmentId, distance, towardB);
            VehicleSpawned?.Invoke(follower);
            return follower;
        }

        #endregion

        #region Cursor & Preview

        bool TryGetCursorPoint(Camera cam, out Vector3 point)
        {
            Ray ray = cam.ScreenPointToRay(TrackInput.MousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance, groundMask, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                return true;
            }
            var plane = new Plane(network.transform.up, network.transform.position);
            if (plane.Raycast(ray, out float enter) && enter <= maxRayDistance)
            {
                point = ray.GetPoint(enter);
                return true;
            }
            point = default;
            return false;
        }

        void CreatePreview()
        {
            Shader shader = Shader.Find("Sprites/Default");
            previewMaterial = shader != null ? new Material(shader) : TrackNetwork.CreateColorMaterial(Color.white, "TrackPreview");

            var lineGo = new GameObject("TrackBuilder Preview Line");
            lineGo.transform.SetParent(transform, false);
            line = lineGo.AddComponent<LineRenderer>();
            line.sharedMaterial = previewMaterial;
            line.widthMultiplier = .08f;
            line.numCapVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;

            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "TrackBuilder Preview Marker";
            sphere.transform.SetParent(transform, false);
            Destroy(sphere.GetComponent<Collider>());
            marker = sphere.GetComponent<Renderer>();
            marker.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            HidePreview();
        }

        void ShowMarker(Vector3 position, Color color, float size)
        {
            marker.gameObject.SetActive(true);
            marker.transform.position = position + network.transform.up * network.RailTopHeight;
            marker.transform.localScale = Vector3.one * size;
            marker.material.color = color;
        }

        void ShowLine(Vector3 from, Vector3 to, Color color)
        {
            Vector3 lift = network.transform.up * (network.RailTopHeight + .02f);
            line.enabled = true;
            line.positionCount = 2;
            line.SetPosition(0, from + lift);
            line.SetPosition(1, to + lift);
            line.startColor = line.endColor = color;
        }

        void ShowPolyline(List<Vector3> pts, Color color)
        {
            Vector3 lift = network.transform.up * (network.RailTopHeight + .02f);
            line.enabled = true;
            line.positionCount = pts.Count;
            for (int i = 0; i < pts.Count; i++) line.SetPosition(i, pts[i] + lift);
            line.startColor = line.endColor = color;
        }

        void HideLine()
        {
            if (line != null) line.enabled = false;
        }

        void HidePreview()
        {
            HideLine();
            if (marker != null) marker.gameObject.SetActive(false);
        }

        void OnGUI()
        {
            if (!showHelp) return;
            string text = buildModeActive
                ? $"[{toggleKey}] 빌드 모드 ON\n좌클릭: 레일 깔기 / 이어가기 (레일 중간 클릭 = 분기)\n우클릭·{cancelKey}: 끊기\n{deleteHoldKey}+좌클릭: 삭제\n[{spawnKey}] 커서 근처 레일에 큐브 소환"
                : $"[{toggleKey}] 빌드 모드 OFF";
            GUI.Box(new Rect(10, 10, 330, buildModeActive ? 100 : 26), GUIContent.none);
            GUI.Label(new Rect(18, 13, 320, 100), text);
        }

        #endregion
    }
}
