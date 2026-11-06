using System.Collections.Generic;
using UnityEngine.Networking;

public sealed class StreamingDownloadHandler : DownloadHandlerScript
{
    public readonly List<(double t, int bytes)> Chunks = new();
    public int CallbackCount;
    private readonly double _start = UnityEngine.Time.realtimeSinceStartupAsDouble;

    public StreamingDownloadHandler() : base(new byte[16384]) { }

    protected override bool ReceiveData(byte[] data, int dataLength)
    {
        CallbackCount++;
        Chunks.Add((UnityEngine.Time.realtimeSinceStartupAsDouble - _start, dataLength));
        return true;
    }
}
