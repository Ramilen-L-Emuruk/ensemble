namespace MultiTrackPlayer.Engine.Sync;

/// <summary>ハードウェア再生位置（mixer 出力サンプル軸のフレーム数）を提供する抽象。</summary>
public interface IPlaybackPositionSource
{
    long GetPositionFrames();
    void Reset();
}
