using UnityEngine;

namespace Rvo
{
    public sealed class VolumeCameraControls : MonoBehaviour
    {
        public Vector3 Pivot;
        public bool Following { get; private set; }
        private Vector3 followTarget;
        public void BeginFollow(Vector3 position) { Following = true; followTarget = Pivot = position; }
        public void UpdateFollowTarget(Vector3 position) { followTarget = position; }
        public void StopFollowing() { Following = false; }
        private void LateUpdate()
        {
            if (!Following) return;
            Vector3 next = Vector3.Lerp(Pivot, followTarget, 1 - Mathf.Exp(-15 * Time.unscaledDeltaTime));
            transform.position += next - Pivot; Pivot = next;
        }
        private void OnGUI()
        {
            var view = GetComponent<Camera>(); var e = Event.current;
            if (!view.pixelRect.Contains(new Vector2(e.mousePosition.x, Screen.height - e.mousePosition.y))) return;
            Vector2 delta = e.delta;
            if (e.type == EventType.MouseDrag && e.button == 1)
            {
                transform.RotateAround(Pivot, Vector3.up, delta.x * 0.2f);
                transform.RotateAround(Pivot, transform.right, delta.y * 0.2f); e.Use();
            }
            if (e.type == EventType.MouseDrag && e.button == 2)
            {
                StopFollowing();
                Vector3 offset = (-transform.right * delta.x + transform.up * delta.y) * Vector3.Distance(transform.position, Pivot) * 0.001f;
                transform.position += offset; Pivot += offset; e.Use();
            }
            if (e.type == EventType.ScrollWheel)
            { transform.position = Pivot + (transform.position - Pivot).normalized * Mathf.Clamp(Vector3.Distance(transform.position, Pivot) * Mathf.Exp(e.delta.y * 0.08f), 1, 3000); e.Use(); }
        }
    }
}
