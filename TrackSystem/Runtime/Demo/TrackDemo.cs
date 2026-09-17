using UnityEngine;

namespace TrackSystem.Demo
{
    /// <summary>
    /// 빈 씬의 오브젝트 하나에 붙이고 Play하면 바닥, 조명, 카메라, 샘플 레일(분기 포함), 큐브 차량을 만든다.
    /// 이미 씬에 TrackNetwork/TrackBuilder/카메라가 있으면 그것을 쓴다.
    /// </summary>
    public sealed class TrackDemo : MonoBehaviour
    {
        [SerializeField] bool createGround = true;
        [SerializeField] bool createSampleLayout = true;
        [SerializeField, Min(0)] int vehicleCount = 4;

        void Start()
        {
            if (createGround && GameObject.Find("TrackDemo Ground") == null)
            {
                GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "TrackDemo Ground";
                ground.transform.localScale = new Vector3(6f, 1f, 6f);
                ground.GetComponent<Renderer>().material = TrackNetwork.CreateColorMaterial(new Color(.32f, .36f, .3f), "TrackDemoGround");
            }

#if UNITY_2023_1_OR_NEWER
            TrackNetwork network = FindAnyObjectByType<TrackNetwork>();
            TrackBuilder builder = FindAnyObjectByType<TrackBuilder>();
            Light sun = FindAnyObjectByType<Light>();
#else
            TrackNetwork network = FindObjectOfType<TrackNetwork>();
            TrackBuilder builder = FindObjectOfType<TrackBuilder>();
            Light sun = FindObjectOfType<Light>();
#endif
            if (network == null) network = new GameObject("TrackNetwork").AddComponent<TrackNetwork>();
            if (builder == null) builder = new GameObject("TrackBuilder").AddComponent<TrackBuilder>();

            if (sun == null)
            {
                sun = new GameObject("Sun").AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.shadows = LightShadows.Soft;
                sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
            }
            if (cam.GetComponent<TrackDemoCamera>() == null) cam.gameObject.AddComponent<TrackDemoCamera>();

            if (createSampleLayout && network.Nodes.Count == 0) BuildSample(network);

            var segments = network.Segments;
            for (int i = 0; i < vehicleCount && segments.Count > 0; i++)
            {
                TrackSegment s = segments[i * segments.Count / vehicleCount];
                builder.SpawnVehicle(s.id, network.GetSegmentLength(s.id) * .5f, true);
            }
        }

        /// <summary>모서리를 깎은 한 방향 순환선 + 위/아래 대피선(분기 4개). 모든 구간이 반시계 방향으로 이어진다.</summary>
        static void BuildSample(TrackNetwork net)
        {
            int N(float x, float z) => net.AddNode(net.transform.TransformPoint(new Vector3(x, 0f, z)));

            int[] loop =
            {
                N(-10, -4), N(-8, -6), N(-3, -6), N(3, -6), N(8, -6),
                N(10, -4), N(10, 4), N(8, 6), N(-8, 6), N(-10, 4),
            };
            for (int i = 0; i < loop.Length; i++) net.Connect(loop[i], loop[(i + 1) % loop.Length]);

            // 대피선: (-3,-6) → (-1,-3.5) → (1,-3.5) → (3,-6)
            int s1 = N(-1, -3.5f), s2 = N(1, -3.5f);
            net.Connect(loop[2], s1);
            net.Connect(s1, s2);
            net.Connect(s2, loop[3]);

            // 위쪽 대피선: 기존 선로 중간을 쪼개 분기를 만든다(유저가 레일 중간을 클릭할 때와 같은 방식).
            int t1 = net.SplitSegment(net.FindSegmentBetween(loop[7], loop[8]), 5f);  // (3, 6)
            int t2 = net.SplitSegment(net.FindSegmentBetween(t1, loop[8]), 6f);       // (-3, 6)
            int u1 = N(1, 8.5f), u2 = N(-1, 8.5f);
            net.Connect(t1, u1);
            net.Connect(u1, u2);
            net.Connect(u2, t2);
        }
    }
}
