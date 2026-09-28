using UnityEngine;

namespace Rvo
{
    /// <summary>IMGUI 事件同时兼容项目的新/旧输入设置；只响应相机视口，避开左侧 HUD。</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class NavigationCameraControls : MonoBehaviour
    {
        private Camera view;
        private void Awake() { view = GetComponent<Camera>(); }
        private void OnGUI()
        {
            if (view == null || !view.orthographic) return;
            var e = Event.current;
            Vector2 screen = new Vector2(e.mousePosition.x, Screen.height - e.mousePosition.y);
            if (!view.pixelRect.Contains(screen)) return;
            if (e.type == EventType.ScrollWheel)
            {
                float before = view.orthographicSize;
                view.orthographicSize = Mathf.Clamp(before * Mathf.Exp(e.delta.y * 0.08f), 5, 1000);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && e.button == 2)
            {
                float scale = view.orthographicSize * 2 / Mathf.Max(1, view.pixelHeight);
                view.transform.position += new Vector3(-e.delta.x, 0, e.delta.y) * scale;
                e.Use();
            }
        }
    }
}
