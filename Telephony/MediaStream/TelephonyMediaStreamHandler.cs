using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PruvaVoice.Api.Telephony.MediaStream;

/// <summary>
/// Real-time bidirectional WebSocket handler for carrier telephony media streams (Twilio Media Streams / Exotel Voice Streams).
/// Bridges 8kHz μ-law audio over WebSocket to linear PCM, with instant barge-in buffer flushing.
/// </summary>
public class TelephonyMediaStreamHandler
{
    private readonly ILogger<TelephonyMediaStreamHandler> _logger;

    // Active media stream sessions indexed by callSessionId
    private static readonly ConcurrentDictionary<string, ActiveMediaSession> ActiveSessions = new();

    public TelephonyMediaStreamHandler(ILogger<TelephonyMediaStreamHandler> logger)
    {
        _logger = logger;
    }

    public static ActiveMediaSession? GetActiveSession(string callSessionId)
    {
        ActiveSessions.TryGetValue(callSessionId, out var session);
        return session;
    }

    public async Task HandleWebSocketAsync(HttpContext context, string callSessionId)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connection expected.");
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        _logger.LogInformation("[MediaStream] Carrier WebSocket connected for CallSessionId: {CallSessionId}", callSessionId);

        var session = new ActiveMediaSession(callSessionId, webSocket, _logger);
        ActiveSessions[callSessionId] = session;

        var buffer = new byte[1024 * 16];
        var memoryStream = new MemoryStream();

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by carrier", CancellationToken.None);
                    break;
                }

                memoryStream.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    var messageJson = Encoding.UTF8.GetString(memoryStream.ToArray());
                    memoryStream.SetLength(0);

                    await session.ProcessCarrierMessageAsync(messageJson);
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning("[MediaStream] WebSocket closed or aborted for CallSessionId {CallSessionId}: {Message}", callSessionId, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MediaStream] Unexpected error in MediaStream for CallSessionId {CallSessionId}", callSessionId);
        }
        finally
        {
            ActiveSessions.TryRemove(callSessionId, out _);
            session.Dispose();
            _logger.LogInformation("[MediaStream] Session cleaned up for CallSessionId: {CallSessionId}", callSessionId);
        }
    }
}

/// <summary>
/// Represents a live bidirectional audio session with a telephony carrier stream.
/// </summary>
public class ActiveMediaSession : IDisposable
{
    public string CallSessionId { get; }
    public string? StreamSid { get; private set; }
    public string? CarrierCallSid { get; private set; }
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public long InboundBytesReceived { get; private set; }
    public long OutboundBytesSent { get; private set; }
    public bool IsActive => _webSocket.State == WebSocketState.Open;

    private readonly WebSocket _webSocket;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action<short[]>? OnPcmAudioReceived;
    public event Action<AmdResult>? OnAmdResolved;
    public event Action? OnCallEnded;
    private readonly AnsweringMachineDetector _amd = new();

    public AmdResult CurrentAmdStatus => _amd.ProcessPcmFrame(Array.Empty<short>(), 0);

    public ActiveMediaSession(string callSessionId, WebSocket webSocket, ILogger logger)
    {
        CallSessionId = callSessionId;
        _webSocket = webSocket;
        _logger = logger;
    }

    public async Task ProcessCarrierMessageAsync(string jsonString)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var eventProp)) return;

            var eventName = eventProp.GetString();

            switch (eventName)
            {
                case "connected":
                    _logger.LogInformation("[MediaStream] Handshake 'connected' received for {CallSessionId}", CallSessionId);
                    break;

                case "start":
                    if (root.TryGetProperty("streamSid", out var streamSidProp))
                        StreamSid = streamSidProp.GetString();

                    if (root.TryGetProperty("start", out var startObj))
                    {
                        if (startObj.TryGetProperty("callSid", out var callSidProp))
                            CarrierCallSid = callSidProp.GetString();
                    }

                    _logger.LogInformation("[MediaStream] Audio Stream started: StreamSid={StreamSid}, CallSid={CallSid}", StreamSid, CarrierCallSid);
                    break;

                case "media":
                    if (root.TryGetProperty("media", out var mediaObj) &&
                        mediaObj.TryGetProperty("payload", out var payloadProp))
                    {
                        var base64 = payloadProp.GetString();
                        if (!string.IsNullOrEmpty(base64))
                        {
                            var muLawBytes = Convert.FromBase64String(base64);
                            InboundBytesReceived += muLawBytes.Length;

                            // Decode μ-law (8kHz) to 16-bit linear PCM (8kHz)
                            var pcm8k = MuLawCodec.DecodeMuLawToPcm(muLawBytes);

                            if (!_amd.IsResolved)
                            {
                                var amdResult = _amd.ProcessPcmFrame(pcm8k, 20);
                                if (_amd.IsResolved)
                                {
                                    _logger.LogInformation("[AMD] CallSession {CallSessionId} classified: {Status} (Confidence: {Confidence:P0}, Reason: {Reason})",
                                        CallSessionId, amdResult.Status, amdResult.Confidence, amdResult.Reason);
                                    OnAmdResolved?.Invoke(amdResult);
                                }
                            }

                            OnPcmAudioReceived?.Invoke(pcm8k);
                        }
                    }
                    break;

                case "mark":
                    // Carrier completed playback of marked audio buffer chunk
                    if (root.TryGetProperty("mark", out var markObj) && markObj.TryGetProperty("name", out var markName))
                    {
                        _logger.LogDebug("[MediaStream] Mark reached: {MarkName}", markName.GetString());
                    }
                    break;

                case "stop":
                    _logger.LogInformation("[MediaStream] Stream stopped by carrier for {CallSessionId}", CallSessionId);
                    OnCallEnded?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MediaStream] Error parsing carrier JSON message");
        }
    }

    /// <summary>
    /// Sends an 8kHz μ-law audio packet to the carrier phone call.
    /// </summary>
    public async Task SendMuLawAudioAsync(ReadOnlyMemory<byte> muLawAudio)
    {
        if (_webSocket.State != WebSocketState.Open || string.IsNullOrEmpty(StreamSid)) return;

        var base64Payload = Convert.ToBase64String(muLawAudio.Span);
        var mediaMessage = new
        {
            @event = "media",
            streamSid = StreamSid,
            media = new
            {
                payload = base64Payload
            }
        };

        var json = JsonSerializer.Serialize(mediaMessage);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync();
        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                OutboundBytesSent += muLawAudio.Length;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends a linear PCM audio chunk (transcoded to μ-law) to the carrier phone call.
    /// </summary>
    public Task SendPcmAudioAsync(ReadOnlySpan<short> pcm8k)
    {
        var muLawBytes = MuLawCodec.EncodePcmToMuLaw(pcm8k);
        return SendMuLawAudioAsync(muLawBytes);
    }

    /// <summary>
    /// Instantly clears pending audio buffers on the carrier trunk (Twilio / Exotel).
    /// Used for sub-100ms barge-in cutoff when the human begins speaking.
    /// </summary>
    public async Task ClearCarrierAudioBufferAsync()
    {
        if (_webSocket.State != WebSocketState.Open || string.IsNullOrEmpty(StreamSid)) return;

        var clearMessage = new
        {
            @event = "clear",
            streamSid = StreamSid
        };

        var json = JsonSerializer.Serialize(clearMessage);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync();
        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                _logger.LogInformation("[MediaStream] Sent 'clear' buffer command for StreamSid: {StreamSid}", StreamSid);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        _sendLock.Dispose();
    }
}
