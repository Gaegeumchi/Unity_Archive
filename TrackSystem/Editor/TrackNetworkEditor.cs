using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TrackSystem.Editor
{
    /// <summary>씬 뷰에서 레일을 편집한다. 인스펙터의 "씬 뷰 편집" 버튼으로 켠다.</summary>
    [CustomEditor(typeof(TrackNetwork))]
    public sealed class TrackNetworkEditor : UnityEditor.Editor
    {
        static bool editing;
        static float gridSize = 1f;

        int selectedNode = -1;
        readonly List<Vector3> points = new List<Vector3>();

        TrackNetwork Network => (TrackNetwork)target;

        void OnEnable() => Undo.undoRedoPerformed += OnUndoRedo;

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            Tools.hidden = false;
        }

        void OnUndoRedo()
        {
            if (target != null) Network.MarkDirty();
        }

        public override void OnInspectorGUI()
        {
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck()) Network.MarkDirty();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"노드 {Network.Nodes.Count}개 · 구간 {Network.Segments.Count}개", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            editing = GUILayout.Toggle(editing, "씬 뷰 편집", "Button", GUILayout.Height(24));
            if (EditorGUI.EndChangeCheck())
            {
                Tools.hidden = editing;
                SceneView.RepaintAll();
            }

            if (editing)
            {
                gridSize = EditorGUILayout.FloatField("그리드 스냅 (0=끔)", gridSize);
                EditorGUILayout.HelpBox(
                    "Shift+클릭: 노드 추가 (선택된 노드와 자동 연결)\n" +
                    "노드 클릭: 선택  ·  Ctrl+노드 클릭: 선택 노드와 연결/연결 해제\n" +
                    "선택 노드는 이동 핸들로 드래그\n" +
                    "Delete: 선택 노드 삭제  ·  Esc: 선택 해제", MessageType.Info);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("메시 다시 만들기")) Network.RebuildMeshNow();
                if (GUILayout.Button("모두 지우기"))
                {
                    Undo.RecordObject(Network, "Clear Track");
                    Network.Clear();
                    selectedNode = -1;
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("JSON 복사")) EditorGUIUtility.systemCopyBuffer = Network.ToJson(true);
                if (GUILayout.Button("JSON 붙여넣기"))
                {
                    Undo.RecordObject(Network, "Paste Track");
                    Network.LoadJson(EditorGUIUtility.systemCopyBuffer);
                }
            }
        }

        void OnSceneGUI()
        {
            TrackNetwork net = Network;
            Event e = Event.current;

            Handles.color = new Color(1f, .8f, .2f, .9f);
            foreach (TrackSegment s in net.Segments)
            {
                net.GetSegmentPoints(s.id, points);
                if (points.Count > 1) Handles.DrawAAPolyLine(3f, points.ToArray());
            }

            if (!editing) return;

            int passive = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(passive);

            if (selectedNode >= 0 && !net.HasNode(selectedNode)) selectedNode = -1;

            foreach (TrackNode node in net.Nodes)
            {
                Vector3 pos = net.transform.TransformPoint(node.position);
                float size = HandleUtility.GetHandleSize(pos) * .12f;
                Handles.color = node.id == selectedNode ? Color.yellow : new Color(.3f, .9f, 1f);
                if (!Handles.Button(pos, Quaternion.identity, size, size * 1.3f, Handles.SphereHandleCap)) continue;

                if (e.control && selectedNode >= 0 && selectedNode != node.id)
                {
                    Undo.RecordObject(net, "Toggle Track Segment");
                    int existing = net.FindSegmentBetween(selectedNode, node.id);
                    if (existing >= 0) net.RemoveSegment(existing, false);
                    else net.Connect(selectedNode, node.id);
                }
                selectedNode = node.id;
                Repaint();
            }

            if (selectedNode >= 0)
            {
                Vector3 pos = net.GetNodePosition(selectedNode);
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.PositionHandle(pos, net.transform.rotation);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(net, "Move Track Node");
                    net.MoveNode(selectedNode, Snap(net, moved));
                }
            }

            if (e.type == EventType.MouseDown && e.button == 0 && e.shift && !e.alt &&
                HandleUtility.nearestControl == passive)
            {
                if (TryGetClickPoint(net, e.mousePosition, out Vector3 point))
                {
                    Undo.RecordObject(net, "Add Track Node");
                    int id = net.AddNode(Snap(net, point));
                    if (selectedNode >= 0) net.Connect(selectedNode, id);
                    selectedNode = id;
                    Repaint();
                }
                e.Use();
            }

            if (e.type == EventType.KeyDown)
            {
                if ((e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) && selectedNode >= 0)
                {
                    Undo.RecordObject(net, "Delete Track Node");
                    net.RemoveNode(selectedNode);
                    selectedNode = -1;
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Escape && selectedNode >= 0)
                {
                    selectedNode = -1;
                    e.Use();
                }
            }
        }

        static bool TryGetClickPoint(TrackNetwork net, Vector2 mousePosition, out Vector3 point)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, 10000f, ~0, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                return true;
            }
            var plane = new Plane(net.transform.up, net.transform.position);
            if (plane.Raycast(ray, out float enter))
            {
                point = ray.GetPoint(enter);
                return true;
            }
            point = default;
            return false;
        }

        static Vector3 Snap(TrackNetwork net, Vector3 world)
        {
            if (gridSize <= 0f) return world;
            Vector3 local = net.transform.InverseTransformPoint(world);
            local.x = Mathf.Round(local.x / gridSize) * gridSize;
            local.z = Mathf.Round(local.z / gridSize) * gridSize;
            return net.transform.TransformPoint(local);
        }
    }
}
