using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public sealed class UwrSpike : MonoBehaviour
{
    [SerializeField] private string url = "http://127.0.0.1:5199/sse";
    private UnityWebRequest _request;

    private IEnumerator Start()
    {
        var handler = new StreamingDownloadHandler();
        _request = new UnityWebRequest(url, "POST");
        _request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
        _request.downloadHandler = handler;
        _request.SetRequestHeader("Content-Type", "application/json");
        _request.SetRequestHeader("Accept", "text/event-stream");
        _request.timeout = 0;

        yield return _request.SendWebRequest();

        Debug.Log($"result={_request.result} code={_request.responseCode} " +
                  $"error={_request.error} callbacks={handler.CallbackCount} " +
                  $"first={(handler.Chunks.Count > 0 ? handler.Chunks[0].t : -1):F3}s");
        foreach (var c in handler.Chunks) Debug.Log($"chunk t={c.t:F3}s bytes={c.bytes}");
    }

    public void AbortNow() => _request?.Abort();
}
