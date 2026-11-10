#if UNITY_EDITOR
using System;
using System.Collections;
using UnityEngine;

// Transient Play-mode host for end-of-frame capture. Never saved into a scene.
public sealed class GameViewVideoRunner : MonoBehaviour
{
    private Action captureFrame;
    private Action<GameViewVideoRunner> destroyed;
    private bool capturing;

    public void StartCapture(Action onFrame, Action<GameViewVideoRunner> onDestroyed)
    {
        captureFrame = onFrame;
        destroyed = onDestroyed;
        capturing = true;
        StartCoroutine(Capture());
    }

    public void StopCapture()
    {
        capturing = false;
        captureFrame = null;
    }

    private IEnumerator Capture()
    {
        var endOfFrame = new WaitForEndOfFrame();
        while (capturing)
        {
            yield return endOfFrame;
            if (capturing) captureFrame?.Invoke();
        }
    }

    private void OnDestroy()
    {
        StopCapture();
        destroyed?.Invoke(this);
        destroyed = null;
    }
}
#endif
