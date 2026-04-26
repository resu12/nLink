using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal sealed unsafe class FfmpegH264BitmapDecoder : IWindowsH264BitmapDecoder
{
    private static int nextDecoderId;

    private readonly string logRole;
    private readonly int decoderId;
    private WindowsH264DecodePreparation.DecoderConfiguration? configuration;
    private ScreenShareVideoStreamConfigV1? activeConfig;
    private AVCodecContext* codecContext;
    private SwsContext* swsContext;
    private AVPacket* reusablePacket;
    private AVFrame* reusableFrame;
    private byte[]? reusableBgraBuffer;
    private int reusableBgraWidth;
    private int reusableBgraHeight;
    private int reusableBgraStride;
    private readonly Queue<Bitmap> pendingDecodedBitmaps = new();
    private bool prependDecoderConfigOnNextPacket = true;
    private bool disposed;
    private bool firstDecodeLogged;

    private FfmpegH264BitmapDecoder(string logRole, int decoderId)
    {
        this.logRole = string.IsNullOrWhiteSpace(logRole) ? "viewer" : logRole.Trim();
        this.decoderId = decoderId;
    }

    public bool IsSupported => !disposed && WindowsFfmpegRuntime.TryInitialize();

    internal static string DebugNativeLibrariesPath => WindowsFfmpegRuntime.DebugNativeLibrariesPath;

    internal static string DebugNativeInitializationFailure => WindowsFfmpegRuntime.DebugNativeInitializationFailure;

    internal static string DebugNativeSearchPaths => WindowsFfmpegRuntime.DebugNativeSearchPaths;

    public static IWindowsH264BitmapDecoder? TryCreate(string logRole = "viewer")
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!WindowsFfmpegRuntime.TryInitialize())
        {
            return null;
        }

        var decoderId = System.Threading.Interlocked.Increment(ref nextDecoderId);
        return new FfmpegH264BitmapDecoder(logRole, decoderId);
    }

    public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(config);

        if (!IsSupported)
        {
            throw new InvalidOperationException("FFmpeg H.264 decoder backend is unavailable.");
        }

        var normalizedConfigData = config.DecoderConfigData ?? Array.Empty<byte>();
        if (activeConfig is not null &&
            activeConfig.StreamEpoch == config.StreamEpoch &&
            string.Equals(activeConfig.CodecProfile, config.CodecProfile, StringComparison.Ordinal) &&
            normalizedConfigData.AsSpan().SequenceEqual(activeConfig.DecoderConfigData))
        {
            return;
        }

        ResetCodecState();

        WindowsH264DecodePreparation.TryCreateDecoderConfiguration(normalizedConfigData, out configuration);
        activeConfig = config with
        {
            DecoderConfigData = normalizedConfigData.Length == 0
                ? Array.Empty<byte>()
                : (byte[])normalizedConfigData.Clone(),
        };
        prependDecoderConfigOnNextPacket = true;
        firstDecodeLogged = false;

        LogLifecycle(
            "screenshare_h264_decoder_configured",
            config.StreamEpoch,
            $"backend=ffmpeg_software; config_bytes={normalizedConfigData.Length}; expected_width={configuration?.ExpectedWidth ?? 0}; expected_height={configuration?.ExpectedHeight ?? 0}; nal_length_size={configuration?.NalLengthSize ?? 0}");
    }

    public Bitmap Decode(EncodedFrameDecodeRequest request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsSupported)
        {
            throw new InvalidOperationException("FFmpeg H.264 decoder backend is unavailable.");
        }

        if (activeConfig is null)
        {
            throw new InvalidOperationException("FFmpeg H.264 decoder has not been configured.");
        }

        if (request.StreamEpoch > 0 && activeConfig.StreamEpoch > 0 && request.StreamEpoch != activeConfig.StreamEpoch)
        {
            Reset();
            throw new InvalidOperationException("H.264 stream epoch changed without decoder reconfiguration.");
        }

        EnsureDecoderOpened();

        var annexBPacket = WindowsH264DecodePreparation.BuildAnnexBPacketForSoftwareDecode(
            request.EncodedFrameBytes,
            request.IsKeyFrame,
            configuration,
            prependDecoderConfigOnNextPacket);
        if (annexBPacket.Length == 0)
        {
            throw new InvalidOperationException("FFmpeg H.264 decoder received an empty packet after normalization.");
        }

        SendPacket(annexBPacket);
        prependDecoderConfigOnNextPacket = false;

        ReceiveDecodedBitmaps();
        if (pendingDecodedBitmaps.Count == 0)
        {
            throw new H264DecoderNeedsMoreInputException("FFmpeg H.264 decoder needs more input before it can produce a frame.");
        }

        var bitmap = pendingDecodedBitmaps.Dequeue();

        if (!firstDecodeLogged)
        {
            firstDecodeLogged = true;
            LogLifecycle(
                "screenshare_h264_decoder_first_frame_decoded",
                request.StreamEpoch,
                $"backend=ffmpeg_software; width={bitmap.PixelSize.Width}; height={bitmap.PixelSize.Height}; is_keyframe={(request.IsKeyFrame ? 1 : 0)}");
        }

        return bitmap;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (codecContext is not null)
        {
            ffmpeg.avcodec_flush_buffers(codecContext);
        }

        DisposePendingDecodedBitmaps();
        prependDecoderConfigOnNextPacket = true;
        firstDecodeLogged = false;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ResetCodecState();
        GC.SuppressFinalize(this);
    }

    private void EnsureDecoderOpened()
    {
        if (codecContext is not null)
        {
            return;
        }

        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec is null)
        {
            throw new InvalidOperationException("FFmpeg could not locate the H.264 decoder.");
        }

        codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (codecContext is null)
        {
            throw new InvalidOperationException("FFmpeg could not allocate an H.264 decoder context.");
        }

        codecContext->thread_count = 1;
        codecContext->thread_type = 0;
        codecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

        if (activeConfig?.DecoderConfigData is { Length: > 0 } extradataBytes)
        {
            var paddedSize = extradataBytes.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE;
            var extradata = (byte*)ffmpeg.av_mallocz((ulong)paddedSize);
            if (extradata is null)
            {
                throw new InvalidOperationException("FFmpeg could not allocate decoder extradata.");
            }

            Marshal.Copy(extradataBytes, 0, (IntPtr)extradata, extradataBytes.Length);
            codecContext->extradata = extradata;
            codecContext->extradata_size = extradataBytes.Length;
        }

        var openResult = ffmpeg.avcodec_open2(codecContext, codec, null);
        ThrowIfError(openResult, "open_codec");
    }

    private void SendPacket(byte[] packetBytes)
    {
        EnsureReusablePacketAllocated();
        if (reusablePacket is null)
        {
            throw new InvalidOperationException("FFmpeg could not allocate a packet.");
        }

        try
        {
            ffmpeg.av_packet_unref(reusablePacket);
            var packetResult = ffmpeg.av_new_packet(reusablePacket, packetBytes.Length);
            ThrowIfError(packetResult, "allocate_packet");
            Marshal.Copy(packetBytes, 0, (IntPtr)reusablePacket->data, packetBytes.Length);

            var sendResult = ffmpeg.avcodec_send_packet(codecContext, reusablePacket);
            ThrowIfError(sendResult, "send_packet");
        }
        finally
        {
            if (reusablePacket is not null)
            {
                ffmpeg.av_packet_unref(reusablePacket);
            }
        }
    }

    private void ReceiveDecodedBitmaps()
    {
        EnsureReusableFrameAllocated();
        if (reusableFrame is null)
        {
            throw new InvalidOperationException("FFmpeg could not allocate a frame.");
        }

        try
        {
            while (true)
            {
                var receiveResult = ffmpeg.avcodec_receive_frame(codecContext, reusableFrame);
                if (receiveResult == ffmpeg.AVERROR_EOF || receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    break;
                }

                ThrowIfError(receiveResult, "receive_frame");
                pendingDecodedBitmaps.Enqueue(CreateBitmapFromFrame(reusableFrame));
                ffmpeg.av_frame_unref(reusableFrame);
            }
        }
        finally
        {
            if (reusableFrame is not null)
            {
                ffmpeg.av_frame_unref(reusableFrame);
            }
        }
    }

    private Bitmap CreateBitmapFromFrame(AVFrame* frame)
    {
        var width = frame->width;
        var height = frame->height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("FFmpeg returned a frame without dimensions.");
        }

        swsContext = ffmpeg.sws_getCachedContext(
            swsContext,
            width,
            height,
            (AVPixelFormat)frame->format,
            width,
            height,
            AVPixelFormat.AV_PIX_FMT_BGRA,
            ffmpeg.SWS_BILINEAR,
            null,
            null,
            null);
        if (swsContext is null)
        {
            throw new InvalidOperationException("FFmpeg could not create a pixel conversion context.");
        }

        var stride = width * 4;
        var targetBytes = EnsureReusableBgraBuffer(width, height, stride);
        fixed (byte* targetPtr = targetBytes)
        {
            var dstData = new byte_ptrArray4();
            var dstLinesize = new int_array4();
            dstData[0] = targetPtr;
            dstLinesize[0] = stride;
            _ = ffmpeg.sws_scale(swsContext, frame->data, frame->linesize, 0, height, dstData, dstLinesize);
        }

        return CreateAvaloniaBitmapFromBgra(targetBytes, width, height, stride);
    }

    private static Bitmap CreateAvaloniaBitmapFromBgra(byte[] sourceBytes, int width, int height, int stride)
    {
        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Unpremul);

        using var framebuffer = writeable.Lock();
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(sourceBytes, y * stride, framebuffer.Address + (y * framebuffer.RowBytes), stride);
        }

        return writeable;
    }

    private void ResetCodecState()
    {
        DisposePendingDecodedBitmaps();
        reusableBgraBuffer = null;
        reusableBgraWidth = 0;
        reusableBgraHeight = 0;
        reusableBgraStride = 0;

        if (reusablePacket is not null)
        {
            var packet = reusablePacket;
            ffmpeg.av_packet_free(&packet);
            reusablePacket = null;
        }

        if (reusableFrame is not null)
        {
            var frame = reusableFrame;
            ffmpeg.av_frame_free(&frame);
            reusableFrame = null;
        }

        if (swsContext is not null)
        {
            ffmpeg.sws_freeContext(swsContext);
            swsContext = null;
        }

        if (codecContext is not null)
        {
            var context = codecContext;
            ffmpeg.avcodec_free_context(&context);
            codecContext = null;
        }

        prependDecoderConfigOnNextPacket = true;
    }

    private void DisposePendingDecodedBitmaps()
    {
        while (pendingDecodedBitmaps.Count > 0)
        {
            pendingDecodedBitmaps.Dequeue().Dispose();
        }
    }

    private void EnsureReusablePacketAllocated()
    {
        if (reusablePacket is not null)
        {
            return;
        }

        reusablePacket = ffmpeg.av_packet_alloc();
    }

    private void EnsureReusableFrameAllocated()
    {
        if (reusableFrame is not null)
        {
            return;
        }

        reusableFrame = ffmpeg.av_frame_alloc();
    }

    private byte[] EnsureReusableBgraBuffer(int width, int height, int stride)
    {
        if (reusableBgraBuffer is not null &&
            reusableBgraWidth == width &&
            reusableBgraHeight == height &&
            reusableBgraStride == stride)
        {
            return reusableBgraBuffer;
        }

        reusableBgraWidth = width;
        reusableBgraHeight = height;
        reusableBgraStride = stride;
        reusableBgraBuffer = new byte[stride * height];
        return reusableBgraBuffer;
    }

    private static void ThrowIfError(int errorCode, string stage)
    {
        if (errorCode >= 0)
        {
            return;
        }

        throw new InvalidOperationException($"FFmpeg H.264 decoder failed at {stage}: {GetErrorString(errorCode)} (0x{errorCode:X8}).");
    }

    private static string GetErrorString(int errorCode)
    {
        const int bufferSize = 1024;
        var buffer = stackalloc byte[bufferSize];
        var result = ffmpeg.av_strerror(errorCode, buffer, (ulong)bufferSize);
        if (result < 0)
        {
            return $"ffmpeg_error_{errorCode}";
        }

        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"ffmpeg_error_{errorCode}";
    }

    private void LogLifecycle(string eventName, long streamEpoch, string details)
    {
        LogLifecycle(eventName, details, logRole, decoderId, streamEpoch);
    }

    private static void LogLifecycle(string eventName, string details, string role, int decoderId, long streamEpoch)
    {
        LocalOperationalLog.Info("ScreenShareTransport", $"event={eventName}; role={Sanitize(role)}; decoder_id={decoderId}; stream_epoch={streamEpoch}; {details}");
        WriteDebugTrace($"[FfmpegH264BitmapDecoder] {eventName}: role={role} decoder_id={decoderId} stream_epoch={streamEpoch} {details}");
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        return value.Replace(';', ',').Trim();
    }

    [Conditional("DEBUG")]
    private static void WriteDebugTrace(string message)
    {
        Trace.WriteLine(message);
    }
}
