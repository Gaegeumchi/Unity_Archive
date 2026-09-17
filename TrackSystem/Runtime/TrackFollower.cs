using System;
using System.Collections.Generic;
using UnityEngine;

namespace TrackSystem
{
    /// <summary>
    /// TrackNetwork 레일을 따라 움직이는 차량. 아무 오브젝트(큐브, 열차 모델, OHT 등)에 붙이면 된다.
    /// 노드에서는 진행 방향 기준 maxTurnAngle 이내로 이어지는 구간만 탈 수 있고,
    /// 갈 곳이 없으면 멈추거나(옵션) 방향을 바꿔 되돌아간다. 앞차와의 간격도 유지한다.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TrackFollower : MonoBehaviour
    {
        public enum RoutingMode
        {
            /// <summary>가장 곧게 이어지는 구간.</summary>
            Straightest,
            /// <summary>갈 수 있는 구간 중 무작위.</summary>
            Random,
            /// <summary>destinationNode까지 최단 경로.</summary>
            Destination,
        }

        const int Unknown = -2;
        const int NoExit = -1;

        static readonly List<TrackFollower> active = new List<TrackFollower>();
        static int serialCounter;
        static readonly List<int> exitBuffer = new List<int>();
        public static IReadOnlyList<TrackFollower> Active => active;

        [SerializeField] TrackNetwork network;

        [Header("Motion")]
        [SerializeField, Min(0f)] float maxSpeed = 3f;
        [SerializeField, Min(.01f)] float acceleration = 3f;
        [SerializeField, Min(.01f)] float braking = 6f;
        [Tooltip("레일 윗면에서 차량 피벗까지의 높이.")]
        [SerializeField] float heightOffset = .15f;

        [Header("Routing")]
        [SerializeField] RoutingMode routing = RoutingMode.Straightest;
        [SerializeField, Range(5f, 170f)] float maxTurnAngle = 70f;
        [SerializeField] bool reverseAtDeadEnd = true;
        [SerializeField] int destinationNode = -1;

        [Header("Spacing")]
        [SerializeField] bool keepDistance = true;
        [Tooltip("앞차 중심까지 유지할 거리.")]
        [SerializeField, Min(0f)] float safeDistance = 1f;

        [Header("Start")]
        [Tooltip("시작 시 현재 위치에서 가장 가까운 레일로 스냅한다.")]
        [SerializeField] bool snapOnStart = true;
        [SerializeField, Min(0f)] float snapRadius = 3f;

        int segmentId = -1;
        float distance;
        bool forward = true;
        float speed;
        int pendingExit = Unknown;
        int seenVersion = -1;
        bool halted;
        bool arrived;
        readonly int serial = ++serialCounter; // 정면 충돌 시 양보 순서 (Unity 버전별 InstanceID API 차이 회피)

        /// <summary>노드를 지날 때(노드 id).</summary>
        public event Action<TrackFollower, int> NodePassed;
        public event Action<TrackFollower> DestinationReached;

        public TrackNetwork Network => network;
        public bool IsOnTrack => network != null && segmentId >= 0 && network.HasSegment(segmentId);
        public int SegmentId => segmentId;
        public float DistanceOnSegment => distance;
        public bool MovingTowardB => forward;
        public float Speed => speed;
        public bool IsHalted => halted || arrived;
        public Vector3 TrackPosition { get; private set; }
        public Vector3 TravelDirection { get; private set; } = Vector3.forward;

        public float MaxSpeed { get => maxSpeed; set => maxSpeed = Mathf.Max(0f, value); }
        public RoutingMode Routing { get => routing; set { routing = value; arrived = false; ResetDecision(); } }
        public int DestinationNode => destinationNode;

        void OnEnable() => active.Add(this);
        void OnDisable() => active.Remove(this);

        void Start()
        {
            if (network == null)
#if UNITY_2023_1_OR_NEWER
                network = FindAnyObjectByType<TrackNetwork>();
#else
                network = FindObjectOfType<TrackNetwork>();
#endif
            if (snapOnStart && network != null && !IsOnTrack) SnapToNearest(transform.position, snapRadius);
        }

        #region Public API

        /// <summary>구간 위 지정 위치에 올린다. towardB = nodeA→nodeB 방향으로 진행.</summary>
        public void PlaceAt(TrackNetwork trackNetwork, int segment, float distanceFromA, bool towardB)
        {
            network = trackNetwork;
            segmentId = segment;
            distance = distanceFromA;
            forward = towardB;
            speed = 0f;
            seenVersion = network.Version;
            ResetDecision();
            ApplyTransform();
        }

        /// <summary>가장 가까운 레일에 올린다. 현재 transform.forward에 가까운 방향으로 진행.</summary>
        public bool SnapToNearest(Vector3 worldPosition, float radius)
        {
            if (network == null || !network.TryGetNearestPoint(worldPosition, radius, out TrackLocation loc)) return false;
            PlaceAt(network, loc.segmentId, loc.distance, Vector3.Dot(transform.forward, loc.tangent) >= 0f);
            return true;
        }

        public void SetDestination(int nodeId)
        {
            destinationNode = nodeId;
            routing = RoutingMode.Destination;
            arrived = false;
            ResetDecision();
        }

        public void Reverse()
        {
            forward = !forward;
            speed = 0f;
            ResetDecision();
        }

        #endregion

        void ResetDecision()
        {
            pendingExit = Unknown;
            halted = false;
        }

        void Update()
        {
            if (network == null) return;

            if (seenVersion != network.Version)
            {
                seenVersion = network.Version;
                ResetDecision();
                // 타고 있던 구간이 지워졌으면 근처 레일로 옮겨 타고, 없으면 멈춘다.
                if (segmentId >= 0 && !network.HasSegment(segmentId) && !SnapToNearest(TrackPosition, snapRadius))
                    segmentId = -1;
            }
            if (segmentId < 0) return;

            float dt = Time.deltaTime;
            float length = network.GetSegmentLength(segmentId);
            distance = Mathf.Clamp(distance, 0f, length);

            if (halted || arrived)
            {
                speed = 0f;
                ApplyTransform();
                return;
            }

            int endNode = EndNode();
            if (pendingExit == Unknown) pendingExit = ChooseExit(endNode);
            float remaining = forward ? length - distance : distance;

            float target = maxSpeed;
            if (ShouldStopAt(endNode))
                target = Mathf.Min(target, Mathf.Sqrt(2f * braking * remaining) + .1f); // 끝에 정확히 서도록 감속
            if (keepDistance) target = Mathf.Min(target, SpacingLimit());

            speed = Mathf.MoveTowards(speed, target, (speed < target ? acceleration : braking) * dt);
            Advance(speed * dt);
            ApplyTransform();
        }

        void Advance(float move)
        {
            for (int guard = 0; move > 0f && guard < 32; guard++)
            {
                float length = network.GetSegmentLength(segmentId);
                float remaining = forward ? length - distance : distance;
                if (move < remaining)
                {
                    distance += forward ? move : -move;
                    return;
                }

                move -= remaining;
                distance = forward ? length : 0f;
                int node = EndNode();
                if (pendingExit == Unknown) pendingExit = ChooseExit(node);
                NodePassed?.Invoke(this, node);

                if (ShouldStopAt(node))
                {
                    speed = 0f;
                    if (routing == RoutingMode.Destination && node == destinationNode)
                    {
                        arrived = true;
                        DestinationReached?.Invoke(this);
                    }
                    else if (reverseAtDeadEnd) Reverse();
                    else halted = true;
                    return;
                }

                int next = pendingExit;
                if (!network.TryGetSegment(next, out TrackSegment s)) return;
                segmentId = next;
                forward = s.nodeA == node;
                distance = forward ? 0f : network.GetSegmentLength(next);
                pendingExit = Unknown;
            }
        }

        bool ShouldStopAt(int node) =>
            pendingExit == NoExit || (routing == RoutingMode.Destination && node == destinationNode);

        int EndNode()
        {
            network.TryGetSegment(segmentId, out TrackSegment s);
            return forward ? s.nodeB : s.nodeA;
        }

        int ChooseExit(int node)
        {
            network.GetAllowedExits(segmentId, node, maxTurnAngle, exitBuffer);
            if (exitBuffer.Count == 0) return NoExit;

            switch (routing)
            {
                case RoutingMode.Random:
                    return exitBuffer[UnityEngine.Random.Range(0, exitBuffer.Count)];
                case RoutingMode.Destination when destinationNode >= 0 && node != destinationNode:
                    int routed = network.FindRouteExit(segmentId, node, destinationNode, maxTurnAngle);
                    if (routed >= 0) return routed;
                    break; // 도달 불가면 가장 곧은 쪽으로
            }

            Vector3 travel = -network.GetExitDirection(segmentId, node);
            int best = exitBuffer[0];
            float bestDot = float.MinValue;
            foreach (int s in exitBuffer)
            {
                float d = Vector3.Dot(travel, network.GetExitDirection(s, node));
                if (d > bestDot) { bestDot = d; best = s; }
            }
            return best;
        }

        /// <summary>앞차까지 거리로 낼 수 있는 최대 속도.</summary>
        float SpacingLimit()
        {
            float limit = float.MaxValue;
            foreach (TrackFollower other in active)
            {
                if (other == this || other.network != network || other.segmentId < 0) continue;
                Vector3 to = other.TrackPosition - TrackPosition;
                float along = Vector3.Dot(to, TravelDirection);
                if (along <= 0f || (to - TravelDirection * along).magnitude > safeDistance * .6f) continue;

                float gap = along - safeDistance;
                if (gap <= 0f)
                {
                    // 정면으로 마주쳐 서로 막혔으면 뒤가 비어 있는 쪽이 양보(후진)한다. 둘 다 비었으면 먼저 생긴 쪽.
                    if (reverseAtDeadEnd && Vector3.Dot(other.TravelDirection, TravelDirection) < -.5f)
                    {
                        bool myBackClear = IsClear(-TravelDirection);
                        bool otherBackClear = other.reverseAtDeadEnd && other.IsClear(-other.TravelDirection);
                        if (myBackClear && (!otherBackClear || serial < other.serial))
                        {
                            Reverse();
                            return float.MaxValue;
                        }
                    }
                    return 0f;
                }
                limit = Mathf.Min(limit, Mathf.Sqrt(2f * braking * gap));
            }
            return limit;
        }

        /// <summary>direction 쪽 safeDistance*1.5 안에 다른 차량이 없는가.</summary>
        bool IsClear(Vector3 direction)
        {
            foreach (TrackFollower other in active)
            {
                if (other == this || other.network != network || other.segmentId < 0) continue;
                Vector3 to = other.TrackPosition - TrackPosition;
                float along = Vector3.Dot(to, direction);
                if (along > 0f && along < safeDistance * 1.5f && (to - direction * along).magnitude <= safeDistance * .6f)
                    return false;
            }
            return true;
        }

        void ApplyTransform()
        {
            if (network == null || !network.Evaluate(segmentId, distance, out Vector3 pos, out Vector3 tangent)) return;
            TrackPosition = pos;
            TravelDirection = forward ? tangent : -tangent;
            Vector3 up = network.transform.up;
            transform.SetPositionAndRotation(pos + up * (network.RailTopHeight + heightOffset),
                Quaternion.LookRotation(TravelDirection, up));
        }
    }
}
