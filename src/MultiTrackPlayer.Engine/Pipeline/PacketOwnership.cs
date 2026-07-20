using Sdcb.FFmpeg.Raw;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace MultiTrackPlayer.Engine.Pipeline;

/// <summary>VideoPacketQueue / AudioPacketQueue が共有するパケット所有権ヘルパー。</summary>
internal static unsafe class PacketOwnership
{
    /// <summary>src の中身を新規確保したパケットへ move する。src 自体は空になり、呼び出し元が av_packet_unref してよい。</summary>
    public static AVPacket* AcquireCopy(AVPacket* src)
    {
        AVPacket* owned = av_packet_alloc();
        av_packet_move_ref(owned, src);
        return owned;
    }

    public static void Release(AVPacket* pkt)
    {
        if (pkt == null) return;
        AVPacket* p = pkt;
        av_packet_free(&p);
    }

    public static int SizeOf(AVPacket* pkt) => pkt != null ? Math.Max(1, pkt->size) : 1;
}
